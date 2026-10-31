// The main window and the tray icon. All window text comes from Loc; all colours from Theme.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly XtuClient client = new XtuClient();
        private readonly AppSettings settings;
        private readonly bool preview;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer resumeTimer = new System.Windows.Forms.Timer();

        private readonly TopBar top = new TopBar();
        private readonly Readout readout = new Readout();
        private readonly Label lblProfiles = new Label();
        private readonly Btn btnNew = new Btn();
        private readonly ProfileTable table = new ProfileTable();
        private readonly Btn btnApply = new Btn();
        private readonly Btn btnHotkey = new Btn();
        private readonly Btn btnDelete = new Btn();
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
        private readonly Label statusLabel = new Label();

        private List<Profile> profiles = new List<Profile>();
        private Snapshot last = new Snapshot();
        private readonly Dictionary<int, string> registered = new Dictionary<int, string>();
        private PowerLineStatus lastLine = PowerLineStatus.Unknown;
        private bool reallyExit, startHidden, busy, inited, suppress;
        private int tick, ruleAuto, ruleFooter, statusY;
        private bool statusError;
        private string comboKey = "";

        private Dictionary<string, string> hotkeys { get { return settings.Hotkeys; } }

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
            int h = Math.Min(Ui.S(800), (int)(wa.Height * 0.92) - Ui.S(48));
            ClientSize = new Size(Ui.S(640), Math.Max(Ui.S(640), h));
            MinimumSize = SizeFromClientSize(new Size(Ui.S(600), Ui.S(640)));

            top.LanguageChosen += delegate (object s, LangEventArgs e) { SetLanguage(e.Lang); };
            Controls.Add(top);
            Controls.Add(readout);

            StyleLabel(lblProfiles, true, 10.5f);
            Controls.Add(lblProfiles);
            btnNew.Kind = BtnKind.Quiet;
            btnNew.Click += delegate { AddProfile(); };
            Controls.Add(btnNew);

            table.Activated += delegate { ApplySelected(); };
            table.SelectedChanged += delegate { UpdateButtons(); };
            Controls.Add(table);

            btnApply.Kind = BtnKind.Primary;
            btnApply.Click += delegate { ApplySelected(); };
            Controls.Add(btnApply);
            btnHotkey.Kind = BtnKind.Secondary;
            btnHotkey.Click += delegate { EditHotkey(); };
            Controls.Add(btnHotkey);
            btnDelete.Kind = BtnKind.Quiet;
            btnDelete.Click += delegate { DeleteProfile(); };
            Controls.Add(btnDelete);

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

            StyleLabel(statusLabel, false, 9.5f);
            statusLabel.ForeColor = Theme.Sub;
            Controls.Add(statusLabel);

            tray.Icon = AppIcon.Load(SystemInformation.SmallIconSize.Width);
            tray.Visible = !preview;
            tray.DoubleClick += delegate { ShowWindow(); };

            timer.Interval = 5000;
            timer.Tick += delegate
            {
                // Refresh less often while hidden in the tray; the power check is cheap.
                tick++;
                if (Visible || tick % 3 == 0) RefreshAll();
                CheckPowerSource(false);
            };
            resumeTimer.Interval = 4000;
            resumeTimer.Tick += delegate { resumeTimer.Stop(); CheckPowerSource(true); };
            if (!preview) SystemEvents.PowerModeChanged += OnPowerModeChanged;

            Load += delegate { Init(); };
            FormClosing += OnFormClosing;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            SizeChanged += delegate { Relayout(); };

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

        // ---- text and layout -----------------------------------------------------------------

        private void ApplyTexts()
        {
            Text = "XTU Profile Switcher";
            lblProfiles.Text = Loc.T("pr.title");
            btnNew.Text = "+  " + Loc.T("pr.new");
            btnApply.Text = Loc.T("btn.apply");
            btnHotkey.Text = Loc.T("btn.hotkey");
            btnDelete.Text = Loc.T("btn.delete");
            autoTitle.Text = Loc.T("auto.title");
            autoDesc.Text = Loc.T("auto.desc");
            lblAc.Text = Loc.T("auto.ac");
            lblBat.Text = Loc.T("auto.battery");
            suTitle.Text = Loc.T("su.title");
            suDesc.Text = Loc.T("su.desc");
            btnStartSvc.Text = Loc.T("btn.startsvc");
            comboKey = "";
            top.Invalidate();
            table.Invalidate();
            readout.Invalidate();
        }

        private void Relayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            int m = Ui.S(28), cw = W - 2 * m;

            top.SetBounds(0, Ui.S(8), W, Ui.S(48));
            int y = Ui.S(62);
            readout.SetBounds(0, y, W, Ui.S(134));
            y += Ui.S(134) + Ui.S(12);

            // from the bottom up
            int yb = H - Ui.S(14);
            statusY = yb - Ui.S(24);
            statusLabel.SetBounds(m + Ui.S(16), statusY, cw - Ui.S(16), Ui.S(24));
            yb = statusY - Ui.S(8);

            int footerTop = yb - Ui.S(84);
            ruleFooter = footerTop;
            suTitle.SetBounds(m, footerTop + Ui.S(12), cw - Ui.S(60), Ui.S(24));
            suDesc.SetBounds(m, footerTop + Ui.S(36), cw - Ui.S(60), Ui.S(22));
            swStartup.Location = new Point(m + cw - swStartup.Width, footerTop + Ui.S(14));
            lblFacts.SetBounds(m, footerTop + Ui.S(58), cw - Ui.S(190), Ui.S(22));
            btnStartSvc.SetBounds(m + cw - Ui.S(180), footerTop + Ui.S(52), Ui.S(180), Ui.S(30));
            yb = footerTop;

            int autoTop = yb - Ui.S(150);
            ruleAuto = autoTop;
            autoTitle.SetBounds(m, autoTop + Ui.S(12), cw - Ui.S(60), Ui.S(24));
            autoDesc.SetBounds(m, autoTop + Ui.S(36), cw - Ui.S(60), Ui.S(22));
            swAuto.Location = new Point(m + cw - swAuto.Width, autoTop + Ui.S(14));
            int ddW = Ui.S(260);
            lblAc.SetBounds(m, autoTop + Ui.S(66), cw - ddW - Ui.S(12), Ui.S(34));
            ddAc.SetBounds(m + cw - ddW, autoTop + Ui.S(66), ddW, Ui.S(34));
            lblBat.SetBounds(m, autoTop + Ui.S(106), cw - ddW - Ui.S(12), Ui.S(34));
            ddBat.SetBounds(m + cw - ddW, autoTop + Ui.S(106), ddW, Ui.S(34));
            yb = autoTop - Ui.S(12);

            int actionsTop = yb - Ui.S(34);
            btnApply.SetBounds(m, actionsTop, Ui.S(110), Ui.S(34));
            btnHotkey.SetBounds(m + Ui.S(120), actionsTop, Ui.S(150), Ui.S(34));
            btnDelete.SetBounds(m + Ui.S(280), actionsTop, Ui.S(100), Ui.S(34));

            lblProfiles.SetBounds(m, y, Ui.S(200), Ui.S(26));
            btnNew.SetBounds(m + cw - Ui.S(160), y - Ui.S(3), Ui.S(160), Ui.S(32));
            int tableTop = y + Ui.S(34);
            table.SetBounds(m, tableTop, cw, Math.Max(Ui.S(100), actionsTop - Ui.S(8) - tableTop));

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Ground);
            int m = Ui.S(28);
            using (Pen line = new Pen(Theme.Line))
            {
                g.DrawLine(line, m, ruleAuto, ClientSize.Width - m, ruleAuto);
                g.DrawLine(line, m, ruleFooter, ClientSize.Width - m, ruleFooter);
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
            RefreshAll();
            if (preview) return;
            CheckPowerSource(false);   // apply the profile for the current power source once at start
            timer.Start();
        }

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
            resumeTimer.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            tray.Visible = false;
            client.Dispose();
        }

        private void SetLanguage(string lang)
        {
            if (Loc.Lang == lang) return;
            Loc.Lang = lang;
            settings.Lang = lang;
            settings.Save();
            ApplyTexts();
            RefreshAll();
        }

        private void Say(string text, bool error)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = error ? Theme.Bad : Theme.Sub;
            statusError = error;
            Invalidate(new Rectangle(0, statusY, ClientSize.Width, Ui.S(26)));
            Log.Write((error ? "ERROR " : "INFO  ") + text);
        }

        // ---- hotkeys --------------------------------------------------------------------------

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

        private void EditHotkey()
        {
            Row row = table.SelectedRow;
            if (row == null) { Say(Loc.T("msg.pick"), true); return; }
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

        // ---- start with Windows ---------------------------------------------------------------

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

        // ---- refresh --------------------------------------------------------------------------

        private static string OnOff(string s) { return s == "On" ? Loc.T("on") : s == "Off" ? Loc.T("off") : "?"; }

        private static string PowerText(PowerLineStatus s)
        {
            return s == PowerLineStatus.Online ? Loc.T("power.ac") : s == PowerLineStatus.Offline ? Loc.T("power.battery") : Loc.T("power.unknown");
        }

        // Preview only (--snapshot --offline): draw the "can't reach XTU" state.
        public bool SimulateOffline;

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
            btnStartSvc.Visible = !running;

            FillCombos();
            UpdateButtons();
            RebuildTray(active);
        }

        private void UpdateButtons()
        {
            Row sel = table.SelectedRow;
            btnApply.Enabled = last.Connected && sel != null;
            btnHotkey.Enabled = sel != null;
            btnDelete.Enabled = sel != null && sel.Custom;
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

        // ---- apply ----------------------------------------------------------------------------

        private void ApplySelected()
        {
            Row row = table.SelectedRow;
            if (row == null) { Say(Loc.T("msg.pick"), true); return; }
            ApplyProfile((Profile)row.Tag, false, null);
        }

        // reason: non-null when the switch was automatic (e.g. "On battery"), shown in the notice.
        private void ApplyProfile(Profile p, bool notify, string reason)
        {
            if (busy) return;
            busy = true;
            try
            {
                try
                {
                    if (!client.IsOpen) client.Connect();
                    string msg = client.Apply(p);
                    Say((reason != null ? reason + " · " : "") + p.Name + ": " + msg, false);
                    RefreshAll();
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

        // ---- profiles made in this tool -------------------------------------------------------

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
                table.Select(dlg.Result.Name);
                Say(Loc.F("msg.created", dlg.Result.Name), false);
            }
        }

        private void DeleteProfile()
        {
            Row row = table.SelectedRow;
            if (row == null) { Say(Loc.T("msg.pick"), true); return; }
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

        // ---- automatic switch by power source -------------------------------------------------

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
