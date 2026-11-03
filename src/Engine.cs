// 文件拆分 / 合并核心逻辑（与界面无关，可单独测试）
// 设计要点：
//   * 纯字节级拷贝，不做任何压缩或转换 —— 拆分后拼接即为原文件，100% 无损。
//   * 拆分时为每个分块和整个文件计算 SHA-256，写入清单文件（*.fsplit.txt）。
//   * 合并时逐块校验 + 整体校验，全部一致才把结果改名为最终文件。
//   * 支持改名自动识别：清单按文件头魔数识别；改名/乱序的分块按精确字节大小 + SHA-256 自动匹配。
//   * 读 / 写 / 哈希采用双缓冲流水线，充分利用磁盘带宽。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FileSplitter
{
    public class PartInfo
    {
        public string Name;
        public long Size;
        public string Sha256; // 无清单模式下为 null
    }

    public class Manifest
    {
        public const string Magic = "# FileSplitter manifest v1";
        public const string Extension = ".fsplit.txt";

        public string OriginalName;
        public long OriginalSize;
        public string Sha256;      // 无清单模式下为 null
        public long PartSize;
        public List<PartInfo> Parts = new List<PartInfo>();

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Magic);
            sb.AppendLine("# 本文件记录了拆分信息和 SHA-256 校验值，合并时用于验证文件完整性。");
            sb.AppendLine("# 请与所有分块文件一起保存/上传，不要修改本文件内容。");
            sb.AppendLine("name=" + OriginalName);
            sb.AppendLine("size=" + OriginalSize.ToString(Inv));
            sb.AppendLine("sha256=" + Sha256);
            sb.AppendLine("partsize=" + PartSize.ToString(Inv));
            sb.AppendLine("parts=" + Parts.Count.ToString(Inv));
            for (int i = 0; i < Parts.Count; i++)
            {
                PartInfo p = Parts[i];
                sb.AppendLine("part." + (i + 1).ToString(Inv) + "=" + p.Name + "|" + p.Size.ToString(Inv) + "|" + p.Sha256);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static Manifest Load(string path)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0 || lines[0].Trim().TrimStart('\uFEFF') != Magic)
                throw new InvalidDataException("不是有效的拆分清单文件：" + path);

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var partLines = new Dictionary<int, string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1);
                if (key.StartsWith("part.", StringComparison.OrdinalIgnoreCase))
                    partLines[int.Parse(key.Substring(5), Inv)] = val;
                else
                    dict[key] = val;
            }

            var m = new Manifest();
            m.OriginalName = Get(dict, "name");
            m.OriginalSize = long.Parse(Get(dict, "size"), Inv);
            m.Sha256 = Get(dict, "sha256").Trim().ToLowerInvariant();
            m.PartSize = long.Parse(Get(dict, "partsize"), Inv);
            int count = int.Parse(Get(dict, "parts"), Inv);
            CheckFileName(m.OriginalName);

            for (int i = 1; i <= count; i++)
            {
                string v;
                if (!partLines.TryGetValue(i, out v))
                    throw new InvalidDataException("清单文件损坏：缺少第 " + i + " 块的记录。");
                string[] f = v.Split('|');
                if (f.Length != 3) throw new InvalidDataException("清单文件损坏：第 " + i + " 块记录格式错误。");
                var p = new PartInfo();
                p.Name = f[0];
                p.Size = long.Parse(f[1], Inv);
                p.Sha256 = f[2].Trim().ToLowerInvariant();
                CheckFileName(p.Name);
                m.Parts.Add(p);
            }
            return m;
        }

        static string Get(Dictionary<string, string> d, string key)
        {
            string v;
            if (!d.TryGetValue(key, out v)) throw new InvalidDataException("清单文件损坏：缺少字段 " + key);
            return v;
        }

        static void CheckFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..")
                throw new InvalidDataException("清单文件中包含非法文件名：" + name);
        }
    }

    /// <summary>合并 / 校验的输入：清单 + 分块所在目录。</summary>
    public class Resolved
    {
        public Manifest Manifest;
        public string Directory;
        public string ManifestPath; // 无清单时为 null
        public int RenamedCount;    // 原文件名不存在、但同目录存在同字节大小候选文件的分块数
        public int MissingCount;    // 同目录连同字节大小候选文件都不存在的分块数
        public bool HasManifest { get { return ManifestPath != null; } }
    }

    public class JobResult
    {
        public bool Ok;
        public string Message;
        public string ResultPath; // 用于“在资源管理器中显示”
        public JobResult(bool ok, string msg, string path) { Ok = ok; Message = msg; ResultPath = path; }
    }

    public delegate void ProgressHandler(long done, long total, string phase);

    public static class Engine
    {
        public const long MiB = 1024L * 1024L;
        const int BufferSize = 8 * 1024 * 1024;
        static readonly byte[] Empty = new byte[0];

        // 兼容正常分块名（a.mkv.001）以及浏览器/网盘自动重命名（a.mkv (1).001、a.mkv.001 (1) 等）
        static readonly Regex PartLooseRegex = new Regex(@"^(.+)\.(\d{3,})(?:\s*[\(\[（【]\d+[\)\]）】])?$", RegexOptions.Compiled);
        static readonly Regex CopySuffixRegex = new Regex(@"\s*[\(\[（【]\d+[\)\]）】]$", RegexOptions.Compiled);

        class PartOrderMismatchException : Exception { }

        // ------------------------------------------------------------------ 拆分
        public static JobResult Split(string src, string outDir, long partSize,
                                      ProgressHandler report, Action<string> log, CancellationToken ct)
        {
            if (partSize <= 0) throw new ArgumentException("分块大小必须大于 0。");
            var fi = new FileInfo(src);
            if (!fi.Exists) throw new FileNotFoundException("找不到源文件：" + src);
            Directory.CreateDirectory(outDir);

            long total = fi.Length;
            long countL = (total + partSize - 1) / partSize;
            if (countL > 99999) throw new ArgumentException("分块数量过多（" + countL + "），请调大分块大小。");
            int count = (int)countL;
            int digits = Math.Max(3, count.ToString(CultureInfo.InvariantCulture).Length);

            var m = new Manifest();
            m.OriginalName = fi.Name;
            m.OriginalSize = total;
            m.PartSize = partSize;

            var created = new List<string>();
            byte[][] bufs = NewBuffers();
            bool ok = false;
            long done = 0;
            log(string.Format("源文件：{0}（{1}），拆分为 {2} 块", fi.FullName, FormatSize(total), count));
            try
            {
                using (HashAlgorithm whole = NewHasher())
                using (FileStream input = OpenRead(src))
                {
                    for (int i = 1; i <= count; i++)
                    {
                        string partName = fi.Name + "." + i.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
                        string partPath = Path.Combine(outDir, partName);
                        long size = Math.Min(partSize, total - done);
                        string phase = string.Format("拆分中：第 {0}/{1} 块", i, count);

                        using (HashAlgorithm ph = NewHasher())
                        {
                            created.Add(partPath);
                            using (FileStream output = OpenWrite(partPath))
                            {
                                output.SetLength(size); // 预分配，空间不足会立即报错
                                Pump(input, size, bufs,
                                    (b, len) => Parallel.Invoke(
                                        () => whole.TransformBlock(b, 0, len, null, 0),
                                        () => ph.TransformBlock(b, 0, len, null, 0),
                                        () => output.Write(b, 0, len)),
                                    len => { done += len; report(done, total, phase); },
                                    ct, "源文件");
                                output.Flush(true);
                            }
                            ph.TransformFinalBlock(Empty, 0, 0);
                            var p = new PartInfo();
                            p.Name = partName;
                            p.Size = size;
                            p.Sha256 = Hex(ph.Hash);
                            m.Parts.Add(p);
                            log(string.Format("  ✓ {0}  {1}", partName, FormatSize(size)));
                        }
                    }
                    if (input.ReadByte() != -1)
                        throw new IOException("源文件在拆分过程中被修改（变大了），请关闭占用该文件的程序后重试。");
                    whole.TransformFinalBlock(Empty, 0, 0);
                    m.Sha256 = Hex(whole.Hash);
                }

                string mp = Path.Combine(outDir, fi.Name + Manifest.Extension);
                created.Add(mp);
                m.Save(mp);
                log("  ✓ 清单文件 " + Path.GetFileName(mp));
                log("  原文件 SHA-256：" + m.Sha256);
                ok = true;
                return new JobResult(true, string.Format(
                    "拆分完成！\r\n\r\n共生成 {0} 个分块 + 1 个清单文件（{1}），位于：\r\n{2}\r\n\r\n" +
                    "请把这 {3} 个文件全部上传到云盘。日后下载回同一个文件夹，用本工具“合并”即可还原。",
                    count, Path.GetFileName(mp), outDir, count + 1), mp);
            }
            finally
            {
                if (!ok) foreach (string p in created) TryDelete(p);
            }
        }

        // ------------------------------------------------------------------ 清单魔数检测与智能解析
        /// <summary>按文件头魔数判断是否为拆分清单（即使文件名/扩展名被改过也能识别）。</summary>
        public static bool IsManifestFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < Manifest.Magic.Length || fi.Length > 10 * MiB) return false;
                byte[] buf = new byte[64];
                int n;
                using (FileStream fs = OpenRead(path)) n = fs.Read(buf, 0, buf.Length);
                string s = Encoding.UTF8.GetString(buf, 0, n).TrimStart('\uFEFF');
                return s.StartsWith(Manifest.Magic, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>供拖放自动分流判断：该文件是否像是待合并的清单或分块（含被改名的情况）。</summary>
        public static bool LooksLikeMergeInput(string path)
        {
            try
            {
                if (IsManifestFile(path)) return true;
                string name = Path.GetFileName(path);
                if (name.EndsWith(Manifest.Extension, StringComparison.OrdinalIgnoreCase)) return true;
                if (PartLooseRegex.IsMatch(name)) return true;

                // 若文件名完全不含 .001 后缀，检查同目录是否有清单正在寻找该精确字节大小的分块
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                long len = new FileInfo(path).Length;
                foreach (KeyValuePair<string, Manifest> kv in ScanManifestsInDir(dir))
                {
                    Manifest m = kv.Value;
                    foreach (PartInfo p in m.Parts)
                    {
                        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
                        if (p.Size == len && p.Size < m.OriginalSize)
                        {
                            string orig = Path.Combine(dir, p.Name);
                            if (!File.Exists(orig) || new FileInfo(orig).Length != p.Size) return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        static List<KeyValuePair<string, Manifest>> ScanManifestsInDir(string dir)
        {
            var list = new List<KeyValuePair<string, Manifest>>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
            foreach (string f in Directory.GetFiles(dir))
            {
                if (!IsManifestFile(f)) continue;
                try { list.Add(new KeyValuePair<string, Manifest>(f, Manifest.Load(f))); } catch { }
            }
            return list;
        }

        static string StripCopySuffix(string s)
        {
            return CopySuffixRegex.Replace(s, "");
        }

        /// <summary>传入清单文件或任意一个分块文件（即使已被重命名），找到全部分块信息。</summary>
        public static Resolved Resolve(string path)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("找不到文件：" + path);

            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            var r = new Resolved();
            r.Directory = dir;

            // 1. 本身就是清单文件（通过文件头魔数或后缀判断）
            if (IsManifestFile(path) || name.EndsWith(Manifest.Extension, StringComparison.OrdinalIgnoreCase))
            {
                r.Manifest = Manifest.Load(path);
                r.ManifestPath = path;
                PreviewPartCounts(r);
                return r;
            }

            // 2. 尝试在同目录查找匹配的清单文件（支持清单或分块被改名）
            Match mt = PartLooseRegex.Match(name);
            if (mt.Success)
            {
                string rawBase = mt.Groups[1].Value;
                foreach (string b in new[] { rawBase, StripCopySuffix(rawBase) })
                {
                    string mp = Path.Combine(dir, b + Manifest.Extension);
                    if (File.Exists(mp) && IsManifestFile(mp))
                    {
                        r.Manifest = Manifest.Load(mp);
                        r.ManifestPath = mp;
                        PreviewPartCounts(r);
                        return r;
                    }
                }
            }

            long inputLen = new FileInfo(path).Length;
            KeyValuePair<string, Manifest>? best = null;
            int bestScore = 0;
            foreach (KeyValuePair<string, Manifest> kv in ScanManifestsInDir(dir))
            {
                Manifest cm = kv.Value;
                int score = 0;
                foreach (PartInfo p in cm.Parts)
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { score = 3; break; }
                    if (p.Size == inputLen) score = Math.Max(score, 2);
                }
                // 若目录下只有一个清单且用户选了分块前缀相关的文件，也允许关联
                if (score == 0 && mt.Success &&
                    string.Equals(StripCopySuffix(mt.Groups[1].Value), cm.OriginalName, StringComparison.OrdinalIgnoreCase))
                    score = 1;

                if (score > bestScore) { bestScore = score; best = kv; }
            }
            if (best.HasValue)
            {
                r.Manifest = best.Value.Value;
                r.ManifestPath = best.Value.Key;
                PreviewPartCounts(r);
                return r;
            }

            // 3. 没有清单：按 .001, .002 ... 顺序查找（兼容浏览器 (1) 后缀，无法做哈希校验）
            if (!mt.Success)
                throw new InvalidDataException("无法识别该文件：未找到拆分清单（*" + Manifest.Extension + "），且文件名不是分块格式（*.001）。");

            string canonBase = StripCopySuffix(mt.Groups[1].Value);
            int width = mt.Groups[2].Value.Length;
            FileInfo[] allFiles = new DirectoryInfo(dir).GetFiles();

            var m = new Manifest();
            m.OriginalName = canonBase;
            long sum = 0;
            for (int i = 1; ; i++)
            {
                string exactName = canonBase + "." + i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
                string exactPath = Path.Combine(dir, exactName);
                FileInfo chosen = null;
                if (File.Exists(exactPath))
                {
                    chosen = new FileInfo(exactPath);
                }
                else
                {
                    // 查找去掉 (1) 副本标记后同前缀、同序号的唯一候选文件
                    var cands = new List<FileInfo>();
                    foreach (FileInfo fi in allFiles)
                    {
                        Match pm = PartLooseRegex.Match(fi.Name);
                        int num;
                        if (pm.Success && pm.Groups[2].Value.Length == width &&
                            int.TryParse(pm.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out num) &&
                            num == i &&
                            string.Equals(StripCopySuffix(pm.Groups[1].Value), canonBase, StringComparison.OrdinalIgnoreCase))
                        {
                            cands.Add(fi);
                        }
                    }
                    if (cands.Count == 1) chosen = cands[0];
                }
                if (chosen == null) break;

                var p = new PartInfo();
                p.Name = chosen.Name;
                p.Size = chosen.Length;
                m.Parts.Add(p);
                sum += p.Size;
            }
            if (m.Parts.Count == 0)
                throw new FileNotFoundException("找不到第一个分块：" + canonBase + "." + "1".PadLeft(width, '0'));
            m.OriginalSize = sum;
            r.Manifest = m;
            return r;
        }

        /// <summary>仅利用文件大小元数据快速统计正常/改名/缺失数量（不读文件内容，供界面即时预览）。</summary>
        static void PreviewPartCounts(Resolved r)
        {
            r.RenamedCount = 0;
            r.MissingCount = 0;
            if (!r.HasManifest || !Directory.Exists(r.Directory)) return;

            FileInfo[] files = new DirectoryInfo(r.Directory).GetFiles();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (r.ManifestPath != null) used.Add(Path.GetFullPath(r.ManifestPath));

            var unmatched = new List<PartInfo>();
            foreach (PartInfo p in r.Manifest.Parts)
            {
                string exact = Path.GetFullPath(Path.Combine(r.Directory, p.Name));
                FileInfo found = null;
                foreach (FileInfo f in files)
                {
                    if (!used.Contains(f.FullName) && FullPathEquals(f.FullName, exact) && f.Length == p.Size)
                    {
                        found = f;
                        break;
                    }
                }
                if (found != null) used.Add(found.FullName);
                else unmatched.Add(p);
            }
            if (unmatched.Count == 0) return;

            var sizePool = new Dictionary<long, int>();
            foreach (FileInfo f in files)
            {
                if (used.Contains(f.FullName) || f.Name.EndsWith(".merging", StringComparison.OrdinalIgnoreCase)) continue;
                int c;
                sizePool.TryGetValue(f.Length, out c);
                sizePool[f.Length] = c + 1;
            }
            foreach (PartInfo p in unmatched)
            {
                int c;
                if (sizePool.TryGetValue(p.Size, out c) && c > 0)
                {
                    sizePool[p.Size] = c - 1;
                    r.RenamedCount++;
                }
                else
                {
                    r.MissingCount++;
                }
            }
        }

        // ------------------------------------------------------------------ 合并 / 校验
        /// <param name="outPath">输出文件路径；为 null 表示“仅校验”，不写任何文件。</param>
        public static JobResult MergeOrVerify(Resolved r, string outPath, bool deletePartsAfter,
                                              ProgressHandler report, Action<string> log, CancellationToken ct)
        {
            if (r.HasManifest)
            {
                try
                {
                    return MergeOrVerifyCore(r, outPath, deletePartsAfter, false, report, log, ct);
                }
                catch (PartOrderMismatchException)
                {
                    log("  ! 检测到同名/改名分块顺序异常或存在备用副本，正在按 SHA-256 全量识别...");
                    return MergeOrVerifyCore(r, outPath, deletePartsAfter, true, report, log, ct);
                }
            }
            return MergeOrVerifyCore(r, outPath, deletePartsAfter, false, report, log, ct);
        }

        static JobResult MergeOrVerifyCore(Resolved r, string outPath, bool deletePartsAfter, bool forceHashMatch,
                                           ProgressHandler report, Action<string> log, CancellationToken ct)
        {
            Manifest m = r.Manifest;
            bool verify = outPath == null;
            if (verify && !r.HasManifest)
                throw new InvalidOperationException("没有找到清单文件（*" + Manifest.Extension + "），无法校验。");

            long total = 0;
            foreach (PartInfo p in m.Parts) total += p.Size;
            if (total != m.OriginalSize)
                throw new InvalidDataException("清单文件损坏：分块大小之和与原文件大小不一致。");

            byte[][] bufs = NewBuffers();
            string[] actualPaths = new string[m.Parts.Count];
            bool[] renamed = new bool[m.Parts.Count];
            string[] rejectedSameSize = new string[m.Parts.Count];
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FileInfo[] dirFiles = new DirectoryInfo(r.Directory).GetFiles();

            long preBytes = 0;
            long grandDone = 0;

            if (!r.HasManifest)
            {
                for (int i = 0; i < m.Parts.Count; i++)
                {
                    string pp = Path.Combine(r.Directory, m.Parts[i].Name);
                    if (File.Exists(pp) && new FileInfo(pp).Length == m.Parts[i].Size)
                        actualPaths[i] = pp;
                }
            }
            else
            {
                usedPaths.Add(Path.GetFullPath(r.ManifestPath));

                // 1) 未强制内容哈希时，优先按原文件名 + 精确大小直接匹配（单遍零额外开销）
                if (!forceHashMatch)
                {
                    for (int i = 0; i < m.Parts.Count; i++)
                    {
                        string exact = Path.GetFullPath(Path.Combine(r.Directory, m.Parts[i].Name));
                        foreach (FileInfo fi in dirFiles)
                        {
                            if (!usedPaths.Contains(fi.FullName) && FullPathEquals(fi.FullName, exact) && fi.Length == m.Parts[i].Size)
                            {
                                actualPaths[i] = fi.FullName;
                                usedPaths.Add(fi.FullName);
                                break;
                            }
                        }
                    }
                }

                // 2) 对按原名未找到的分块，按精确字节大小分组匹配改名文件
                var unmatchedBySize = new Dictionary<long, List<int>>();
                for (int i = 0; i < m.Parts.Count; i++)
                {
                    if (actualPaths[i] != null) continue;
                    List<int> idxs;
                    if (!unmatchedBySize.TryGetValue(m.Parts[i].Size, out idxs))
                    {
                        idxs = new List<int>();
                        unmatchedBySize[m.Parts[i].Size] = idxs;
                    }
                    idxs.Add(i);
                }

                var hashGroups = new List< Tuple<List<int>, List<FileInfo>> >();
                foreach (KeyValuePair<long, List<int>> kv in unmatchedBySize)
                {
                    long sz = kv.Key;
                    List<int> idxs = kv.Value;
                    var cands = new List<FileInfo>();
                    FileInfo outFileCand = null;
                    foreach (FileInfo fi in dirFiles)
                    {
                        if (usedPaths.Contains(fi.FullName) || fi.Name.EndsWith(".merging", StringComparison.OrdinalIgnoreCase)) continue;
                        if (fi.Length != sz) continue;
                        if (outPath != null && FullPathEquals(fi.FullName, outPath)) outFileCand = fi;
                        else cands.Add(fi);
                    }
                    if (outFileCand != null) cands.Add(outFileCand);

                    if (cands.Count == 0) continue;

                    if (!forceHashMatch && idxs.Count == 1 && cands.Count == 1)
                    {
                        // 唯一同大小文件：无需预读，合并流中边写边算 SHA-256 校验即可
                        int idx = idxs[0];
                        actualPaths[idx] = cands[0].FullName;
                        renamed[idx] = !string.Equals(cands[0].Name, m.Parts[idx].Name, StringComparison.OrdinalIgnoreCase);
                        usedPaths.Add(cands[0].FullName);
                    }
                    else if (!forceHashMatch && idxs.Count > 1 && cands.Count == idxs.Count &&
                             TryMatchByPartNumber(idxs, cands, actualPaths, renamed, m.Parts, usedPaths))
                    {
                        // 如统一改了前缀（新名.001、新名.002）：按分块序号单遍匹配，若被调换顺序会自动回退到 SHA-256 重排
                    }
                    else
                    {
                        hashGroups.Add(Tuple.Create(idxs, cands));
                        foreach (FileInfo cf in cands) preBytes += cf.Length;
                    }
                }

                // 3) 存在多个同大小改名文件（如完全改名为“甲”“乙”或顺序打乱）：计算 SHA-256 精确对号入座
                long grandTotalPre = preBytes + total;
                foreach (Tuple<List<int>, List<FileInfo>> grp in hashGroups)
                {
                    List<int> idxs = grp.Item1;
                    List<FileInfo> cands = grp.Item2;
                    var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (int idx in idxs) needed.Add(m.Parts[idx].Sha256);

                    var byHash = new Dictionary<string, Queue<FileInfo>>(StringComparer.OrdinalIgnoreCase);
                    var badNames = new List<string>();
                    foreach (FileInfo cf in cands)
                    {
                        string phase = "识别改名分块：" + cf.Name;
                        string h = ComputeFileSha256(cf.FullName, cf.Length, bufs,
                            len => { grandDone += len; report(grandDone, grandTotalPre, phase); }, ct);
                        if (needed.Contains(h))
                        {
                            Queue<FileInfo> q;
                            if (!byHash.TryGetValue(h, out q)) { q = new Queue<FileInfo>(); byHash[h] = q; }
                            q.Enqueue(cf);
                        }
                        else
                        {
                            badNames.Add(cf.Name);
                        }
                    }

                    foreach (int idx in idxs)
                    {
                        Queue<FileInfo> q;
                        if (byHash.TryGetValue(m.Parts[idx].Sha256, out q) && q.Count > 0)
                        {
                            FileInfo matched = q.Dequeue();
                            actualPaths[idx] = matched.FullName;
                            renamed[idx] = !string.Equals(matched.Name, m.Parts[idx].Name, StringComparison.OrdinalIgnoreCase);
                            usedPaths.Add(matched.FullName);
                        }
                        else if (badNames.Count > 0)
                        {
                            rejectedSameSize[idx] = string.Join("、", badNames);
                        }
                    }
                }
            }

            // 预检查汇总：缺失 / 大小不符 / 同大小但校验不符
            var problems = new List<string>();
            var hashProblems = new List<string>();
            var skip = new HashSet<int>();
            for (int i = 0; i < m.Parts.Count; i++)
            {
                if (actualPaths[i] != null) continue;
                skip.Add(i);
                PartInfo p = m.Parts[i];
                string exact = Path.Combine(r.Directory, p.Name);
                if (File.Exists(exact))
                {
                    long len = new FileInfo(exact).Length;
                    if (len != p.Size)
                    {
                        problems.Add(string.Format("第 {0} 块 {1}：大小不符（应为 {2:N0} 字节，实际 {3:N0} 字节，可能未下载完整）", i + 1, p.Name, p.Size, len));
                        continue;
                    }
                }
                if (rejectedSameSize[i] != null)
                {
                    string msg = string.Format("第 {0} 块 {1}：内容校验失败（候选文件 {2} 的 SHA-256 与清单不符）", i + 1, p.Name, rejectedSameSize[i]);
                    problems.Add(msg);
                    hashProblems.Add(msg);
                }
                else
                {
                    problems.Add(string.Format("第 {0} 块 {1}：文件缺失", i + 1, p.Name));
                }
            }

            if (problems.Count > 0 && !verify)
            {
                string errText = "以下分块缺失、不完整或已损坏，请重新下载后再合并：\r\n\r\n" + string.Join("\r\n", problems);
                if (hashProblems.Count == problems.Count) throw new InvalidDataException(errText);
                throw new IOException(errText);
            }
            foreach (string s in problems) log("  ✗ " + s);

            log(string.Format("{0}：{1}（{2}，{3} 块）{4}", verify ? "校验" : "合并", m.OriginalName,
                FormatSize(total), m.Parts.Count, r.HasManifest ? "" : "  [无清单，跳过哈希校验]"));

            var allPartHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (r.HasManifest) foreach (PartInfo p in m.Parts) if (p.Sha256 != null) allPartHashes.Add(p.Sha256);

            var bad = new List<string>(problems);
            string tmp = verify ? null : outPath + ".merging";
            long grandTotal = preBytes + total;
            bool ok = false;
            try
            {
                using (HashAlgorithm whole = NewHasher())
                using (FileStream output = verify ? null : OpenWrite(tmp))
                {
                    if (output != null) output.SetLength(total);
                    for (int i = 0; i < m.Parts.Count; i++)
                    {
                        PartInfo p = m.Parts[i];
                        if (skip.Contains(i)) { grandDone += p.Size; report(grandDone, grandTotal, ""); continue; }
                        string pp = actualPaths[i];
                        string actualName = Path.GetFileName(pp);
                        string phase = string.Format("{0}：第 {1}/{2} 块", verify ? "校验中" : "合并中", i + 1, m.Parts.Count);

                        using (HashAlgorithm ph = NewHasher())
                        using (FileStream input = OpenRead(pp))
                        {
                            Action<byte[], int> sink;
                            if (output == null)
                                sink = (b, len) => ph.TransformBlock(b, 0, len, null, 0);
                            else
                                sink = (b, len) => Parallel.Invoke(
                                    () => whole.TransformBlock(b, 0, len, null, 0),
                                    () => ph.TransformBlock(b, 0, len, null, 0),
                                    () => output.Write(b, 0, len));
                            Pump(input, p.Size, bufs, sink,
                                 len => { grandDone += len; report(grandDone, grandTotal, phase); }, ct, actualName);
                            ph.TransformFinalBlock(Empty, 0, 0);

                            string gotHash = Hex(ph.Hash);
                            if (p.Sha256 != null && gotHash != p.Sha256)
                            {
                                // 若该文件其实是另一块分块（文件名被调换），或同目录还有其他未被占用的同大小副本（如重下的 (1) 完好副本），自动全量重排重试
                                if (!forceHashMatch && (allPartHashes.Contains(gotHash) || HasUnusedFileOfSize(dirFiles, usedPaths, p.Size)))
                                    throw new PartOrderMismatchException();

                                string msg = renamed[i]
                                    ? string.Format("第 {0} 块 {1}（匹配到同大小文件 {2}）：内容校验失败，文件已损坏或不是该分块", i + 1, p.Name, actualName)
                                    : string.Format("第 {0} 块 {1}：内容校验失败（文件已损坏）", i + 1, p.Name);
                                if (!verify) throw new InvalidDataException(msg + "\r\n\r\n请从云盘重新下载该分块后再合并。");
                                bad.Add(msg);
                                log("  ✗ " + msg);
                            }
                            else
                            {
                                if (renamed[i])
                                    log(string.Format("  ✓ {0}  ← 已自动匹配改名文件：{1}{2}", p.Name, actualName, p.Sha256 != null ? "  校验通过" : ""));
                                else
                                    log(string.Format("  ✓ {0}{1}", actualName, p.Sha256 != null ? "  校验通过" : ""));
                            }
                        }
                    }

                    if (output != null)
                    {
                        output.Flush(true);
                        whole.TransformFinalBlock(Empty, 0, 0);
                        if (m.Sha256 != null && Hex(whole.Hash) != m.Sha256)
                            throw new InvalidDataException("合并结果的整体 SHA-256 与原文件不一致，已放弃输出。请检查分块文件。");
                    }
                }

                if (verify)
                {
                    ok = true;
                    if (bad.Count == 0)
                        return new JobResult(true, string.Format("全部 {0} 个分块校验通过，可以放心合并 / 上传。", m.Parts.Count), r.ManifestPath);
                    return new JobResult(false, string.Format("{0} 个分块中有 {1} 个存在问题：\r\n\r\n{2}\r\n\r\n请重新下载这些分块。",
                        m.Parts.Count, bad.Count, string.Join("\r\n", bad)), r.ManifestPath);
                }

                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(tmp, outPath);
                ok = true;
                log("  ✓ 输出：" + outPath);

                if (deletePartsAfter && r.HasManifest)
                {
                    foreach (string ap in actualPaths)
                    {
                        if (ap != null && !FullPathEquals(ap, outPath)) TryDelete(ap);
                    }
                    if (!FullPathEquals(r.ManifestPath, outPath)) TryDelete(r.ManifestPath);
                    log("  已删除分块文件和清单。");
                }

                string verdict = r.HasManifest
                    ? "所有分块及整体 SHA-256 校验一致，还原出的文件与原文件逐字节相同。"
                    : "注意：未找到清单文件，仅按顺序拼接，未做哈希校验。";
                if (r.HasManifest) log("  SHA-256：" + m.Sha256);
                return new JobResult(true, "合并完成！\r\n\r\n" + outPath + "\r\n\r\n" + verdict, outPath);
            }
            finally
            {
                if (!ok && tmp != null) TryDelete(tmp);
            }
        }

        static bool TryMatchByPartNumber(List<int> idxs, List<FileInfo> cands, string[] actualPaths, bool[] renamed,
                                         List<PartInfo> parts, HashSet<string> usedPaths)
        {
            var map = new Dictionary<int, FileInfo>();
            foreach (FileInfo fi in cands)
            {
                Match mt = PartLooseRegex.Match(fi.Name);
                int num;
                if (!mt.Success || !int.TryParse(mt.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out num))
                    return false;
                if (map.ContainsKey(num)) return false;
                map[num] = fi;
            }
            foreach (int idx in idxs)
            {
                if (!map.ContainsKey(idx + 1)) return false;
            }
            foreach (int idx in idxs)
            {
                FileInfo fi = map[idx + 1];
                actualPaths[idx] = fi.FullName;
                renamed[idx] = !string.Equals(fi.Name, parts[idx].Name, StringComparison.OrdinalIgnoreCase);
                usedPaths.Add(fi.FullName);
            }
            return true;
        }

        static bool HasUnusedFileOfSize(FileInfo[] dirFiles, HashSet<string> usedPaths, long size)
        {
            foreach (FileInfo fi in dirFiles)
            {
                if (!usedPaths.Contains(fi.FullName) && !fi.Name.EndsWith(".merging", StringComparison.OrdinalIgnoreCase) && fi.Length == size)
                    return true;
            }
            return false;
        }

        static string ComputeFileSha256(string path, long size, byte[][] bufs, Action<int> advanced, CancellationToken ct)
        {
            using (HashAlgorithm h = NewHasher())
            using (FileStream fs = OpenRead(path))
            {
                Pump(fs, size, bufs, (b, len) => h.TransformBlock(b, 0, len, null, 0), advanced, ct, Path.GetFileName(path));
                h.TransformFinalBlock(Empty, 0, 0);
                return Hex(h.Hash);
            }
        }

        static bool FullPathEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ 工具函数
        /// <summary>
        /// 从 input 精确读取 length 字节，每块交给 sink 处理；
        /// 处理当前块的同时读取下一块（双缓冲流水线）。
        /// </summary>
        static void Pump(Stream input, long length, byte[][] bufs, Action<byte[], int> sink,
                         Action<int> advanced, CancellationToken ct, string what)
        {
            long remaining = length;
            int cur = 0;
            int n = ReadBlock(input, bufs[cur], remaining, what);
            while (n > 0)
            {
                ct.ThrowIfCancellationRequested();
                byte[] b = bufs[cur];
                int len = n;
                Task t = Task.Run(() => sink(b, len));
                remaining -= len;
                cur ^= 1;

                int next = 0;
                ExceptionDispatchInfo readErr = null;
                try { next = ReadBlock(input, bufs[cur], remaining, what); }
                catch (Exception ex) { readErr = ExceptionDispatchInfo.Capture(ex); }

                try { t.Wait(); }
                catch (AggregateException ae) { ExceptionDispatchInfo.Capture(Unwrap(ae)).Throw(); }
                if (readErr != null) readErr.Throw();

                advanced(len);
                n = next;
            }
        }

        static int ReadBlock(Stream s, byte[] buf, long remaining, string what)
        {
            if (remaining <= 0) return 0;
            int want = (int)Math.Min(buf.Length, remaining);
            int got = 0;
            while (got < want)
            {
                int r = s.Read(buf, got, want - got);
                if (r <= 0) break;
                got += r;
            }
            if (got != want)
                throw new EndOfStreamException(what + " 比预期的短，文件可能不完整或在处理过程中被修改。");
            return got;
        }

        public static Exception Unwrap(Exception ex)
        {
            while (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        static byte[][] NewBuffers() { return new[] { new byte[BufferSize], new byte[BufferSize] }; }

        static HashAlgorithm NewHasher() { return new SHA256CryptoServiceProvider(); }

        static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        }

        static FileStream OpenWrite(string path)
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
        }

        static string Hex(byte[] h) { return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant(); }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static string FormatSize(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return i == 0 ? b.ToString("0") + " B" : b.ToString("0.00") + " " + u[i];
        }
    }
}
