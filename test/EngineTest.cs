// 核心逻辑自动化测试（控制台程序）：拆分 -> 校验 -> 合并 -> 逐字节比较，以及损坏/缺失/改名/乱序检测
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using FileSplitter;

static class EngineTest
{
    static int fails = 0;

    static void Check(bool cond, string what)
    {
        Console.WriteLine((cond ? "[PASS] " : "[FAIL] ") + what);
        if (!cond) fails++;
    }

    static string Sha(string p)
    {
        using (var s = File.OpenRead(p)) using (var h = SHA256.Create())
            return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
    }

    static void MakeRandom(string path, long size)
    {
        var rnd = new Random(12345);
        var buf = new byte[1 << 20];
        using (var f = File.Create(path))
        {
            long left = size;
            while (left > 0) { rnd.NextBytes(buf); int n = (int)Math.Min(buf.Length, left); f.Write(buf, 0, n); left -= n; }
        }
    }

    static void Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "fsplit_test");
        long partSize = args.Length > 1 ? long.Parse(args[1]) : 10 * Engine.MiB;
        long[] sizes = args.Length > 2 ? args.Skip(2).Select(long.Parse).ToArray()
                                       : new[] { 25 * Engine.MiB + 123, 20 * Engine.MiB, 10 * Engine.MiB + 1 };
        ProgressHandler rep = (d, t, p) => { };
        Action<string> log = s => Console.WriteLine("    " + s);
        var ct = CancellationToken.None;

        foreach (long size in sizes)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            string src = Path.Combine(root, "src", "测试 文件=1.bin");
            string parts = Path.Combine(root, "parts");
            string outDir = Path.Combine(root, "out");
            Directory.CreateDirectory(Path.GetDirectoryName(src));
            Directory.CreateDirectory(outDir);
            Console.WriteLine("\n===== size=" + size + " partSize=" + partSize + " =====");
            MakeRandom(src, size);
            string srcSha = Sha(src);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            JobResult jr = Engine.Split(src, parts, partSize, rep, log, ct);
            Console.WriteLine("    split time " + sw.Elapsed);
            long expected = (size + partSize - 1) / partSize;
            Check(Directory.GetFiles(parts).Length == expected + 1, "part count + manifest = " + (expected + 1));

            // 用 .002 作为入口解析
            Resolved r = Engine.Resolve(Path.Combine(parts, "测试 文件=1.bin.002"));
            Check(r.HasManifest && r.Manifest.Parts.Count == expected && r.RenamedCount == 0 && r.MissingCount == 0,
                  "resolve via .002 finds manifest");
            Check(Engine.MergeOrVerify(r, null, false, rep, log, ct).Ok, "verify ok");

            sw.Restart();
            string outPath = Path.Combine(outDir, r.Manifest.OriginalName);
            Check(Engine.MergeOrVerify(r, outPath, false, rep, log, ct).Ok, "merge ok");
            Console.WriteLine("    merge time " + sw.Elapsed);
            Check(Sha(outPath) == srcSha, "merged file identical to source");

            // 损坏检测 + 同目录存在完好重下副本 .002 (1) 时自动恢复
            string p2 = Path.Combine(parts, r.Manifest.Parts[1].Name);
            string p2GoodCopy = p2 + " (1)";
            File.Copy(p2, p2GoodCopy);
            using (var f = new FileStream(p2, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Position = f.Length / 2; int b = f.ReadByte(); f.Position--; f.WriteByte((byte)(b ^ 1));
            }
            File.Delete(outPath);
            Check(Engine.MergeOrVerify(Engine.Resolve(jr.ResultPath), outPath, false, rep, log, ct).Ok && Sha(outPath) == srcSha,
                  "auto-recovers using intact .002 (1) when .002 is corrupted");
            File.Delete(p2GoodCopy);

            // 损坏检测（无完好副本时必须拒绝输出）
            JobResult v = Engine.MergeOrVerify(Engine.Resolve(jr.ResultPath), null, false, rep, log, ct);
            Check(!v.Ok && v.Message.Contains(r.Manifest.Parts[1].Name), "verify detects corrupted part 2");
            File.Delete(outPath);
            bool threw = false;
            try { Engine.MergeOrVerify(Engine.Resolve(jr.ResultPath), outPath, false, rep, log, ct); }
            catch (InvalidDataException) { threw = true; }
            Check(threw && !File.Exists(outPath) && !File.Exists(outPath + ".merging"), "merge refuses corrupted part and leaves no output");

            // 缺失检测
            File.Delete(p2);
            Check(Engine.Resolve(jr.ResultPath).MissingCount == 1, "preview detects 1 missing part");
            threw = false;
            try { Engine.MergeOrVerify(Engine.Resolve(jr.ResultPath), outPath, false, rep, log, ct); }
            catch (IOException) { threw = true; }
            Check(threw, "merge refuses when a part is missing");

            // 改名测试 1：若前两块同大小（>=20MB），把 .001 与 .002 文件名对调，验证自动重排
            if (Directory.Exists(parts)) Directory.Delete(parts, true);
            jr = Engine.Split(src, parts, partSize, rep, log, ct);
            if (r.Manifest.Parts[0].Size == r.Manifest.Parts[1].Size)
            {
                string f1 = Path.Combine(parts, r.Manifest.Parts[0].Name);
                string f2 = Path.Combine(parts, r.Manifest.Parts[1].Name);
                string swapTmp = Path.Combine(parts, "_swap.tmp");
                File.Move(f1, swapTmp);
                File.Move(f2, f1);
                File.Move(swapTmp, f2);
                File.Delete(outPath);
                Check(Engine.MergeOrVerify(Engine.Resolve(f1), outPath, false, rep, log, ct).Ok && Sha(outPath) == srcSha,
                      "swapped .001/.002 filenames auto-reordered by SHA-256");
            }

            // 改名测试 2：所有分块与清单全部改成毫不相干的名字（连扩展名都改掉），且合并后自动清理分块
            if (Directory.Exists(parts)) Directory.Delete(parts, true);
            jr = Engine.Split(src, parts, partSize, rep, log, ct);
            string renamedManifest = Path.Combine(parts, "随便改名的清单.dat");
            File.Move(jr.ResultPath, renamedManifest);
            string[] weirdNames = { "分块_Z.unknown", "分块_A", "分块_M (1).bak" };
            for (int i = 0; i < r.Manifest.Parts.Count; i++)
                File.Move(Path.Combine(parts, r.Manifest.Parts[i].Name), Path.Combine(parts, weirdNames[i]));

            string entryPart = Path.Combine(parts, weirdNames[1]);
            Check(Engine.LooksLikeMergeInput(renamedManifest) && Engine.LooksLikeMergeInput(entryPart),
                  "LooksLikeMergeInput recognizes arbitrarily renamed manifest & part");
            Resolved rr = Engine.Resolve(entryPart);
            Check(rr.HasManifest && rr.RenamedCount == expected && rr.MissingCount == 0,
                  "resolve via arbitrarily renamed part finds renamed manifest");
            string inPlaceOut = Path.Combine(parts, rr.Manifest.OriginalName);
            Check(Engine.MergeOrVerify(rr, inPlaceOut, true, rep, log, ct).Ok && Sha(inPlaceOut) == srcSha,
                  "merge arbitrarily renamed parts + deletePartsAfter succeeds");
            Check(Directory.GetFiles(parts).Length == 1 && File.Exists(inPlaceOut),
                  "deletePartsAfter cleaned up all renamed parts and renamed manifest");

            // 无清单模式 + 浏览器 (1) 后缀重命名：删除清单，改名分块后合并
            if (Directory.Exists(parts)) Directory.Delete(parts, true);
            jr = Engine.Split(src, parts, partSize, rep, log, ct);
            File.Delete(jr.ResultPath);
            string rawP1 = Path.Combine(parts, "测试 文件=1.bin (1).001");
            string rawP2 = Path.Combine(parts, "测试 文件=1.bin.002 (1)");
            File.Move(Path.Combine(parts, "测试 文件=1.bin.001"), rawP1);
            File.Move(Path.Combine(parts, "测试 文件=1.bin.002"), rawP2);
            Resolved raw = Engine.Resolve(rawP1);
            Check(!raw.HasManifest && raw.Manifest.Parts.Count == expected && raw.Manifest.OriginalName == "测试 文件=1.bin",
                  "raw resolve handles browser (1) copy suffixes");
            File.Delete(outPath);
            Engine.MergeOrVerify(raw, outPath, false, rep, log, ct);
            Check(Sha(outPath) == srcSha, "raw merge with (1) suffixes identical");

            // 取消：确保不残留文件
            var cts = new CancellationTokenSource(); cts.Cancel();
            string cancelDir = Path.Combine(root, "cancel");
            threw = false;
            try { Engine.Split(src, cancelDir, partSize, rep, log, cts.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw && Directory.GetFiles(cancelDir).Length == 0, "cancel cleans partial outputs");
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Console.WriteLine(fails == 0 ? "\nALL TESTS PASSED" : "\n" + fails + " TEST(S) FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
