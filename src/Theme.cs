// 主题（浅色 / 深色）与自绘控件
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FileSplitter
{
    public enum ThemeMode { System = 0, Light = 1, Dark = 2 }

    public class Theme
    {
        public bool IsDark;
        public Color Back, Surface, Input, Text, SubText, Border, Accent, AccentHover, AccentText,
                     ButtonBack, ButtonBorder, ButtonHover, ButtonDown, Track, Danger, Warn;

        public static readonly Theme Light = new Theme
        {
            IsDark = false,
            Back = Color.FromArgb(243, 243, 243),
            Surface = Color.FromArgb(251, 251, 251),
            Input = Color.White,
            Text = Color.FromArgb(28, 28, 28),
            SubText = Color.FromArgb(112, 112, 112),
            Border = Color.FromArgb(208, 208, 208),
            Accent = Color.FromArgb(0, 95, 184),
            AccentHover = Color.FromArgb(25, 117, 204),
            AccentText = Color.White,
            ButtonBack = Color.FromArgb(253, 253, 253),
            ButtonBorder = Color.FromArgb(200, 200, 200),
            ButtonHover = Color.FromArgb(236, 241, 247),
            ButtonDown = Color.FromArgb(222, 230, 240),
            Track = Color.FromArgb(222, 222, 222),
            Danger = Color.FromArgb(196, 43, 28),
            Warn = Color.FromArgb(196, 102, 0),
        };

        public static readonly Theme Dark = new Theme
        {
            IsDark = true,
            Back = Color.FromArgb(32, 32, 32),
            Surface = Color.FromArgb(43, 43, 43),
            Input = Color.FromArgb(26, 26, 26),
            Text = Color.FromArgb(234, 234, 234),
            SubText = Color.FromArgb(158, 158, 158),
            Border = Color.FromArgb(72, 72, 72),
            Accent = Color.FromArgb(76, 160, 255),
            AccentHover = Color.FromArgb(108, 178, 255),
            AccentText = Color.FromArgb(12, 12, 12),
            ButtonBack = Color.FromArgb(56, 56, 56),
            ButtonBorder = Color.FromArgb(84, 84, 84),
            ButtonHover = Color.FromArgb(70, 70, 70),
            ButtonDown = Color.FromArgb(48, 48, 48),
            Track = Color.FromArgb(62, 62, 62),
            Danger = Color.FromArgb(255, 120, 110),
            Warn = Color.FromArgb(255, 180, 70),
        };

        /// <summary>Windows“应用模式”是否为深色。</summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        const string SettingsKey = @"Software\FileSplitter";

        public static ThemeMode LoadMode()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    object v = k == null ? null : k.GetValue("Theme");
                    if (v is int && (int)v >= 0 && (int)v <= 2) return (ThemeMode)(int)v;
                }
            }
            catch { }
            return ThemeMode.System;
        }

        public static void SaveMode(ThemeMode m)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue("Theme", (int)m, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subApp, string idList);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>深色标题栏（Windows 10 1809+ / Windows 11）。</summary>
        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                    DwmSetWindowAttribute(hwnd, 19, ref v, 4);        // 旧版 Windows 10
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
            }
            catch { }
        }

        /// <summary>深色滚动条。</summary>
        public static void SetDarkScrollBars(IntPtr hwnd, bool dark)
        {
            try { SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }
    }

    static class ColorUtil
    {
        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>自绘按钮（避免系统按钮在深色禁用态出现白色浮雕字）。</summary>
    public class ThemedButton : Button
    {
        bool hover, down;
        public bool Primary;
        public Theme Theme = FileSplitter.Theme.Light;

        public ThemedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme;
            Graphics g = e.Graphics;
            Color bg, border, fg;
            if (!Enabled)
            {
                bg = t.Back;
                border = t.Border;
                fg = t.SubText;
            }
            else if (Primary)
            {
                bg = down ? ColorUtil.Blend(t.Accent, Color.Black, 0.16f) : (hover ? t.AccentHover : t.Accent);
                border = bg;
                fg = t.AccentText;
            }
            else
            {
                bg = down ? t.ButtonDown : (hover ? t.ButtonHover : t.ButtonBack);
                border = Focused ? t.Accent : t.ButtonBorder;
                fg = t.Text;
            }
            g.Clear(bg);
            using (var pen = new Pen(border))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>自绘复选框（随 DPI 自动放大勾选框，适配深色主题）。</summary>
    public class ThemedCheckBox : Control
    {
        bool isChecked, hover;
        public Theme Theme = FileSplitter.Theme.Light;
        public event EventHandler CheckedChanged;

        public ThemedCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            if (Enabled) { Focus(); Checked = !Checked; }
            base.OnClick(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme;
            Graphics g = e.Graphics;
            g.Clear(t.Surface);

            int box = Math.Max(14, (int)Math.Round(Font.Height * 0.82f));
            int by = (Height - box) / 2;
            var rect = new Rectangle(0, by, box - 1, box - 1);

            if (isChecked)
            {
                using (var b = new SolidBrush(Enabled ? t.Accent : t.SubText))
                    g.FillRectangle(b, 0, by, box, box);
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float pw = Math.Max(1.6f, box / 8f);
                using (var pen = new Pen(t.AccentText, pw))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box * 0.22f, by + box * 0.52f),
                        new PointF(box * 0.43f, by + box * 0.73f),
                        new PointF(box * 0.78f, by + box * 0.28f),
                    });
                }
                g.SmoothingMode = old;
            }
            else
            {
                using (var b = new SolidBrush(t.Input))
                    g.FillRectangle(b, 0, by, box, box);
                Color bc = (hover || Focused) && Enabled ? t.Accent : t.ButtonBorder;
                using (var pen = new Pen(bc))
                    g.DrawRectangle(pen, rect);
            }

            int gap = Math.Max(6, box / 3);
            var tr = new Rectangle(box + gap, 0, Width - box - gap, Height);
            TextRenderer.DrawText(g, Text, Font, tr, Enabled ? t.Text : t.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>扁平标签页按钮（替代系统 TabControl，后者无法换成深色）。</summary>
    public class TabButton : Control
    {
        bool selected, hover;
        public Theme Theme = FileSplitter.Theme.Light;

        public TabButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
        }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Theme;
            Graphics g = e.Graphics;
            Color bg = selected ? t.Surface : (hover && Enabled ? t.ButtonHover : t.Back);
            g.Clear(bg);
            using (var pen = new Pen(t.Border))
            {
                if (selected)
                {
                    g.DrawLine(pen, 0, 0, 0, Height);
                    g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
                    g.DrawLine(pen, 0, 0, Width - 1, 0);
                    int bar = Math.Max(2, Height / 12);
                    using (var b = new SolidBrush(t.Accent)) g.FillRectangle(b, 1, 1, Width - 2, bar);
                }
                else
                {
                    g.DrawLine(pen, 0, Height - 1, Width - 1, Height - 1);
                }
            }
            Color fg = !Enabled ? t.SubText : (selected ? t.Text : t.SubText);
            using (var f = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(g, Text, f, ClientRectangle, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>扁平进度条。</summary>
    public class FlatProgress : Control
    {
        int value;
        public int Maximum = 1000;
        public Theme Theme = FileSplitter.Theme.Light;

        public FlatProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        public int Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(Maximum, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Track);
            int w = (int)((long)Width * value / Maximum);
            if (w > 0) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, 0, 0, w, Height);
        }
    }

    /// <summary>带自绘边框的输入框容器（系统 TextBox 边框无法换色）。</summary>
    public class InputBox : Panel
    {
        public readonly TextBox Box = new TextBox();
        public Theme Theme = FileSplitter.Theme.Light;

        public InputBox(bool multiline)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
            Box.BorderStyle = BorderStyle.None;
            Box.Multiline = multiline;
            Controls.Add(Box);
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Click += delegate { Box.Focus(); };
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Math.Max(4, Font.Height / 3);
            if (Box.Multiline)
            {
                Box.SetBounds(pad, 2 + pad / 2, Math.Max(10, Width - pad - 2), Math.Max(10, Height - pad - 3));
            }
            else
            {
                int bh = Box.PreferredHeight;
                Box.SetBounds(pad, Math.Max(1, (Height - bh) / 2), Math.Max(10, Width - pad * 2), bh);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Input);
            bool focus = Box.Focused && !Box.ReadOnly;
            using (var pen = new Pen(focus ? Theme.Accent : Theme.Border))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (focus)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, 1, Height - 2, Width - 2, 1);
        }

        public void ApplyTheme(Theme t)
        {
            Theme = t;
            BackColor = t.Input;
            Box.BackColor = t.Input;
            Box.ForeColor = t.Text;
            if (Box.Multiline && Box.IsHandleCreated) Native.SetDarkScrollBars(Box.Handle, t.IsDark);
            Invalidate();
        }
    }
}
