// 图形界面（WinForms，无需安装任何运行库，Windows 自带 .NET Framework 4.x 即可运行）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("文件拆分合并工具")]
[assembly: System.Reflection.AssemblyProduct("FileSplitter")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace FileSplitter
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    public class MainForm : Form
    {
        const int DefaultMB = 3800; // 3800 MiB = 3,984,588,800 字节，低于 4GB（无论按 10^9 还是 2^30 计算）
        const int MaxMB = 4095;

        TabButton tabSplit, tabMerge;
        Panel pageHost, tpSplit, tpMerge;
        TextBox txtSrc, txtSplitOut, txtSize, txtMergeIn, txtMergeOut, txtLog;
        Label lblSizeInfo, lblSplitPlan, lblMergeInfo, lblStatus;
        ThemedButton btnSplit, btnMerge, btnVerify, btnCancel, btnTheme;
        ThemedCheckBox chkDeleteParts;
        FlatProgress progress;

        readonly List<Label> hints = new List<Label>();
        Theme T = Theme.Light;
        ThemeMode themeMode;
        int mergeInfoKind; // 0=Accent 1=Warn 2=Danger

        CancellationTokenSource cts;
        bool busy, closeAfterJob;
        readonly Stopwatch sw = new Stopwatch();
        long lastUiMs;
        float simulatedDpi;

        public MainForm(string[] args)
        {
            // 高 DPI：关闭 WinForms 自动缩放（部分系统上只放大字体不放大布局），
            // 全部按 96 DPI 坐标布局，最后手动整体缩放。
            AutoScaleMode = AutoScaleMode.None;
            float scale = GetUiScale();
            float fontScale = simulatedDpi > 0 ? scale : 1F; // 字体按“磅”计，系统会自动随 DPI 放大
            Font = new Font("Microsoft YaHei UI", 9F * fontScale);
            Text = "文件拆分合并工具  ·  无损 · SHA-256 校验";
            ClientSize = new Size(660, 500);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- 页面容器 + 自绘标签页
            pageHost = new Panel();
            pageHost.Location = new Point(12, 47);
            pageHost.Size = new Size(636, 224);
            pageHost.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            pageHost.Paint += (s, e) =>
            {
                using (var pen = new Pen(T.Border)) e.Graphics.DrawRectangle(pen, 0, 0, pageHost.Width - 1, pageHost.Height - 1);
            };
            Controls.Add(pageHost);

            tpSplit = NewPage();
            tpMerge = NewPage();
            tpMerge.Visible = false;

            tabSplit = NewTab("拆分文件", 12, 112, tpSplit);
            tabMerge = NewTab("合并 / 校验", 124, 120, tpMerge);
            tabSplit.Selected = true;

            btnTheme = AddButton(this, "", 516, 12, 132, 30, AnchorStyles.Top | AnchorStyles.Right, OnThemeClick);

            BuildSplitTab();
            BuildMergeTab();

            // ---- 底部：进度 / 状态 / 日志
            progress = new FlatProgress();
            progress.Location = new Point(12, 284);
            progress.Size = new Size(636, 6);
            progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(progress);

            lblStatus = AddLabel(this, "就绪。可以把文件直接拖进窗口。", 12, 304);
            lblStatus.AutoSize = false;
            lblStatus.Size = new Size(544, 22);
            lblStatus.AutoEllipsis = true;
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            btnCancel = AddButton(this, "取消", 568, 300, 80, 28, AnchorStyles.Top | AnchorStyles.Right, OnCancel);
            btnCancel.Enabled = false;

            var logBox = new InputBox(true);
            logBox.Location = new Point(12, 338);
            logBox.Size = new Size(636, 150);
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog = logBox.Box;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.HandleCreated += delegate { Native.SetDarkScrollBars(txtLog.Handle, T.IsDark); };
            Controls.Add(logBox);

            if (Math.Abs(scale - 1F) > 0.01F)
            {
                Scale(new SizeF(scale, scale));
                ClientSize = new Size((int)Math.Round(660 * scale), (int)Math.Round(500 * scale));
            }
            // 对齐标签页与页面上边框（消除整数取整造成的 1px 偏差）
            tabSplit.Left = pageHost.Left;
            tabMerge.Left = tabSplit.Right;
            tabMerge.Top = tabSplit.Top;
            tabMerge.Height = tabSplit.Height;
            int hostBottom = pageHost.Bottom;
            pageHost.Top = tabSplit.Bottom - 1;
            pageHost.Height = hostBottom - pageHost.Top;

            EnableDrop(this);
            themeMode = Theme.LoadMode();
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            if (args != null && args.Length > 0 && File.Exists(args[0])) HandleFile(args[0]);
        }

        /// <summary>系统 DPI / 96。环境变量 FILESPLITTER_DPI 可模拟其他 DPI（测试用）。</summary>
        float GetUiScale()
        {
            float dpi = 96F;
            float sim;
            string ov = Environment.GetEnvironmentVariable("FILESPLITTER_DPI");
            if (ov != null && float.TryParse(ov, NumberStyles.Float, CultureInfo.InvariantCulture, out sim) && sim >= 48)
            {
                simulatedDpi = sim;
                return sim / 96F;
            }
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; } catch { }
            return dpi / 96F;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            MinimumSize = Size; // 缩放完成后再固定最小尺寸
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetDarkTitleBar(Handle, T.IsDark);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            base.OnFormClosed(e);
        }

        // ================================================================ 界面构建
        Panel NewPage()
        {
            var p = new Panel();
            p.Location = new Point(1, 1);
            p.Size = new Size(634, 222); // 先定尺寸再放控件，保证右锚定控件位置正确
            p.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            pageHost.Controls.Add(p);
            return p;
        }

        TabButton NewTab(string text, int x, int w, Panel page)
        {
            var t = new TabButton();
            t.Text = text;
            t.Location = new Point(x, 12);
            t.Size = new Size(w, 36); // 覆盖容器上边框 1px，与页面无缝衔接
            t.Click += delegate { if (!busy) SelectPage(page); };
            Controls.Add(t);
            t.BringToFront();
            return t;
        }

        void SelectPage(Panel page)
        {
            tpSplit.Visible = page == tpSplit;
            tpMerge.Visible = page == tpMerge;
            tabSplit.Selected = page == tpSplit;
            tabMerge.Selected = page == tpMerge;
        }

        /// <summary>供测试使用：0=拆分页，1=合并页。</summary>
        public void SelectTabIndex(int i) { SelectPage(i == 0 ? tpSplit : tpMerge); }

        void BuildSplitTab()
        {
            AddLabel(tpSplit, "源文件：", 12, 23);
            txtSrc = AddText(tpSplit, 96, 18, 430);
            txtSrc.TextChanged += delegate { UpdateSplitPlan(); };
            AddButton(tpSplit, "浏览...", 534, 18, 80, 26, AnchorStyles.Top | AnchorStyles.Right, delegate
            {
                using (var d = new OpenFileDialog())
                {
                    d.Title = "选择要拆分的文件";
                    if (d.ShowDialog(this) == DialogResult.OK) txtSrc.Text = d.FileName;
                }
            });

            AddLabel(tpSplit, "输出目录：", 12, 61);
            txtSplitOut = AddText(tpSplit, 96, 56, 430);
            AddButton(tpSplit, "浏览...", 534, 56, 80, 26, AnchorStyles.Top | AnchorStyles.Right, delegate { PickFolder(txtSplitOut); });
            AddHint(tpSplit, "留空 = 与源文件相同的目录", 96, 86);

            AddLabel(tpSplit, "每块大小：", 12, 115);
            txtSize = AddText(tpSplit, 96, 110, 90);
            txtSize.Parent.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            txtSize.Text = DefaultMB.ToString(CultureInfo.InvariantCulture);
            txtSize.MaxLength = 4;
            txtSize.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            txtSize.TextChanged += delegate { UpdateSizeInfo(); UpdateSplitPlan(); };
            AddLabel(tpSplit, "MB", 192, 115);
            lblSizeInfo = AddLabel(tpSplit, "", 222, 115);

            lblSplitPlan = AddLabel(tpSplit, "", 96, 146);

            btnSplit = AddButton(tpSplit, "开始拆分", 96, 176, 170, 36, AnchorStyles.Top | AnchorStyles.Left, OnSplit);
            btnSplit.Font = new Font(Font, FontStyle.Bold);
            btnSplit.Primary = true;
            AddHint(tpSplit, "小米云盘单文件上限 4GB，默认 3800MB 留有余量", 280, 186);
        }

        void BuildMergeTab()
        {
            AddLabel(tpMerge, "清单/分块：", 12, 23);
            txtMergeIn = AddText(tpMerge, 96, 18, 430);
            txtMergeIn.TextChanged += delegate { UpdateMergeInfo(); };
            AddButton(tpMerge, "浏览...", 534, 18, 80, 26, AnchorStyles.Top | AnchorStyles.Right, delegate
            {
                using (var d = new OpenFileDialog())
                {
                    d.Title = "选择拆分清单或任意分块文件（改名文件也可直接选择）";
                    d.Filter = "所有文件|*.*|拆分清单或分块|*" + Manifest.Extension + ";*.txt;*.001;*.0001";
                    if (d.ShowDialog(this) == DialogResult.OK) txtMergeIn.Text = d.FileName;
                }
            });

            AddLabel(tpMerge, "输出目录：", 12, 61);
            txtMergeOut = AddText(tpMerge, 96, 56, 430);
            AddButton(tpMerge, "浏览...", 534, 56, 80, 26, AnchorStyles.Top | AnchorStyles.Right, delegate { PickFolder(txtMergeOut); });
            AddHint(tpMerge, "留空 = 与分块文件相同的目录", 96, 86);

            lblMergeInfo = AddLabel(tpMerge, "", 96, 106);
            lblMergeInfo.AutoSize = false;
            lblMergeInfo.Size = new Size(518, 38);
            lblMergeInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            chkDeleteParts = new ThemedCheckBox();
            chkDeleteParts.Text = "合并且校验成功后，删除分块文件和清单";
            chkDeleteParts.Location = new Point(96, 146);
            chkDeleteParts.Size = new Size(518, 24);
            chkDeleteParts.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            tpMerge.Controls.Add(chkDeleteParts);

            btnVerify = AddButton(tpMerge, "仅校验分块", 96, 176, 130, 36, AnchorStyles.Top | AnchorStyles.Left, OnVerify);
            btnMerge = AddButton(tpMerge, "开始合并", 236, 176, 170, 36, AnchorStyles.Top | AnchorStyles.Left, OnMerge);
            btnMerge.Font = new Font(Font, FontStyle.Bold);
            btnMerge.Primary = true;
        }

        static Label AddLabel(Control parent, string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            parent.Controls.Add(l);
            return l;
        }

        void AddHint(Control parent, string text, int x, int y)
        {
            hints.Add(AddLabel(parent, text, x, y));
        }

        static TextBox AddText(Control parent, int x, int y, int w)
        {
            var ib = new InputBox(false);
            ib.Location = new Point(x, y);
            ib.Size = new Size(w, 26);
            ib.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            parent.Controls.Add(ib);
            return ib.Box;
        }

        static ThemedButton AddButton(Control parent, string text, int x, int y, int w, int h, AnchorStyles a, EventHandler click)
        {
            var b = new ThemedButton();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.Anchor = a;
            b.Click += click;
            parent.Controls.Add(b);
            return b;
        }

        void PickFolder(TextBox target)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "选择输出目录";
                if (Directory.Exists(target.Text)) d.SelectedPath = target.Text;
                if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
            }
        }

        // ================================================================ 主题
        void OnThemeClick(object sender, EventArgs e)
        {
            SetThemeMode((ThemeMode)(((int)themeMode + 1) % 3), true);
        }

        /// <summary>切换主题；persist=false 时不写入注册表（测试用）。</summary>
        public void SetThemeMode(ThemeMode mode, bool persist)
        {
            themeMode = mode;
            if (persist) Theme.SaveMode(mode);
            ApplyTheme();
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (themeMode != ThemeMode.System) return;
            try { BeginInvoke(new Action(ApplyTheme)); } catch { }
        }

        void ApplyTheme()
        {
            bool dark = themeMode == ThemeMode.Dark || (themeMode == ThemeMode.System && Theme.SystemPrefersDark());
            T = dark ? Theme.Dark : Theme.Light;

            SuspendLayout();
            BackColor = T.Back;
            ForeColor = T.Text;
            pageHost.BackColor = T.Surface;
            tpSplit.BackColor = tpMerge.BackColor = T.Surface;
            tpSplit.ForeColor = tpMerge.ForeColor = T.Text;
            ApplyTo(this);
            foreach (Label l in hints) l.ForeColor = T.SubText;
            lblStatus.ForeColor = T.Text;
            string[] names = { "跟随系统", "浅色", "深色" };
            btnTheme.Text = "主题：" + names[(int)themeMode];
            UpdateSizeInfo();
            UpdateSplitPlan();
            ApplyMergeInfoColor();
            ResumeLayout();

            if (IsHandleCreated) Native.SetDarkTitleBar(Handle, T.IsDark);
            Invalidate(true);
        }

        void ApplyTo(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                var tab = c as TabButton;
                var prog = c as FlatProgress;
                var input = c as InputBox;
                var btn = c as ThemedButton;
                var chk = c as ThemedCheckBox;
                if (tab != null) { tab.Theme = T; tab.Invalidate(); }
                else if (prog != null) { prog.Theme = T; prog.Invalidate(); }
                else if (input != null) { input.ApplyTheme(T); continue; }
                else if (btn != null) { btn.Theme = T; btn.Invalidate(); }
                else if (chk != null) { chk.Theme = T; chk.Invalidate(); }
                ApplyTo(c);
            }
        }

        // ================================================================ 拖放
        void EnableDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                e.Effect = (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0 && File.Exists(files[0])) HandleFile(files[0]);
            };
            foreach (Control child in c.Controls) EnableDrop(child);
        }

        void HandleFile(string path)
        {
            if (tpMerge.Visible || Engine.LooksLikeMergeInput(path))
            {
                SelectPage(tpMerge);
                txtMergeIn.Text = path;
            }
            else
            {
                SelectPage(tpSplit);
                txtSrc.Text = path;
            }
        }

        // ================================================================ 信息显示
        /// <summary>每块字节数；输入无效时返回 -1。</summary>
        long PartSize
        {
            get
            {
                int mb;
                if (int.TryParse(txtSize.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out mb) && mb >= 1 && mb <= MaxMB)
                    return mb * Engine.MiB;
                return -1;
            }
        }

        void UpdateSizeInfo()
        {
            long b = PartSize;
            if (b < 0)
            {
                lblSizeInfo.Text = "请输入 1 – " + MaxMB + " 之间的整数";
                lblSizeInfo.ForeColor = T.Danger;
                return;
            }
            bool warn = b >= 4000000000L;
            lblSizeInfo.Text = string.Format("= {0:N0} 字节{1}", b, warn ? "  ⚠ 可能超过云盘 4GB 上限" : "");
            lblSizeInfo.ForeColor = warn ? T.Danger : T.SubText;
        }

        void UpdateSplitPlan()
        {
            lblSplitPlan.ForeColor = T.Accent;
            string p = txtSrc.Text.Trim().Trim('"');
            long ps = PartSize;
            if (p.Length == 0 || !File.Exists(p) || ps < 0) { lblSplitPlan.Text = ""; return; }
            long size = new FileInfo(p).Length;
            long n = (size + ps - 1) / ps;
            lblSplitPlan.Text = size <= ps
                ? string.Format("文件大小 {0}，不超过分块大小，无需拆分。", Engine.FormatSize(size))
                : string.Format("文件大小 {0}，将拆分为 {1} 个分块 + 1 个清单文件。", Engine.FormatSize(size), n);
        }

        void UpdateMergeInfo()
        {
            string p = txtMergeIn.Text.Trim().Trim('"');
            mergeInfoKind = 0;
            if (p.Length == 0 || !File.Exists(p)) { lblMergeInfo.Text = ""; ApplyMergeInfoColor(); return; }
            try
            {
                Resolved r = Engine.Resolve(p);
                string status;
                if (!r.HasManifest)
                {
                    status = "（⚠ 未找到清单，仅按顺序拼接，无法校验）";
                    mergeInfoKind = 1;
                }
                else if (r.MissingCount > 0)
                {
                    status = string.Format("（⚠ 缺少 {0} 个分块，请确认分块已全部放在同目录）", r.MissingCount);
                    mergeInfoKind = 2;
                }
                else if (r.RenamedCount > 0)
                {
                    status = string.Format("（有清单；检测到 {0} 个分块已改名，将按 SHA-256 自动匹配）", r.RenamedCount);
                    mergeInfoKind = 0;
                }
                else
                {
                    status = "（有清单，可做完整校验）";
                    mergeInfoKind = 0;
                }
                lblMergeInfo.Text = string.Format("原文件：{0}\r\n大小 {1}，共 {2} 个分块{3}",
                    r.Manifest.OriginalName, Engine.FormatSize(r.Manifest.OriginalSize), r.Manifest.Parts.Count, status);
            }
            catch (Exception ex)
            {
                lblMergeInfo.Text = ex.Message;
                mergeInfoKind = 2;
            }
            ApplyMergeInfoColor();
        }

        void ApplyMergeInfoColor()
        {
            lblMergeInfo.ForeColor = mergeInfoKind == 0 ? T.Accent : mergeInfoKind == 1 ? T.Warn : T.Danger;
        }

        // ================================================================ 操作
        void OnSplit(object sender, EventArgs e)
        {
            string src = txtSrc.Text.Trim().Trim('"');
            if (!File.Exists(src)) { Warn("请先选择要拆分的文件。"); return; }
            long partSize = PartSize;
            if (partSize < 0) { Warn("每块大小请输入 1 – " + MaxMB + " 之间的整数（单位 MB）。"); return; }
            long size = new FileInfo(src).Length;
            if (size <= partSize) { Info("该文件大小不超过分块大小，无需拆分，可直接上传。"); return; }

            string outDir = txtSplitOut.Text.Trim().Trim('"');
            if (outDir.Length == 0) outDir = Path.GetDirectoryName(Path.GetFullPath(src));

            string name = Path.GetFileName(src);
            if (File.Exists(Path.Combine(outDir, name + Manifest.Extension)) || File.Exists(Path.Combine(outDir, name + ".001")))
            {
                if (!Ask("输出目录中已存在同名的分块/清单文件，是否覆盖？")) return;
            }
            if (!CheckFreeSpace(outDir, size)) return;

            RunJob("拆分", (report, log, ct) => Engine.Split(src, outDir, partSize, report, log, ct));
        }

        void OnMerge(object sender, EventArgs e) { StartMergeOrVerify(false); }
        void OnVerify(object sender, EventArgs e) { StartMergeOrVerify(true); }

        void StartMergeOrVerify(bool verifyOnly)
        {
            string input = txtMergeIn.Text.Trim().Trim('"');
            if (!File.Exists(input)) { Warn("请先选择拆分清单（*" + Manifest.Extension + "）或第一个分块文件（*.001）。"); return; }
            Resolved r;
            try { r = Engine.Resolve(input); }
            catch (Exception ex) { Warn(ex.Message); return; }

            if (verifyOnly)
            {
                if (!r.HasManifest) { Warn("未找到清单文件（*" + Manifest.Extension + "），无法校验。\r\n请把清单文件和分块放在同一目录。"); return; }
                RunJob("校验", (report, log, ct) => Engine.MergeOrVerify(r, null, false, report, log, ct));
                return;
            }

            if (!r.HasManifest &&
                !Ask(string.Format("没有找到清单文件（{0}{1}）。\r\n\r\n将按顺序拼接找到的 {2} 个分块，但无法进行 SHA-256 校验，" +
                                   "也无法确认分块是否齐全。\r\n\r\n仍要继续吗？", r.Manifest.OriginalName, Manifest.Extension, r.Manifest.Parts.Count)))
                return;

            string outDir = txtMergeOut.Text.Trim().Trim('"');
            if (outDir.Length == 0) outDir = r.Directory;
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, r.Manifest.OriginalName);
            if (File.Exists(outPath) && !Ask("目标文件已存在：\r\n" + outPath + "\r\n\r\n是否覆盖？")) return;
            if (!CheckFreeSpace(outDir, r.Manifest.OriginalSize)) return;

            bool del = chkDeleteParts.Checked && r.HasManifest;
            RunJob("合并", (report, log, ct) => Engine.MergeOrVerify(r, outPath, del, report, log, ct));
        }

        bool CheckFreeSpace(string dir, long need)
        {
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)));
                if (d.AvailableFreeSpace < need)
                    return Ask(string.Format("磁盘空间可能不足：需要 {0}，可用 {1}。\r\n\r\n仍要继续吗？",
                        Engine.FormatSize(need), Engine.FormatSize(d.AvailableFreeSpace)));
            }
            catch { }
            return true;
        }

        // ================================================================ 后台任务
        delegate JobResult Job(ProgressHandler report, Action<string> log, CancellationToken ct);

        void RunJob(string title, Job job)
        {
            SetBusy(true);
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            progress.Value = 0;
            lastUiMs = -1000;
            sw.Restart();
            Log("==== " + title + " ====");

            var th = new Thread(() =>
            {
                JobResult result = null;
                Exception error = null;
                try { result = job(Report, s => BeginInvoke(new Action(() => Log(s))), token); }
                catch (Exception ex) { error = Engine.Unwrap(ex); }
                try { BeginInvoke(new Action(() => OnJobDone(title, result, error))); } catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        // 在后台线程调用，节流后更新界面
        void Report(long done, long total, string phase)
        {
            long now = sw.ElapsedMilliseconds;
            if (now - lastUiMs < 120 && done < total) return;
            lastUiMs = now;
            double sec = Math.Max(0.001, now / 1000.0);
            double speed = done / sec;
            string eta = speed > 0 ? FormatTime((total - done) / speed) : "--";
            int val = total > 0 ? (int)(done * 1000 / total) : 1000;
            string text = string.Format("{0}   {1:0.0}%   {2}/s   剩余 {3}", phase, val / 10.0, Engine.FormatSize(speed), eta);
            try
            {
                BeginInvoke(new Action(() =>
                {
                    progress.Value = val;
                    lblStatus.Text = text;
                }));
            }
            catch { }
        }

        void OnJobDone(string title, JobResult result, Exception error)
        {
            sw.Stop();
            SetBusy(false);
            string elapsed = FormatTime(sw.Elapsed.TotalSeconds);
            if (error is OperationCanceledException)
            {
                lblStatus.Text = title + "已取消。";
                Log("已取消，未完成的输出文件已清理。");
            }
            else if (error != null)
            {
                lblStatus.Text = title + "失败。";
                Log("✗ 失败：" + error.Message);
                if (!closeAfterJob) MessageBox.Show(this, error.Message, title + "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                progress.Value = 1000;
                lblStatus.Text = string.Format("{0}{1}，用时 {2}", title, result.Ok ? "完成" : "发现问题", elapsed);
                Log(string.Format("{0}{1}，用时 {2}", title, result.Ok ? "完成" : "发现问题", elapsed));
                if (!closeAfterJob)
                {
                    if (!result.Ok)
                        MessageBox.Show(this, result.Message, title + "结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else if (result.ResultPath != null && File.Exists(result.ResultPath))
                    {
                        if (MessageBox.Show(this, result.Message + "\r\n\r\n是否打开所在文件夹？", title + "完成",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            try { Process.Start("explorer.exe", "/select,\"" + result.ResultPath + "\""); } catch { }
                        }
                    }
                    else
                        MessageBox.Show(this, result.Message, title + "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                UpdateMergeInfo();
            }
            if (closeAfterJob) Close();
        }

        void OnCancel(object sender, EventArgs e)
        {
            if (cts != null) cts.Cancel();
            btnCancel.Enabled = false;
            lblStatus.Text = "正在取消...";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                if (!Ask("任务正在进行中，确定要取消并退出吗？")) { e.Cancel = true; return; }
                closeAfterJob = true;
                OnCancel(null, EventArgs.Empty);
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        void SetBusy(bool b)
        {
            busy = b;
            SetInteractive(pageHost, !b);
            tabSplit.Enabled = tabMerge.Enabled = !b;
            btnCancel.Enabled = b;
        }

        // 运行期间锁定输入（不直接禁用整个面板，避免标签在深色下出现浮雕效果）
        static void SetInteractive(Control parent, bool on)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is TextBox) ((TextBox)c).ReadOnly = !on;
                else if (c is ThemedButton || c is ThemedCheckBox) c.Enabled = on;
                SetInteractive(c, on);
            }
        }

        void Log(string s)
        {
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss  ") + s + "\r\n");
        }

        static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "--";
            var t = TimeSpan.FromSeconds(Math.Round(seconds));
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                     : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        bool Ask(string msg) { return MessageBox.Show(this, msg, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }
        void Warn(string msg) { MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        void Info(string msg) { MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
