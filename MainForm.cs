// The main window and the tray icon. All window text comes from Loc; all colours from Theme.
//
// Layout: top bar, the readout (always visible), then three tabs.
//   Profiles: the table; click a row to apply it, "..." for hotkey / delete.
//   Monitor:  temperature, power and CPU use over the last 5 minutes, and recording to CSV.
//   Settings: automatic switch by power source, start with Windows, system facts.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XtuProfileSwitcher
{
    internal sealed class MainForm : Form
    {
        private const int WmHotkey = 0x0312;
        private static readonly int[] EverySeconds = { 1, 2, 5, 10, 30, 60 };
        private static readonly int[] StopAfterMinutes = { 0, 5, 15, 30, 60, 120, 240, 480 };

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly XtuClient client = new XtuClient();
        private readonly MonitorService monitor = new MonitorService();
        private readonly AppSettings settings;
        private readonly bool preview;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer monTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer resumeTimer = new System.Windows.Forms.Timer();

        private readonly TopBar top = new TopBar();
        private readonly Readout readout = new Readout();
        private readonly TabStrip tabs = new TabStrip();
        private readonly Btn btnNew = new Btn();
        private readonly ProfileTable table = new ProfileTable();

        // monitor page
        private readonly SeriesRow rowTemp = new SeriesRow();
        private readonly SeriesRow rowPower = new SeriesRow();
        private readonly SeriesRow rowUsage = new SeriesRow();
        private readonly Label lblDetails = new Label();
        private readonly Label recTitle = new Label();
        private readonly Label capEvery = new Label();
        private readonly Label capFor = new Label();
        private readonly Dropdown ddEvery = new Dropdown();
        private readonly Dropdown ddFor = new Dropdown();
        private readonly Switch swCore = new Switch();
        private readonly Switch swGpu = new Switch();
        private readonly Label lblCore = new Label();
        private readonly Label lblGpu = new Label();
        private readonly Btn btnRec = new Btn();
        private readonly Label lblRec = new Label();
        private readonly Btn btnOpen = new Btn();

        // settings page
        private readonly Label autoTitle = new Label();
        private readonly Label autoDesc = new Label();
        private readonly Switch swAuto = new Switch();
        private readonly Label lblAc = new Label();
        private readonly Label lblBat = new Label();
        private readonly Dropdown ddAc = new Dropdown();
        private readonly Dropdown ddBat = new Dropdown();
        private readonly Label suTitle = new Label();
        private readonly Label suDesc = new Label();
        private readonly Switch swStartup = new Switch();
        private readonly Label lblFacts = new Label();
        private readonly Btn btnStartSvc = new Btn();
        private readonly Label lblLang = new Label();
        private readonly Dropdown ddLang = new Dropdown();
        private readonly Label statusLabel = new Label();

        private List<Profile> profiles = new List<Profile>();
        private Snapshot last = new Snapshot();
        private readonly Dictionary<int, string> registered = new Dictionary<int, string>();
        private PowerLineStatus lastLine = PowerLineStatus.Unknown;
        private bool reallyExit, startHidden, busy, inited, suppress, monRetained;
        private int tick, ruleSettings, ruleLang, statusY;
        private bool statusError;
        private string comboKey = "";

        private Dictionary<string, string> hotkeys { get { return settings.Hotkeys; } }
        private int Page { get { return tabs.SelectedIndex; } }

        // Preview only (--snapshot ...): draw states that are hard to reach on demand.
        public bool SimulateOffline;
        public int PreviewTab;
        public string PreviewClick;
        public bool PreviewRecord;
        public string PreviewRecordPath = "";

        public MainForm(bool startHidden, EventWaitHandle showSignal, bool preview, AppSettings settings)
        {
            this.startHidden = startHidden;
            this.preview = preview;
            this.settings = settings;

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Icon = AppIcon.Load(32);
            Font = Theme.Body(9.5f);
            BackColor = Theme.Ground;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int h = Math.Min(Ui.S(700), (int)(wa.Height * 0.92) - Ui.S(48));
            ClientSize = new Size(Ui.S(640), Math.Max(Ui.S(560), h));
            MinimumSize = SizeFromClientSize(new Size(Ui.S(600), Ui.S(560)));

            Controls.Add(top);
            Controls.Add(readout);

            tabs.SelectedChanged += delegate { ShowPage(); };
            Controls.Add(tabs);
            btnNew.Kind = BtnKind.Quiet;
            btnNew.Click += delegate { AddProfile(); };
            Controls.Add(btnNew);

            table.Activated += delegate (object s, RowEventArgs e) { ApplyProfile((Profile)e.Row.Tag, false, null); };
            table.MenuRequested += delegate (object s, RowEventArgs e) { ShowRowMenu(e.Row, e.Anchor); };
            Controls.Add(table);

            BuildMonitorPage();
            BuildSettingsPage();

            StyleLabel(statusLabel, false, 9.5f);
            statusLabel.ForeColor = Theme.Sub;
            Controls.Add(statusLabel);

            tray.Icon = AppIcon.Load(SystemInformation.SmallIconSize.Width);
            tray.Visible = !preview;
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWindow(); };

            timer.Interval = 5000;
            timer.Tick += delegate
            {
                // Refresh less often while hidden in the tray; the power check is cheap.
                tick++;
                if (Visible || tick % 3 == 0)
                {
                    RefreshAll();
                    monitor.Profiles = profiles;
                }
                CheckPowerSource(false);
            };
            monTimer.Interval = 1000;
            monTimer.Tick += delegate { UpdateMonitorUi(); };
            resumeTimer.Interval = 4000;
            resumeTimer.Tick += delegate { resumeTimer.Stop(); CheckPowerSource(true); };
            if (!preview) SystemEvents.PowerModeChanged += OnPowerModeChanged;
            monitor.RecordingEnded += delegate
            {
                try { if (IsHandleCreated) BeginInvoke(new Action(OnRecordingEnded)); } catch { }
            };

            Load += delegate { Init(); };
            FormClosing += OnFormClosing;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            SizeChanged += delegate { Relayout(); };
            VisibleChanged += delegate { UpdateMonitorSubscription(); };

            if (showSignal != null)
            {
                ThreadPool.RegisterWaitForSingleObject(showSignal, delegate
                {
                    try { if (IsHandleCreated) BeginInvoke(new Action(ShowWindow)); } catch { }
                }, null, -1, false);
            }

            ApplyTexts();
            Relayout();
        }

        private static void StyleLabel(Label l, bool strong, float pt)
        {
            l.AutoSize = false;
            l.BackColor = Theme.Ground;
            l.ForeColor = Theme.Text;
            l.Font = strong ? Theme.Strong(pt) : Theme.Body(pt);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.UseMnemonic = false;
        }

        private void BuildMonitorPage()
        {
            rowTemp.Hue = Theme.Heat;
            rowTemp.Lo = 20; rowTemp.Hi = 100;
            rowPower.Hue = Theme.Ember;
            rowPower.AutoHi = true;
            rowUsage.Hue = Theme.Text;
            rowUsage.Lo = 0; rowUsage.Hi = 100;
            Controls.Add(rowTemp);
            Controls.Add(rowPower);
            Controls.Add(rowUsage);

            StyleLabel(lblDetails, false, 9f);
            lblDetails.ForeColor = Theme.Sub;
            Controls.Add(lblDetails);

            StyleLabel(recTitle, true, 10.5f);
            StyleLabel(capEvery, false, 9.5f);
            capEvery.ForeColor = Theme.Sub;
            StyleLabel(capFor, false, 9.5f);
            capFor.ForeColor = Theme.Sub;
            StyleLabel(lblCore, false, 9.5f);
            StyleLabel(lblGpu, false, 9.5f);
            StyleLabel(lblRec, false, 9.5f);
            lblRec.ForeColor = Theme.Sub;
            Controls.Add(recTitle);
            Controls.Add(capEvery);
            Controls.Add(capFor);
            Controls.Add(lblCore);
            Controls.Add(lblGpu);
            Controls.Add(lblRec);

            ddEvery.SelectionChanged += delegate
            {
                if (suppress) return;
                settings.RecEvery = EverySeconds[Math.Max(0, ddEvery.SelectedIndex)];
                settings.Save();
            };
            ddFor.SelectionChanged += delegate
            {
                if (suppress) return;
                settings.RecMinutes = StopAfterMinutes[Math.Max(0, ddFor.SelectedIndex)];
                settings.Save();
            };
            swCore.CheckedChanged += delegate { if (suppress) return; settings.RecPerCore = swCore.Checked; settings.Save(); };
            swGpu.CheckedChanged += delegate
            {
                if (suppress) return;
                settings.RecGpu = swGpu.Checked;
                monitor.IncludeGpu = swGpu.Checked;
                settings.Save();
            };
            Controls.Add(ddEvery);
            Controls.Add(ddFor);
            Controls.Add(swCore);
            Controls.Add(swGpu);

            btnRec.Kind = BtnKind.Primary;
            btnRec.Click += delegate { ToggleRecording(); };
            Controls.Add(btnRec);
            btnOpen.Kind = BtnKind.Quiet;
            btnOpen.Click += delegate { OpenLogFolder(); };
            Controls.Add(btnOpen);

            monitor.IncludeGpu = settings.RecGpu;
        }

        private void BuildSettingsPage()
        {
            StyleLabel(autoTitle, true, 10.5f);
            StyleLabel(autoDesc, false, 9.5f);
            autoDesc.ForeColor = Theme.Sub;
            StyleLabel(lblAc, false, 9.5f);
            StyleLabel(lblBat, false, 9.5f);
            Controls.Add(autoTitle);
            Controls.Add(autoDesc);
            Controls.Add(lblAc);
            Controls.Add(lblBat);
            swAuto.CheckedChanged += delegate { OnAutoChanged(swAuto); };
            ddAc.SelectionChanged += delegate { OnAutoChanged(ddAc); };
            ddBat.SelectionChanged += delegate { OnAutoChanged(ddBat); };
            Controls.Add(swAuto);
            Controls.Add(ddAc);
            Controls.Add(ddBat);

            StyleLabel(suTitle, true, 10.5f);
            StyleLabel(suDesc, false, 9.5f);
            suDesc.ForeColor = Theme.Sub;
            StyleLabel(lblFacts, false, 9f);
            lblFacts.ForeColor = Theme.Sub;
            Controls.Add(suTitle);
            Controls.Add(suDesc);
            Controls.Add(lblFacts);
            swStartup.CheckedChanged += delegate { OnStartupChanged(); };
            Controls.Add(swStartup);
            btnStartSvc.Kind = BtnKind.Secondary;
            btnStartSvc.Click += delegate { StartService(); };
            Controls.Add(btnStartSvc);

            StyleLabel(lblLang, true, 10.5f);
            Controls.Add(lblLang);
            ddLang.SelectionChanged += delegate
            {
                if (suppress) return;
                List<Language> all = Loc.Languages;
                if (ddLang.SelectedIndex >= 0 && ddLang.SelectedIndex < all.Count) SetLanguage(all[ddLang.SelectedIndex].Code);
            };
            Controls.Add(ddLang);
        }

        // ---- text, pages and layout -------------------------------------------------------------

        private static string EveryText(int s) { return s < 60 ? Loc.F("rec.s", s) : Loc.F("rec.min", s / 60); }

        private static string StopText(int m)
        {
            return m == 0 ? Loc.T("rec.untilstopped") : m < 60 ? Loc.F("rec.min", m) : Loc.F("rec.h", m / 60);
        }

        private void ApplyTexts()
        {
            Text = "XTU Profile Switcher";
            tabs.Items.Clear();
            tabs.Items.Add(Loc.T("tab.profiles"));
            tabs.Items.Add(Loc.T("tab.monitor"));
            tabs.Items.Add(Loc.T("tab.settings"));
            btnNew.Text = "+  " + Loc.T("pr.new");
            autoTitle.Text = Loc.T("auto.title");
            autoDesc.Text = Loc.T("auto.desc");
            lblAc.Text = Loc.T("auto.ac");
            lblBat.Text = Loc.T("auto.battery");
            suTitle.Text = Loc.T("su.title");
            suDesc.Text = Loc.T("su.desc");
            btnStartSvc.Text = Loc.T("btn.startsvc");
            lblLang.Text = Loc.T("lang.title");

            rowTemp.Title = Loc.T("mon.temp");
            rowPower.Title = Loc.T("mon.power");
            rowUsage.Title = Loc.T("mon.usage");
            foreach (SeriesRow r in new SeriesRow[] { rowTemp, rowPower, rowUsage })
            {
                r.SpanLabel = Loc.F("mon.span", 5);
                r.NowLabel = Loc.T("mon.now");
            }
            rowTemp.Unit = "°C"; rowPower.Unit = "W"; rowUsage.Unit = "%";
            recTitle.Text = Loc.T("rec.title");
            capEvery.Text = Loc.T("rec.every");
            capFor.Text = Loc.T("rec.for");
            lblCore.Text = Loc.T("rec.percore");
            lblGpu.Text = Loc.T("rec.gpu");
            btnOpen.Text = Loc.T("rec.open");

            suppress = true;
            try
            {
                // Language names are shown in their own language so anyone can find theirs.
                ddLang.Items.Clear();
                List<Language> all = Loc.Languages;
                int il = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    ddLang.Items.Add(all[i].Name);
                    if (all[i].Code == Loc.Lang) il = i;
                }
                ddLang.SelectedIndex = il;

                int ie = Array.IndexOf(EverySeconds, settings.RecEvery), im = Array.IndexOf(StopAfterMinutes, settings.RecMinutes);
                ddEvery.Items.Clear();
                foreach (int s in EverySeconds) ddEvery.Items.Add(EveryText(s));
                ddEvery.SelectedIndex = ie < 0 ? 0 : ie;
                ddFor.Items.Clear();
                foreach (int m in StopAfterMinutes) ddFor.Items.Add(StopText(m));
                ddFor.SelectedIndex = im < 0 ? 0 : im;
                swCore.Set(settings.RecPerCore);
                swGpu.Set(settings.RecGpu);
            }
            finally { suppress = false; }

            comboKey = "";
            tabs.Invalidate();
            top.Invalidate();
            table.Invalidate();
            readout.Invalidate();
            UpdateRecordingUi();
            ShowPage();
        }

        // Show the controls of the current tab only.
        private void ShowPage()
        {
            int p = Page;
            table.Visible = p == 0;
            btnNew.Visible = p == 0;
            foreach (Control c in new Control[] { rowTemp, rowPower, rowUsage, lblDetails, recTitle, capEvery, capFor, ddEvery, ddFor, swCore, swGpu, lblCore, lblGpu, btnRec, lblRec })
                c.Visible = p == 1;
            btnOpen.Visible = p == 1 && monitor.LastPath.Length > 0;
            foreach (Control c in new Control[] { autoTitle, autoDesc, lblAc, lblBat, swAuto, ddAc, ddBat, suTitle, suDesc, swStartup, lblFacts, lblLang, ddLang })
                c.Visible = p == 2;
            btnStartSvc.Visible = p == 2 && last.ServiceState != "Running";
            UpdateMonitorSubscription();
            Invalidate();
        }

        private void Relayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int m = Ui.S(28), cw = W - 2 * m;

            top.SetBounds(0, Ui.S(8), W, Ui.S(48));
            int y = Ui.S(62);
            readout.SetBounds(0, y, W, Ui.S(134));
            y += Ui.S(134) + Ui.S(6);
            tabs.SetBounds(0, y, W, Ui.S(40));
            btnNew.SetBounds(m + cw - Ui.S(160), y + Ui.S(4), Ui.S(160), Ui.S(30));
            y += Ui.S(40);

            statusY = H - Ui.S(14) - Ui.S(24);
            statusLabel.SetBounds(m + Ui.S(16), statusY, cw - Ui.S(16), Ui.S(24));
            int pageTop = y + Ui.S(10), pageBottom = statusY - Ui.S(8);

            // profiles tab
            table.SetBounds(m, pageTop, cw, Math.Max(Ui.S(100), pageBottom - pageTop));

            // monitor tab: three charts share what is left above the recording block
            int recH = Ui.S(150), detailsH = Ui.S(22), gaps = Ui.S(14);
            int chartsH = Math.Max(Ui.S(3 * 56), pageBottom - pageTop - recH - detailsH - gaps);
            int rh = Math.Min(Ui.S(84), chartsH / 3);
            rowTemp.SetBounds(0, pageTop, W, rh);
            rowPower.SetBounds(0, pageTop + rh, W, rh);
            rowUsage.SetBounds(0, pageTop + 2 * rh, W, rh);
            int dy = pageTop + 3 * rh + Ui.S(4);
            lblDetails.SetBounds(m, dy, cw, detailsH);
            int ry = dy + detailsH + Ui.S(10);
            recTitle.SetBounds(m, ry, cw, Ui.S(24));
            int ya = ry + Ui.S(30);
            capEvery.SetBounds(m, ya, Ui.S(104), Ui.S(34));
            ddEvery.SetBounds(m + Ui.S(104), ya, Ui.S(110), Ui.S(34));
            capFor.SetBounds(m + Ui.S(244), ya, Ui.S(80), Ui.S(34));
            ddFor.SetBounds(m + Ui.S(328), ya, Math.Max(Ui.S(120), cw - Ui.S(328)), Ui.S(34));
            int yb = ya + Ui.S(42);
            swCore.Location = new Point(m, yb + Ui.S(3));
            lblCore.SetBounds(m + Ui.S(48), yb, Ui.S(200), Ui.S(28));
            swGpu.Location = new Point(m + Ui.S(280), yb + Ui.S(3));
            lblGpu.SetBounds(m + Ui.S(328), yb, Math.Max(Ui.S(120), cw - Ui.S(328)), Ui.S(28));
            int yc = yb + Ui.S(38);
            btnRec.SetBounds(m, yc, Ui.S(180), Ui.S(34));
            btnOpen.SetBounds(m + cw - Ui.S(130), yc, Ui.S(130), Ui.S(34));
            lblRec.SetBounds(m + Ui.S(192), yc, Math.Max(Ui.S(100), cw - Ui.S(192) - Ui.S(138)), Ui.S(34));

            // settings tab
            int a = pageTop + Ui.S(6);
            autoTitle.SetBounds(m, a, cw - Ui.S(60), Ui.S(24));
            autoDesc.SetBounds(m, a + Ui.S(24), cw - Ui.S(60), Ui.S(22));
            swAuto.Location = new Point(m + cw - swAuto.Width, a + Ui.S(2));
            int ddW = Ui.S(260);
            lblAc.SetBounds(m, a + Ui.S(58), cw - ddW - Ui.S(12), Ui.S(34));
            ddAc.SetBounds(m + cw - ddW, a + Ui.S(58), ddW, Ui.S(34));
            lblBat.SetBounds(m, a + Ui.S(98), cw - ddW - Ui.S(12), Ui.S(34));
            ddBat.SetBounds(m + cw - ddW, a + Ui.S(98), ddW, Ui.S(34));

            ruleSettings = a + Ui.S(152);
            int s = ruleSettings + Ui.S(16);
            suTitle.SetBounds(m, s, cw - Ui.S(60), Ui.S(24));
            suDesc.SetBounds(m, s + Ui.S(24), cw - Ui.S(60), Ui.S(22));
            swStartup.Location = new Point(m + cw - swStartup.Width, s + Ui.S(2));
            lblFacts.SetBounds(m, s + Ui.S(64), cw - Ui.S(190), Ui.S(24));
            btnStartSvc.SetBounds(m + cw - Ui.S(180), s + Ui.S(60), Ui.S(180), Ui.S(30));

            ruleLang = s + Ui.S(106);
            int l = ruleLang + Ui.S(16);
            lblLang.SetBounds(m, l, cw - ddW - Ui.S(12), Ui.S(34));
            ddLang.SetBounds(m + cw - ddW, l, ddW, Ui.S(34));

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Ground);
            int m = Ui.S(28);
            if (Page == 2)
                using (Pen line = new Pen(Theme.Line))
                {
                    g.DrawLine(line, m, ruleSettings, ClientSize.Width - m, ruleSettings);
                    g.DrawLine(line, m, ruleLang, ClientSize.Width - m, ruleLang);
                }
            if (statusLabel.Text.Length > 0)
                Ui.Dot(g, statusError ? Theme.Bad : Theme.Sub, m + Ui.S(4f), statusY + Ui.S(12f), Ui.S(7f));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.TitleBar(Handle);
            if (!preview) RegisterHotkeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotkeys();
            base.OnHandleDestroyed(e);
        }

        // Start hidden in the tray when asked to, without flashing a window.
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
                Init();
            }
            base.SetVisibleCore(value);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                string name;
                if (registered.TryGetValue(m.WParam.ToInt32(), out name)) ApplyByName(name);
                return;
            }
            base.WndProc(ref m);
        }

        private void Init()
        {
            if (inited) return;
            inited = true;
            suppress = true;
            try { swStartup.Set(!preview && StartupTask.Exists()); } catch { }
            suppress = false;
            if (preview) tabs.SelectedIndex = PreviewTab;
            RefreshAll();
            monitor.Profiles = profiles;
            if (preview)
            {
                // Self-tests: click a row as a user would (this really applies it); record for a few seconds.
                if (PreviewClick != null) table.TestClick(table.RowPoint(PreviewClick, false), MouseButtons.Left);
                if (PreviewTab == 1)
                {
                    monitor.Retain(); monRetained = true;
                    if (PreviewRecord && PreviewRecordPath.Length > 0) { monitor.StartRecording(PreviewRecordPath, 1, 0, true, true); UpdateRecordingUi(); }
                }
                return;
            }
            CheckPowerSource(false);   // apply the profile for the current power source once at start
            timer.Start();
            UpdateMonitorSubscription();
        }

        // Called by the snapshot tool after it has waited for readings to arrive.
        public void PreviewUpdate() { UpdateMonitorUi(); }

        private void ShowWindow()
        {
            startHidden = false;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshAll();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!reallyExit && !preview && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                Osd.Flash("XTU Profile Switcher", Loc.T("msg.trayhint"), false);
                return;
            }
            timer.Stop();
            monTimer.Stop();
            resumeTimer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            tray.Visible = false;
            monitor.Dispose();      // finishes a running recording cleanly
            client.Dispose();
        }

        private void SetLanguage(string lang)
        {
            if (Loc.Lang == lang || !Loc.Set(lang)) return;
            settings.Lang = lang;
            settings.Save();
            ApplyTexts();
            RefreshAll();
            UpdateMonitorUi();
        }

        private void Say(string text, bool error)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = error ? Theme.Bad : Theme.Sub;
            statusError = error;
            Invalidate(new Rectangle(0, statusY, ClientSize.Width, Ui.S(26)));
            Log.Write((error ? "ERROR " : "INFO  ") + text);
        }

        // ---- monitor and recording ------------------------------------------------------------

        // Sample live values only while the Monitor tab is on screen (a recording keeps its own sampling).
        private void UpdateMonitorSubscription()
        {
            bool want = Page == 1 && Visible && !startHidden;
            if (want && !monRetained) { monitor.Retain(); monRetained = true; }
            else if (!want && monRetained && !preview) { monitor.Release(); monRetained = false; }
            bool needTimer = monRetained || monitor.Recording;
            if (needTimer && !monTimer.Enabled && !preview) { monTimer.Start(); UpdateMonitorUi(); }
            else if (!needTimer && monTimer.Enabled) monTimer.Stop();
        }

        private static string Hms(TimeSpan t)
        {
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private static double[] Series(List<Sample> h, Func<Sample, double> pick)
        {
            double[] v = new double[h.Count];
            for (int i = 0; i < h.Count; i++) v[i] = pick(h[i]);
            return v;
        }

        private void UpdateMonitorUi()
        {
            UpdateRecordingUi();
            if (Page != 1) return;

            Sample s = monitor.Latest;
            List<Sample> h = monitor.History(300);

            // The time axis grows with the data: 1 minute, then 2, then 5.
            int spanS = h.Count <= 60 ? 60 : h.Count <= 120 ? 120 : 300;
            foreach (SeriesRow r in new SeriesRow[] { rowTemp, rowPower, rowUsage })
            {
                r.Capacity = spanS;
                r.SpanLabel = Loc.F("mon.span", spanS / 60);
            }
            rowTemp.Values = Series(h, delegate (Sample x) { return x.X(Mon.PkgTemp); });
            rowPower.Values = Series(h, delegate (Sample x) { return x.X(Mon.PkgPower); });
            rowUsage.Values = Series(h, delegate (Sample x) { return x.CpuUtil; });

            double t = s != null ? s.X(Mon.PkgTemp) : double.NaN;
            double p = s != null ? s.X(Mon.PkgPower) : double.NaN;
            double u = s != null ? s.CpuUtil : double.NaN;
            rowTemp.Current = double.IsNaN(t) ? "–" : t.ToString("0");
            rowPower.Current = double.IsNaN(p) ? "–" : p.ToString("0.0");
            rowUsage.Current = double.IsNaN(u) ? "–" : u.ToString("0");
            if (s != null && s.HaveLimits) { rowPower.Ref = (double)s.Pl1; rowPower.RefLabel = Loc.F("mon.pl1", s.Pl1.ToString("0.###")); }
            else { rowPower.Ref = double.NaN; rowPower.RefLabel = ""; }
            rowTemp.Invalidate(); rowPower.Invalidate(); rowUsage.Invalidate();

            if (s == null) { lblDetails.Text = Loc.T("mon.waiting"); return; }
            List<string> parts = new List<string>();
            double f = s.X(Mon.MaxPFreq);
            if (!double.IsNaN(f)) parts.Add(Loc.T("mon.clock") + " " + f.ToString("0.00") + " GHz");
            double hot = s.MaxCoreTemp();
            if (!double.IsNaN(hot)) parts.Add(Loc.T("mon.coremax") + " " + hot.ToString("0") + " °C");
            if (!double.IsNaN(s.GpuTemp))
                parts.Add(Loc.T("mon.gpu") + " " + s.GpuTemp.ToString("0") + " °C" + (double.IsNaN(s.GpuPower) ? "" : " · " + s.GpuPower.ToString("0") + " W"));
            List<string> thr = new List<string>();
            if (s.X(Mon.CpuThrottle) > 0) thr.Add(Loc.T("mon.thermal"));
            if (s.X(Mon.PowerThrottle) > 0) thr.Add(Loc.T("mon.powerlimit"));
            if (s.X(Mon.CurrentThrottle) > 0) thr.Add(Loc.T("mon.current"));
            if (s.X(Mon.VrThrottle) > 0) thr.Add(Loc.T("mon.vr"));
            parts.Add(Loc.T("mon.throttle") + ": " + (thr.Count == 0 ? Loc.T("mon.none") : string.Join(", ", thr.ToArray())));
            lblDetails.Text = string.Join("   ·   ", parts.ToArray());
        }

        private void UpdateRecordingUi()
        {
            bool rec = monitor.Recording;
            btnRec.Kind = rec ? BtnKind.Secondary : BtnKind.Primary;
            btnRec.Text = rec ? Loc.T("rec.stop") : Loc.T("rec.start");
            btnRec.Invalidate();
            if (rec)
                lblRec.Text = Loc.F("rec.status", Hms(DateTime.Now - monitor.RecStarted), monitor.RecRows);
            else if (monitor.LastPath.Length > 0)
                lblRec.Text = Loc.F("rec.saved", monitor.LastRows) + " · " + Path.GetFileName(monitor.LastPath);
            else
                lblRec.Text = Loc.T("rec.idle");
            lblRec.ForeColor = rec ? Theme.Ember : Theme.Sub;
            btnOpen.Visible = Page == 1 && monitor.LastPath.Length > 0;
            ddEvery.Enabled = ddFor.Enabled = swCore.Enabled = swGpu.Enabled = !rec;
        }

        private static string LogDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XtuProfileSwitcher", "logs");
        }

        private void ToggleRecording()
        {
            if (monitor.Recording) { StopRecording(); return; }
            string path = Path.Combine(LogDirectory(), "xtu-log-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv");
            try
            {
                monitor.Profiles = profiles;
                monitor.StartRecording(path, settings.RecEvery, settings.RecMinutes, settings.RecPerCore, settings.RecGpu);
                Osd.Flash(Loc.T("rec.osd.start"), Path.GetFileName(path), false);
                Say(Loc.T("rec.osd.start") + ": " + path, false);
            }
            catch (Exception ex) { Say(Loc.F("rec.err", ex.Message), true); }
            UpdateMonitorSubscription();
            UpdateMonitorUi();
        }

        private void StopRecording()
        {
            long rows;
            string path = monitor.StopRecording(out rows);
            Osd.Flash(Loc.T("rec.osd.saved"), Loc.F("rec.saved", rows), false);
            Say(Loc.F("rec.saved", rows) + ": " + path, false);
            UpdateMonitorSubscription();
            UpdateMonitorUi();
            ShowPage();
        }

        // A timed recording finished by itself.
        private void OnRecordingEnded()
        {
            Osd.Flash(Loc.T("rec.osd.saved"), Loc.F("rec.saved", monitor.LastRows), false);
            Say(Loc.F("rec.saved", monitor.LastRows) + ": " + monitor.LastPath, false);
            UpdateMonitorSubscription();
            UpdateMonitorUi();
            ShowPage();
        }

        private void OpenLogFolder()
        {
            try
            {
                string p = monitor.LastPath;
                if (p.Length > 0 && File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                else { Directory.CreateDirectory(LogDirectory()); Process.Start("explorer.exe", "\"" + LogDirectory() + "\""); }
            }
            catch (Exception ex) { Say(ex.Message, true); }
        }

        // ---- hotkeys ----------------------------------------------------------------------------

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();
            List<string> failed = new List<string>();
            int id = 1;
            foreach (KeyValuePair<string, string> kv in hotkeys)
            {
                Hotkey h = Hotkey.Parse(kv.Value);
                if (h == null) { failed.Add(kv.Value); continue; }
                if (RegisterHotKey(Handle, id, h.Mods | Hotkey.NoRepeat, (uint)h.Key))
                {
                    registered[id] = kv.Key;
                    Log.Write("hotkey " + h + " -> " + kv.Key);
                    id++;
                }
                else failed.Add(h.ToString());
            }
            if (failed.Count > 0) Say(Loc.F("msg.hk.fail", string.Join(", ", failed.ToArray())), true);
        }

        private void UnregisterHotkeys()
        {
            if (!IsHandleCreated) return;
            foreach (int id in registered.Keys) UnregisterHotKey(Handle, id);
            registered.Clear();
        }

        private void ShowRowMenu(Row row, Point anchor)
        {
            ContextMenuStrip menu = Menus.Create();
            menu.Items.Add(Menus.Item(Loc.T("btn.hotkey"), delegate { EditHotkey(row); }));
            if (row.Custom)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Menus.Item(Loc.T("btn.delete"), delegate { DeleteProfile(row); }));
            }
            menu.Show(table, anchor);
        }

        private void EditHotkey(Row row)
        {
            string current;
            hotkeys.TryGetValue(row.Name, out current);

            using (HotkeyDialog dlg = new HotkeyDialog(row.Name, current))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;

                string text = dlg.Result;
                if (text.Length > 0)
                {
                    Hotkey h = Hotkey.Parse(text);
                    if (h == null) { Say(Loc.T("msg.hk.invalid"), true); return; }

                    foreach (KeyValuePair<string, string> kv in hotkeys)
                    {
                        Hotkey other = Hotkey.Parse(kv.Value);
                        if (other != null && kv.Key != row.Name && other.Canonical() == h.Canonical())
                        { Say(Loc.F("msg.hk.dup", h, kv.Key), true); return; }
                    }
                    if (Hotkey.XtuCombos().Contains(h.Canonical())) { Say(Loc.F("msg.hk.xtu", h), true); return; }
                    text = h.ToString();
                }

                if (text.Length == 0) hotkeys.Remove(row.Name); else hotkeys[row.Name] = text;
                settings.Save();
                RegisterHotkeys();
                RefreshAll();
                Say(text.Length == 0 ? Loc.F("msg.hk.cleared", row.Name) : Loc.F("msg.hk.set", text, row.Name), false);
            }
        }

        private void ApplyByName(string name)
        {
            foreach (Profile p in profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { ApplyProfile(p, true, null); return; }
            Osd.Flash(Loc.T("msg.noprofile"), name, true);
        }

        // ---- start with Windows -----------------------------------------------------------------

        private void OnStartupChanged()
        {
            if (suppress) return;
            try
            {
                if (swStartup.Checked) { StartupTask.Enable(); Say(Loc.T("msg.su.on"), false); }
                else { StartupTask.Disable(); Say(Loc.T("msg.su.off"), false); }
            }
            catch (Exception ex)
            {
                Say(Loc.F("msg.su.fail", ex.Message), true);
                suppress = true;
                try { swStartup.Set(StartupTask.Exists()); } catch { }
                suppress = false;
            }
        }

        // ---- refresh ----------------------------------------------------------------------------

        private static string OnOff(string s) { return s == "On" ? Loc.T("on") : s == "Off" ? Loc.T("off") : "?"; }

        private static string PowerText(PowerLineStatus s)
        {
            return s == PowerLineStatus.Online ? Loc.T("power.ac") : s == PowerLineStatus.Offline ? Loc.T("power.battery") : Loc.T("power.unknown");
        }

        private void RefreshAll()
        {
            last = Model.Read(client);
            if (preview && SimulateOffline)
            {
                last.Connected = false;
                last.Error = "No endpoint was listening at net.pipe://localhost/ExtremeTuningUtility/Tuning.";
            }
            profiles = Model.LoadProfiles(last, settings.Customs);
            Profile active = Model.ActiveProfile(profiles, last);

            bool running = last.ServiceState == "Running";
            top.Status = last.Connected ? Loc.T("status.connected") : (running ? Loc.T("status.offline") : Loc.T("status.stopped"));
            top.StatusColor = last.Connected ? Theme.Ok : Theme.Bad;
            top.Invalidate();

            readout.Connected = last.Connected;
            // Say what to do, not the raw exception (that goes to the log).
            readout.Problem = running ? Loc.T("ro.hint.noanswer") : Loc.T("ro.hint.stopped");
            if (!last.Connected && last.Error.Length > 0) Log.Write("not connected: " + last.Error);
            readout.Pl1 = last.Pl1; readout.Pl2 = last.Pl2; readout.Pl2On = last.Pl2Enable; readout.Window = last.Window;
            if (last.Connected) { readout.BiosPl1 = last.DefPl1; readout.BiosPl2 = last.DefPl2; }
            readout.ProfileName = active != null ? active.Name : null;
            readout.PowerText = PowerText(SystemInformation.PowerStatus.PowerLineStatus);
            readout.Invalidate();

            List<Row> rows = new List<Row>();
            foreach (Profile p in profiles)
            {
                string hk;
                hotkeys.TryGetValue(p.Name, out hk);
                Row r = new Row();
                r.Name = p.Name; r.Pl1 = p.Pl1; r.Pl2 = p.Pl2; r.Window = p.Window; r.Pl2On = p.Pl2Enable;
                r.Active = p == active; r.Custom = p.Custom; r.Key = hk; r.Tag = p;
                rows.Add(r);
            }
            table.SetRows(rows);

            lblFacts.Text = last.Connected
                ? Loc.T("facts.restore") + ": " + OnOff(last.RestoreOnBoot ? "On" : "Off") + "   ·   " + Loc.T("facts.fast") + ": " + OnOff(last.FastStartup)
                : "";
            btnStartSvc.Visible = Page == 2 && !running;

            FillCombos();
            RebuildTray(active);
        }

        private void RebuildTray(Profile active)
        {
            ContextMenuStrip menu = Menus.Create();
            foreach (Profile p in profiles)
            {
                Profile captured = p;
                string hk;
                hotkeys.TryGetValue(p.Name, out hk);
                ToolStripMenuItem mi = Menus.Item(p.Name + "    " + p.Pl1.ToString("0.###") + "/" + p.Pl2.ToString("0.###") + " W"
                    + (string.IsNullOrEmpty(hk) ? "" : "    " + hk), delegate { ApplyProfile(captured, true, null); });
                mi.Checked = p == active;
                menu.Items.Add(mi);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Menus.Item(monitor.Recording ? Loc.T("tray.rec.stop") : Loc.T("tray.rec.start"), delegate { ToggleRecording(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Menus.Item(Loc.T("tray.open"), delegate { ShowWindow(); }));
            menu.Items.Add(Menus.Item(Loc.T("tray.quit"), delegate { reallyExit = true; Close(); Application.Exit(); }));
            ContextMenuStrip old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();

            string tip = last.Connected
                ? Loc.F("tray.tip", last.Pl1.ToString("0.###"), last.Pl2.ToString("0.###"), active != null ? active.Name : Loc.T("tray.nomatch"))
                : Loc.T("tray.offline");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        // ---- apply ------------------------------------------------------------------------------

        // reason: non-null when the switch was automatic (e.g. "On battery"), shown in the notice.
        private void ApplyProfile(Profile p, bool notify, string reason)
        {
            if (busy) return;
            busy = true;
            try
            {
                try
                {
                    if (!notify) { Say(Loc.F("msg.applying", p.Name), false); statusLabel.Update(); }
                    if (!client.IsOpen) client.Connect();
                    string msg = client.Apply(p);
                    Say((reason != null ? reason + " · " : "") + p.Name + ": " + msg, false);
                    RefreshAll();
                    monitor.Profiles = profiles;
                    if (notify)
                    {
                        string values = last.Connected
                            ? string.Format("PL1 {0:0.###} W · PL2 {1:0.###} W · {2:0.###} s", last.Pl1, last.Pl2, last.Window)
                            : msg;
                        if (msg == Loc.T("msg.already")) values = msg + " · " + values;
                        if (reason != null) values = reason + " · " + values;
                        Osd.Flash(p.Name, values, false);
                    }
                }
                catch (Exception ex)
                {
                    client.Close();
                    Say(Loc.F("msg.failed", p.Name, ex.Message), true);
                    RefreshAll();
                    if (notify) Osd.Flash(Loc.F("msg.failed", p.Name, ""), ex.Message, true);
                }
            }
            finally { busy = false; }
        }

        // ---- profiles made in this tool ---------------------------------------------------------

        private void AddProfile()
        {
            List<string> used = new List<string>();
            foreach (Profile p in profiles) used.Add(p.Name);

            using (ProfileDialog dlg = new ProfileDialog(used))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                settings.Customs.Add(dlg.Result);
                settings.Save();
                RefreshAll();
                Say(Loc.F("msg.created", dlg.Result.Name), false);
            }
        }

        private void DeleteProfile(Row row)
        {
            if (!row.Custom) { Say(Loc.T("msg.onlycustom"), true); return; }
            if (MessageBox.Show(this, Loc.F("msg.confirmdel", row.Name), "XTU Profile Switcher",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            string name = row.Name;
            settings.Customs.RemoveAll(delegate (Profile c) { return c.Name == name; });
            settings.Hotkeys.Remove(name);
            if (string.Equals(settings.AcProfile, name, StringComparison.OrdinalIgnoreCase)) settings.AcProfile = "";
            if (string.Equals(settings.BatteryProfile, name, StringComparison.OrdinalIgnoreCase)) settings.BatteryProfile = "";
            settings.Save();
            RegisterHotkeys();
            comboKey = "";
            RefreshAll();
            Say(Loc.F("msg.deleted", name), false);
        }

        // ---- automatic switch by power source ---------------------------------------------------

        private string ComboValue(Dropdown d)
        {
            return d.SelectedIndex <= 0 || d.SelectedIndex >= d.Items.Count ? "" : d.Items[d.SelectedIndex];
        }

        // Rebuild the two lists only when the profiles or the saved choice change.
        private void FillCombos()
        {
            StringBuilder key = new StringBuilder(Loc.Lang);
            foreach (Profile p in profiles) key.Append('|').Append(p.Name);
            key.Append('|').Append(settings.AcProfile).Append('|').Append(settings.BatteryProfile).Append('|').Append(settings.AutoSwitch);
            if (key.ToString() == comboKey) return;
            comboKey = key.ToString();

            suppress = true;
            try
            {
                swAuto.Set(settings.AutoSwitch);
                FillCombo(ddAc, settings.AcProfile);
                FillCombo(ddBat, settings.BatteryProfile);
            }
            finally { suppress = false; }
        }

        private void FillCombo(Dropdown d, string selected)
        {
            d.Items.Clear();
            d.Items.Add(Loc.T("auto.none"));
            foreach (Profile p in profiles) d.Items.Add(p.Name);
            int idx = 0;
            for (int i = 1; i < d.Items.Count; i++)
                if (string.Equals(d.Items[i], selected, StringComparison.OrdinalIgnoreCase)) idx = i;
            d.SelectedIndex = idx;
        }

        private void OnAutoChanged(Control sender)
        {
            if (suppress) return;
            settings.AutoSwitch = swAuto.Checked;
            settings.AcProfile = ComboValue(ddAc);
            settings.BatteryProfile = ComboValue(ddBat);
            settings.Save();
            comboKey = "";

            PowerLineStatus now = SystemInformation.PowerStatus.PowerLineStatus;
            Say(settings.AutoSwitch
                ? Loc.F("msg.autoon", settings.AcProfile.Length > 0 ? settings.AcProfile : Loc.T("auto.none"),
                    settings.BatteryProfile.Length > 0 ? settings.BatteryProfile : Loc.T("auto.none"))
                : Loc.T("msg.autooff"), false);

            // Apply right away only when the change concerns the power source in use now.
            bool applyNow = settings.AutoSwitch
                && (sender == swAuto
                    || (sender == ddAc && now == PowerLineStatus.Online)
                    || (sender == ddBat && now == PowerLineStatus.Offline));
            CheckPowerSource(applyNow);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            // Raised on a system thread; hop to the UI thread.
            bool resumed = e.Mode == PowerModes.Resume;
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(delegate
                {
                    CheckPowerSource(false);
                    // Power limits can fall back to firmware defaults around sleep, so re-apply the
                    // profile for the current power source shortly after waking (if auto-switch is on).
                    if (resumed && settings.AutoSwitch) { resumeTimer.Stop(); resumeTimer.Start(); }
                }));
            }
            catch { }
        }

        // Applies the profile mapped to the current power source when the source changed (or when forced).
        private void CheckPowerSource(bool force)
        {
            PowerLineStatus now = SystemInformation.PowerStatus.PowerLineStatus;
            string text = PowerText(now);
            if (readout.PowerText != text) { readout.PowerText = text; readout.Invalidate(); }
            if (now == PowerLineStatus.Unknown) return;

            bool changed = now != lastLine;
            lastLine = now;
            if (!settings.AutoSwitch || (!changed && !force)) return;

            bool onAc = now == PowerLineStatus.Online;
            string name = onAc ? settings.AcProfile : settings.BatteryProfile;
            if (string.IsNullOrEmpty(name)) return;
            string reason = onAc ? Loc.T("reason.ac") : Loc.T("reason.battery");

            foreach (Profile p in profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyProfile(p, true, reason);
                    return;
                }
            Say(Loc.F("msg.auto.missing", name, reason), true);
        }

        private void StartService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(Paths.ServiceName))
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
                Say(Loc.T("msg.svc.ok"), false);
            }
            catch (Exception ex) { Say(Loc.F("msg.svc.fail", ex.Message), true); }
            RefreshAll();
        }
    }
}
