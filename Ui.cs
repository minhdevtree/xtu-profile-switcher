// Look and controls.
//
// A working tool, not a showcase: flat surfaces, 1px hairlines instead of cards, one data colour
// (ember, for the watts) and state colours only where they mean something. The one thing that is
// specific to this product is the power strip in the readout (PL1/PL2 against the BIOS defaults).
// Dark or light follows the Windows app setting.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XtuProfileSwitcher
{
    internal static class Theme
    {
        public static bool Dark;
        public static Color Ground, Raised, Line, Text, Sub, Ember, EmberDim, Track, Ok, Bad, InkBg, InkFg, Heat;

        public static void Init(string mode)
        {
            bool dark;
            if (mode == "dark") dark = true;
            else if (mode == "light") dark = false;
            else
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                dark = v != null && Convert.ToInt32(v) == 0;
            }
            Dark = dark;
            if (dark)
            {
                Ground = Rgb(0x15, 0x17, 0x1B); Raised = Rgb(0x1E, 0x21, 0x27); Line = Rgb(0x2C, 0x30, 0x38);
                Text = Rgb(0xEA, 0xE8, 0xE2); Sub = Rgb(0x92, 0x99, 0xA6);
                Ember = Rgb(0xF2, 0xA3, 0x3A); EmberDim = Rgb(0x6B, 0x4B, 0x1F); Track = Rgb(0x25, 0x29, 0x31);
                Ok = Rgb(0x4F, 0xC3, 0xA1); Bad = Rgb(0xF0, 0x6B, 0x6B); Heat = Rgb(0xF0, 0x7D, 0x6A);
                InkBg = Rgb(0xEA, 0xE8, 0xE2); InkFg = Rgb(0x15, 0x17, 0x1B);
            }
            else
            {
                Ground = Rgb(0xF4, 0xF2, 0xEC); Raised = Rgb(0xE9, 0xE6, 0xDD); Line = Rgb(0xD9, 0xD5, 0xC9);
                Text = Rgb(0x1A, 0x1C, 0x20); Sub = Rgb(0x5F, 0x66, 0x72);
                Ember = Rgb(0xB4, 0x50, 0x0A); EmberDim = Rgb(0xE6, 0xC7, 0xA3); Track = Rgb(0xE2, 0xDE, 0xD2);
                Ok = Rgb(0x1B, 0x7F, 0x63); Bad = Rgb(0xC1, 0x30, 0x30); Heat = Rgb(0xC2, 0x43, 0x2A);
                InkBg = Rgb(0x1A, 0x1C, 0x20); InkFg = Rgb(0xF4, 0xF2, 0xEC);
            }
        }

        private static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        // Blend a toward b by t (0..1).
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        private static string bodyFamily, displayFamily;
        private static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        private static string Pick(string preferred)
        {
            foreach (FontFamily f in FontFamily.Families) if (f.Name == preferred) return preferred;
            return "Segoe UI";
        }

        public static Font Body(float pt) { return Get(BodyName, pt, FontStyle.Regular); }
        public static Font Strong(float pt) { return Get(BodyName, pt, FontStyle.Bold); }
        public static Font Figure(float pt) { return Get(DisplayName, pt, FontStyle.Bold); }

        private static string BodyName { get { if (bodyFamily == null) bodyFamily = Pick("Segoe UI Variable Text"); return bodyFamily; } }
        private static string DisplayName { get { if (displayFamily == null) displayFamily = Pick("Segoe UI Variable Display"); return displayFamily; } }

        private static Font Get(string family, float pt, FontStyle style)
        {
            string key = family + "|" + pt + "|" + style;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(family, pt, style, GraphicsUnit.Point); fonts[key] = f; }
            return f;
        }
    }

    internal static class Ui
    {
        public static float Scale = 1f;

        public static void InitScale()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = Math.Max(1f, g.DpiX / 96f); }
            catch { Scale = 1f; }
        }

        public static int S(int v) { return (int)Math.Round(v * Scale); }
        public static float S(float v) { return v * Scale; }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            GraphicsPath p = new GraphicsPath();
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags align)
        {
            TextRenderer.DrawText(g, s, f, r, c,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | align);
        }

        public static int Width(Graphics g, string s, Font f)
        {
            return TextRenderer.MeasureText(g, s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        }

        public static void Dot(Graphics g, Color c, float cx, float cy, float diameter)
        {
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, cx - diameter / 2, cy - diameter / 2, diameter, diameter);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Dark title bar to match the dark theme.
        public static void TitleBar(IntPtr handle)
        {
            try
            {
                int v = Theme.Dark ? 1 : 0;
                DwmSetWindowAttribute(handle, 20, ref v, 4);
                DwmSetWindowAttribute(handle, 19, ref v, 4);
            }
            catch { }
        }

        // Renders a (hidden, off-screen) form to a PNG, for checking the look without touching the real screen.
        public static void Snapshot(Form f, string file, Size client) { Snapshot(f, file, client, 0, null); }

        // warmupMs: keep the message loop running for a while first (live data needs time to arrive).
        public static void Snapshot(Form f, string file, Size client, int warmupMs, Action beforeDraw)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.ShowInTaskbar = false;
            f.Location = new Point(-12000, -12000);
            f.ClientSize = client;
            f.Show();
            Application.DoEvents();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < warmupMs) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
            if (beforeDraw != null) beforeDraw();
            f.Refresh();
            using (Bitmap bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }

    internal static class AppIcon
    {
        // Picks the best-matching size from the embedded multi-size icon.
        public static Icon Load(int size)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("XtuProfileSwitcher.app.ico"))
                {
                    if (s != null) return new Icon(s, new Size(size, size));
                }
            }
            catch { }
            return SystemIcons.Application;
        }
    }

    // Context menus (dropdown lists, tray) in the theme's colours.
    internal sealed class ThemedColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Raised; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Raised; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Raised; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Raised; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color MenuItemBorder { get { return Theme.Line; } }
        public override Color MenuItemSelected { get { return Theme.Line; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Line; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Line; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Raised; } }
        public override Color CheckBackground { get { return Theme.Line; } }
        public override Color CheckSelectedBackground { get { return Theme.Line; } }
        public override Color CheckPressedBackground { get { return Theme.Line; } }
    }

    internal static class Menus
    {
        public static ContextMenuStrip Create()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripProfessionalRenderer r = new ToolStripProfessionalRenderer(new ThemedColors());
            r.RoundedEdges = false;
            m.Renderer = r;
            m.BackColor = Theme.Raised;
            m.ForeColor = Theme.Text;
            m.Font = Theme.Body(9.5f);
            m.ShowImageMargin = true;
            return m;
        }

        public static ToolStripMenuItem Item(string text, EventHandler click)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text);
            i.ForeColor = Theme.Text;
            i.BackColor = Theme.Raised;
            if (click != null) i.Click += click;
            return i;
        }
    }

    // Base for the custom-drawn controls: double buffered, themed ground.
    internal abstract class Flat : Control
    {
        protected Flat()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Ground;
            ForeColor = Theme.Text;
            Font = Theme.Body(9.5f);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Theme.Ground); }

        protected static void FocusRing(Graphics g, Rectangle r, float radius)
        {
            using (GraphicsPath p = Ui.Round(new RectangleF(r.X - 1.5f, r.Y - 1.5f, r.Width + 3f, r.Height + 3f), radius + 1.5f))
            using (Pen pen = new Pen(Theme.Ember, 1.5f))
                g.DrawPath(pen, p);
        }
    }

    internal enum BtnKind { Primary, Secondary, Quiet }

    internal sealed class Btn : Flat
    {
        public BtnKind Kind = BtnKind.Secondary;
        private bool hover, down;

        public Btn()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Height = Ui.S(34);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        // (Btn and Switch repaint on enable/disable; Dropdown does too, below.)

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg = Theme.Ground, fg = Theme.Text, border = Color.Transparent;

            if (Kind == BtnKind.Primary)
            {
                bg = Enabled ? (hover || down ? Theme.Mix(Theme.InkBg, Theme.Ember, down ? 0.35 : 0.18) : Theme.InkBg) : Theme.Raised;
                fg = Enabled ? Theme.InkFg : Theme.Sub;
            }
            else if (Kind == BtnKind.Secondary)
            {
                bg = Enabled && (hover || down) ? Theme.Raised : Theme.Ground;
                border = Enabled ? (hover ? Theme.Sub : Theme.Line) : Theme.Line;
                fg = Enabled ? Theme.Text : Theme.Sub;
            }
            else
            {
                bg = Enabled && (hover || down) ? Theme.Raised : Theme.Ground;
                fg = Enabled ? (hover ? Theme.Text : Theme.Sub) : Theme.Mix(Theme.Sub, Theme.Ground, 0.5);
            }

            using (GraphicsPath p = Ui.Round(r, Ui.S(6)))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
                if (border != Color.Transparent) using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
            }
            Ui.Text(g, Text, Theme.Body(9.5f), fg, new Rectangle(Ui.S(8), 0, Width - Ui.S(16), Height), TextFormatFlags.HorizontalCenter);
            if (Focused && ShowFocusCues) FocusRing(g, r, Ui.S(6));
        }
    }

    internal sealed class Switch : Flat
    {
        private bool on;
        public event EventHandler CheckedChanged;

        public Switch()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Size = new Size(Ui.S(40), Ui.S(22));
        }

        public bool Checked
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        }

        // Change the state without raising CheckedChanged (used when loading settings).
        public void Set(bool value) { on = value; Invalidate(); }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            // A disabled switch keeps its position but fades towards the background.
            Color onC = Enabled ? Theme.Ember : Theme.Mix(Theme.Ember, Theme.Ground, 0.6);
            Color knobC = on ? Theme.Ground : (Enabled ? Theme.Sub : Theme.Mix(Theme.Sub, Theme.Ground, 0.5));
            using (GraphicsPath p = Ui.Round(r, r.Height / 2))
            {
                using (SolidBrush b = new SolidBrush(on ? onC : Theme.Track)) g.FillPath(b, p);
                if (!on) using (Pen pen = new Pen(Theme.Line)) g.DrawPath(pen, p);
            }
            float d = r.Height - Ui.S(8f);
            float x = on ? r.Right - d - Ui.S(4f) : r.Left + Ui.S(4f);
            Ui.Dot(g, knobC, x + d / 2, r.Top + r.Height / 2, d);
            if (Focused && ShowFocusCues) FocusRing(g, new Rectangle(0, 0, Width - 1, Height - 1), Height / 2f);
        }
    }

    internal sealed class Dropdown : Flat
    {
        public readonly List<string> Items = new List<string>();
        private int idx;
        private bool hover;
        public event EventHandler SelectionChanged;

        public Dropdown()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Height = Ui.S(34);
        }

        public int SelectedIndex
        {
            get { return idx; }
            set { if (value >= 0 && value < Items.Count) { idx = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.Space || keyData == Keys.Down || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.Down) { Open(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnClick(EventArgs e) { Open(); base.OnClick(e); }

        private void Open()
        {
            if (!Enabled || Items.Count == 0) return;
            ContextMenuStrip m = Menus.Create();
            for (int i = 0; i < Items.Count; i++)
            {
                int index = i;
                ToolStripMenuItem it = Menus.Item(Items[i], delegate
                {
                    idx = index;
                    Invalidate();
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                });
                it.Checked = i == idx;
                m.Items.Add(it);
            }
            m.MinimumSize = new Size(Width, 0);
            m.Show(this, new Point(0, Height + Ui.S(2)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Ui.Round(r, Ui.S(6)))
            {
                using (SolidBrush b = new SolidBrush(Enabled ? Theme.Raised : Theme.Ground)) g.FillPath(b, p);
                using (Pen pen = new Pen(hover && Enabled ? Theme.Sub : Theme.Line)) g.DrawPath(pen, p);
            }
            string t = idx >= 0 && idx < Items.Count ? Items[idx] : "";
            Ui.Text(g, t, Theme.Body(9.5f), Enabled ? Theme.Text : Theme.Mix(Theme.Sub, Theme.Ground, 0.4), new Rectangle(Ui.S(12), 0, Width - Ui.S(40), Height), TextFormatFlags.Left);
            float cx = Width - Ui.S(18f), cy = Height / 2f;
            using (Pen pen = new Pen(Theme.Sub, 1.6f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - Ui.S(4f), cy - Ui.S(2f), cx, cy + Ui.S(2f));
                g.DrawLine(pen, cx, cy + Ui.S(2f), cx + Ui.S(4f), cy - Ui.S(2f));
            }
            if (Focused && ShowFocusCues) FocusRing(g, r, Ui.S(6));
        }
    }

    // The top of the window: product mark and name, connection state.
    internal sealed class TopBar : Flat
    {
        public string Status = "";
        public Color StatusColor = Theme.Sub;

        public TopBar() { Height = Ui.S(48); Cursor = Cursors.Default; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int m = Ui.S(28);

            int icon = Ui.S(26);
            try { using (Icon ic = AppIcon.Load(icon)) g.DrawIcon(ic, new Rectangle(m, (Height - icon) / 2, icon, icon)); } catch { }
            Ui.Text(g, "XTU Profile Switcher", Theme.Strong(11.5f), Theme.Text,
                new Rectangle(m + icon + Ui.S(10), 0, Width / 2, Height), TextFormatFlags.Left);

            // connection state on the right
            if (Status.Length > 0)
            {
                Font f = Theme.Body(9.5f);
                int sw = Ui.Width(g, Status, f);
                int sx = Width - m - sw;
                Ui.Dot(g, StatusColor, sx - Ui.S(10), Height / 2f, Ui.S(8f));
                Ui.Text(g, Status, f, Theme.Sub, new Rectangle(sx, 0, sw + Ui.S(4), Height), TextFormatFlags.Left);
            }
        }
    }
    // What the CPU has right now. The strip is this product's own element: PL1 and PL2 drawn as
    // watts on one scale, with the BIOS defaults marked so a reset to default is visible at a glance.
    internal sealed class Readout : Flat
    {
        public bool Connected;
        public decimal Pl1, Pl2, Window, BiosPl1 = 90, BiosPl2 = 115;
        public bool Pl2On = true;
        public string ProfileName;     // null: no saved profile matches
        public string PowerText = "";
        public string Problem = "";

        private const decimal ScaleMax = 150m;

        public Readout() { Height = Ui.S(134); }

        private static string W(decimal v) { return v.ToString("0.###"); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int m = Ui.S(28), w = Width - 2 * m;

            if (!Connected)
            {
                Ui.Text(g, Loc.T("ro.unavailable"), Theme.Strong(14f), Theme.Text, new Rectangle(m, Ui.S(8), w, Ui.S(30)), TextFormatFlags.Left);
                Ui.Text(g, Problem, Theme.Body(9.5f), Theme.Sub, new Rectangle(m, Ui.S(40), w, Ui.S(24)), TextFormatFlags.Left);
                return;
            }

            // two figures: the sustained and the burst limit
            Font fig = Theme.Figure(30f), unit = Theme.Body(11f), label = Theme.Body(9f);
            int x = m, top = Ui.S(2);
            DrawFigure(g, W(Pl1), "W", Loc.T("ro.pl1"), x, top, fig, unit, label, true);
            int x2 = x + Ui.S(150);
            DrawFigure(g, Pl2On ? W(Pl2) : Loc.T("ro.off"), Pl2On ? "W" : "", Loc.T("ro.pl2"), x2, top, fig, unit, label, Pl2On);
            Ui.Text(g, Loc.F("ro.window", W(Window)), Theme.Body(10f), Theme.Sub, new Rectangle(x2 + Ui.S(150), top + Ui.S(22), Ui.S(130), Ui.S(26)), TextFormatFlags.Left);

            // right side: which saved profile this is, and where the power comes from
            int rw = Ui.S(190);
            Rectangle rr = new Rectangle(m + w - rw, top + Ui.S(6), rw, Ui.S(26));
            Ui.Text(g, ProfileName != null ? ProfileName : "–", Theme.Strong(11.5f), ProfileName != null ? Theme.Text : Theme.Sub, rr, TextFormatFlags.Right);
            Ui.Text(g, ProfileName != null ? Loc.T("ro.matches") : Loc.T("ro.nomatch"), Theme.Body(9f), Theme.Sub,
                new Rectangle(rr.X, rr.Bottom, rw, Ui.S(20)), TextFormatFlags.Right);
            Ui.Text(g, PowerText, Theme.Body(9f), Theme.Sub, new Rectangle(rr.X, rr.Bottom + Ui.S(20), rw, Ui.S(20)), TextFormatFlags.Right);

            // the strip
            int sy = top + Ui.S(84), sh = Ui.S(8);
            RectangleF track = new RectangleF(m, sy, w, sh);
            using (GraphicsPath p = Ui.Round(track, sh / 2f)) using (SolidBrush b = new SolidBrush(Theme.Track)) g.FillPath(b, p);

            float f1 = Frac(Pl1), f2 = Pl2On ? Math.Max(f1, Frac(Pl2)) : f1;
            if (f2 > f1)
                using (GraphicsPath p = Ui.Round(new RectangleF(m, sy, w * f2, sh), sh / 2f)) using (SolidBrush b = new SolidBrush(Theme.EmberDim)) g.FillPath(b, p);
            if (f1 > 0)
                using (GraphicsPath p = Ui.Round(new RectangleF(m, sy, Math.Max(sh, w * f1), sh), sh / 2f)) using (SolidBrush b = new SolidBrush(Theme.Ember)) g.FillPath(b, p);

            // BIOS default ticks
            using (Pen tick = new Pen(Theme.Text, 1.5f))
            {
                foreach (decimal d in new decimal[] { BiosPl1, BiosPl2 })
                {
                    float tx = m + w * Frac(d);
                    g.DrawLine(tick, tx, sy - Ui.S(4), tx, sy + sh + Ui.S(4));
                }
            }
            Font small = Theme.Body(8.5f);
            Ui.Text(g, "0", small, Theme.Sub, new Rectangle(m, sy + sh + Ui.S(8), Ui.S(30), Ui.S(18)), TextFormatFlags.Left);
            Ui.Text(g, W(ScaleMax) + " W", small, Theme.Sub, new Rectangle(m + w - Ui.S(60), sy + sh + Ui.S(8), Ui.S(60), Ui.S(18)), TextFormatFlags.Right);
            float bx = m + w * Frac(BiosPl1);
            string bios = Loc.T("ro.bios") + " " + W(BiosPl1) + " / " + W(BiosPl2) + " W";
            int bw = Ui.Width(g, bios, small) + Ui.S(8);
            Ui.Text(g, bios, small, Theme.Sub, new Rectangle((int)Math.Min(bx - bw / 2f, m + w - Ui.S(70) - bw), sy + sh + Ui.S(8), bw, Ui.S(18)), TextFormatFlags.HorizontalCenter);
        }

        private float Frac(decimal v) { return (float)Math.Max(0m, Math.Min(1m, v / ScaleMax)); }

        private void DrawFigure(Graphics g, string value, string unitText, string caption, int x, int y, Font fig, Font unit, Font label, bool data)
        {
            Color c = data ? Theme.Ember : Theme.Sub;
            int vw = Ui.Width(g, value, fig);
            Ui.Text(g, value, fig, c, new Rectangle(x, y, vw + Ui.S(4), Ui.S(46)), TextFormatFlags.Left);
            if (unitText.Length > 0)
                Ui.Text(g, unitText, unit, Theme.Sub, new Rectangle(x + vw + Ui.S(4), y + Ui.S(10), Ui.S(30), Ui.S(30)), TextFormatFlags.Left);
            Ui.Text(g, caption, label, Theme.Sub, new Rectangle(x, y + Ui.S(46), Ui.S(140), Ui.S(20)), TextFormatFlags.Left);
        }
    }

    // Small borderless notice that shows for a couple of seconds without taking focus.
    internal sealed class Osd : Form
    {
        private static Osd current;
        private readonly System.Windows.Forms.Timer life = new System.Windows.Forms.Timer();
        private readonly string title, detail;
        private readonly bool error;

        public static void Flash(string title, string detail, bool error)
        {
            try
            {
                if (current != null && !current.IsDisposed) current.Close();
                current = new Osd(title, detail, error);
                current.Show();
            }
            catch (Exception ex) { Log.Write("OSD failed: " + ex.Message); }
        }

        private Osd(string title, string detail, bool error)
        {
            this.title = title;
            this.detail = detail;
            this.error = error;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Raised;
            Size = new Size(Ui.S(440), Ui.S(72));
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - Ui.S(56));
            using (GraphicsPath p = Ui.Round(new RectangleF(0, 0, Width, Height), Ui.S(10)))
                Region = new Region(p);

            life.Interval = 2600;
            life.Tick += delegate { life.Stop(); Close(); };
            Shown += delegate { life.Start(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;   // no-activate, tool window, click-through
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Raised);
            using (GraphicsPath p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(10)))
            using (Pen pen = new Pen(Theme.Line)) g.DrawPath(pen, p);
            Ui.Dot(g, error ? Theme.Bad : Theme.Ember, Ui.S(24f), Height / 2f, Ui.S(9f));
            Ui.Text(g, title, Theme.Strong(11f), Theme.Text, new Rectangle(Ui.S(42), Ui.S(10), Width - Ui.S(58), Ui.S(26)), TextFormatFlags.Left);
            Ui.Text(g, detail, Theme.Body(9.5f), Theme.Sub, new Rectangle(Ui.S(42), Ui.S(36), Width - Ui.S(58), Ui.S(24)), TextFormatFlags.Left);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) life.Dispose();
            base.Dispose(disposing);
        }
    }

    internal abstract class DialogBase : Form
    {
        protected DialogBase(string title, int width, int height)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Ground;
            ForeColor = Theme.Text;
            Font = Theme.Body(9.5f);
            Icon = AppIcon.Load(32);
            ClientSize = new Size(Ui.S(width), Ui.S(height));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.TitleBar(Handle);
        }

        protected Label Caption(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Theme.Sub;
            l.BackColor = Theme.Ground;
            l.SetBounds(Ui.S(x), Ui.S(y), Ui.S(w), Ui.S(22));
            l.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(l);
            return l;
        }

        protected TextBox Field(int x, int y, int w, string text)
        {
            TextBox t = new TextBox();
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Theme.Raised;
            t.ForeColor = Theme.Text;
            t.Font = Theme.Body(10f);
            t.Text = text;
            t.SetBounds(Ui.S(x), Ui.S(y), Ui.S(w), Ui.S(28));
            Controls.Add(t);
            return t;
        }

        protected Btn Button(string text, BtnKind kind, int x, int y, int w)
        {
            Btn b = new Btn();
            b.Kind = kind;
            b.Text = text;
            b.SetBounds(Ui.S(x), Ui.S(y), Ui.S(w), Ui.S(34));
            Controls.Add(b);
            return b;
        }
    }

    // Records a key combination.
    internal sealed class HotkeyDialog : DialogBase
    {
        private readonly Label shown = new Label();
        public string Result;   // null = unchanged, "" = cleared, otherwise hotkey text

        public HotkeyDialog(string profileName, string current)
            : base(Loc.F("dlg.hk.title", profileName), 400, 190)
        {
            Label hint = Caption(Loc.T("dlg.hk.hint"), 24, 20, 352);
            hint.Height = Ui.S(24);

            shown.Text = string.IsNullOrEmpty(current) ? Loc.T("dlg.hk.none") : current;
            shown.Font = Theme.Strong(14f);
            shown.BackColor = Theme.Raised;
            shown.ForeColor = string.IsNullOrEmpty(current) ? Theme.Sub : Theme.Text;
            shown.TextAlign = ContentAlignment.MiddleCenter;
            shown.SetBounds(Ui.S(24), Ui.S(52), Ui.S(352), Ui.S(52));
            Controls.Add(shown);

            Btn save = Button(Loc.T("dlg.save"), BtnKind.Primary, 24, 130, 100);
            save.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Btn clear = Button(Loc.T("dlg.clear"), BtnKind.Secondary, 134, 130, 100);
            clear.Click += delegate { Result = ""; shown.Text = Loc.T("dlg.hk.none"); shown.ForeColor = Theme.Sub; };
            Btn cancel = Button(Loc.T("dlg.cancel"), BtnKind.Quiet, 276, 130, 100);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.None
                || key == Keys.Escape || key == Keys.Enter || key == Keys.Tab || key == Keys.Space)
                return base.ProcessCmdKey(ref msg, keyData);

            Hotkey h = new Hotkey();
            h.Key = key;
            if ((keyData & Keys.Control) != 0) h.Mods |= Hotkey.Ctrl;
            if ((keyData & Keys.Alt) != 0) h.Mods |= Hotkey.Alt;
            if ((keyData & Keys.Shift) != 0) h.Mods |= Hotkey.Shift;
            if ((h.Mods & (Hotkey.Ctrl | Hotkey.Alt)) == 0) return true;   // wait for Ctrl or Alt

            Result = h.ToString();
            shown.Text = Result;
            shown.ForeColor = Theme.Text;
            return true;
        }
    }

    // Creates a power-limit profile that lives in this tool (not in XTU).
    internal sealed class ProfileDialog : DialogBase
    {
        private readonly TextBox name, pl1, pl2, window;
        private readonly Label error = new Label();
        private readonly List<string> used;
        public Profile Result;

        public ProfileDialog(List<string> usedNames)
            : base(Loc.T("dlg.new.title"), 400, 300)
        {
            used = usedNames;
            Caption(Loc.T("dlg.new.name"), 24, 22, 150);
            name = Field(190, 20, 186, "PL35-40W");
            Caption(Loc.T("dlg.new.pl1"), 24, 62, 160);
            pl1 = Field(190, 60, 186, "35");
            Caption(Loc.T("dlg.new.pl2"), 24, 102, 160);
            pl2 = Field(190, 100, 186, "40");
            Caption(Loc.T("dlg.new.window"), 24, 142, 160);
            window = Field(190, 140, 186, "56");

            error.ForeColor = Theme.Bad;
            error.BackColor = Theme.Ground;
            error.SetBounds(Ui.S(24), Ui.S(182), Ui.S(352), Ui.S(44));
            Controls.Add(error);

            Btn ok = Button(Loc.T("dlg.create"), BtnKind.Primary, 24, 244, 110);
            ok.Click += delegate { TryCreate(); };
            Btn cancel = Button(Loc.T("dlg.cancel"), BtnKind.Quiet, 266, 244, 110);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = null;
        }

        private void TryCreate()
        {
            string n = name.Text.Trim();
            if (n.Length == 0 || n.Length > 40) { error.Text = Loc.T("val.name"); return; }
            if (n.StartsWith("@") || n.IndexOf('\t') >= 0 || n.IndexOf('\n') >= 0 || n.IndexOf('\r') >= 0)
            { error.Text = Loc.T("val.namechars"); return; }
            foreach (string u in used)
                if (string.Equals(u, n, StringComparison.OrdinalIgnoreCase)) { error.Text = Loc.F("val.nameused", u); return; }

            int a, b, c;
            if (!int.TryParse(pl1.Text.Trim(), out a) || !int.TryParse(pl2.Text.Trim(), out b) || !int.TryParse(window.Text.Trim(), out c))
            { error.Text = Loc.T("val.numbers"); return; }

            Profile p = new Profile();
            p.Name = n;
            p.Custom = true;
            p.Pl1 = a;
            p.Pl2 = b;
            p.Pl2Enable = true;
            p.Window = c;
            try { XtuClient.Validate(p); }
            catch (Exception ex) { error.Text = ex.Message; return; }

            Result = p;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
