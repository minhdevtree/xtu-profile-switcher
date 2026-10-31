// XTU Profile Switcher
//
// Small helper for Intel Extreme Tuning Utility (XTU) 7.14.x.
//   * shows whether the XTU service is running and reachable
//   * shows the power limits that are really active right now (read from the service)
//   * works out which saved XTU profile those values correspond to
//   * applies the power-limit part of a saved profile (PL1, PL2, PL2 enable, time window)
//
// It talks to the XTU service over its WCF named pipe, using Intel's own contract
// assemblies from the XTU install folder. That interface is internal and unsupported,
// so this tool only touches four controls and validates every value before sending it.
//
// Targets .NET Framework 4.8 and C# 5 (built with the csc.exe that ships with Windows).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace XtuProfileSwitcher
{
    internal static class Paths
    {
        public const string XtuDir = @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers";
        public const string ProfilesXml = @"C:\ProgramData\Intel\Intel Extreme Tuning Utility\Profiles.xml";
        public const string ServiceName = "XTU3SERVICE";
        public const string PipeUri = "net.pipe://localhost/ExtremeTuningUtility/Tuning";

        public static string LogFile
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XtuProfileSwitcher");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "app.log");
            }
        }
    }

    internal static class Ids
    {
        // Control ids from IronCity.Common.ControlIdHelper.
        public const uint Pl2 = 47;        // TurboPackageTdpShort          (Turbo Boost Short Power Max)
        public const uint Pl1 = 48;        // TurboPackageTdpExtended       (Turbo Boost Power Max)
        public const uint Pl2Enable = 49;  // TurboPackageTdpShortEnable
        public const uint Window = 66;     // TurboExtendedTimeWindow
    }

    // Hard limits, independent of what XTU would accept.
    internal static class Limits
    {
        public const decimal MinPl1 = 15m;
        public const decimal MaxPl1 = 120m;
        public const decimal MaxPl2 = 150m;
        public const decimal MinWindow = 1m;
        public const decimal MaxWindow = 128m;
    }

    internal sealed class Profile
    {
        public string Name;
        public decimal Pl1;
        public decimal Pl2;
        public bool Pl2Enable;
        public decimal Window;
        public bool Builtin;

        public bool Matches(Snapshot s)
        {
            return s.Connected
                && Math.Abs(s.Pl1 - Pl1) < 0.01m
                && Math.Abs(s.Pl2 - Pl2) < 0.01m
                && s.Pl2Enable == Pl2Enable
                && Math.Abs(s.Window - Window) < 0.01m;
        }

        public string Describe()
        {
            return string.Format("PL1 {0:0.###} W / PL2 {1:0.###} W{2} / {3:0.###} s",
                Pl1, Pl2, Pl2Enable ? "" : " (off)", Window);
        }
    }

    internal sealed class Snapshot
    {
        public string ServiceState = "?";
        public bool Connected;
        public string Error = "";
        public decimal Pl1, Pl2, Window;
        public bool Pl2Enable;
        public decimal DefPl1, DefPl2, DefWindow;
        public bool DefPl2Enable;
        public bool RestoreOnBoot;
        public string FastStartup = "?";
    }

    internal static class Log
    {
        public static void Write(string msg)
        {
            try
            {
                File.AppendAllText(Paths.LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    // Thin wrapper around the XTU tuning pipe.
    internal sealed class XtuClient : IDisposable
    {
        private ChannelFactory<IronCity.Core.IXtuTuning> factory;
        private IronCity.Core.IXtuTuning channel;

        public bool IsOpen { get { return channel != null; } }

        public void Connect()
        {
            Close();
            NetNamedPipeBinding binding = new NetNamedPipeBinding();
            binding.OpenTimeout = TimeSpan.FromSeconds(5);
            binding.SendTimeout = TimeSpan.FromSeconds(15);
            binding.ReceiveTimeout = TimeSpan.FromSeconds(15);
            binding.MaxReceivedMessageSize = 67108864;
            binding.MaxBufferSize = 67108864;
            binding.ReaderQuotas.MaxArrayLength = 67108864;
            binding.ReaderQuotas.MaxStringContentLength = 67108864;
            binding.ReaderQuotas.MaxDepth = 64;

            factory = new ChannelFactory<IronCity.Core.IXtuTuning>(binding, new EndpointAddress(Paths.PipeUri));
            foreach (OperationDescription op in factory.Endpoint.Contract.Operations)
            {
                DataContractSerializerOperationBehavior dc = op.Behaviors.Find<DataContractSerializerOperationBehavior>();
                if (dc != null) dc.MaxItemsInObjectGraph = int.MaxValue;
            }
            channel = factory.CreateChannel();
            try
            {
                if (!channel.Connect()) throw new InvalidOperationException("Dịch vụ XTU từ chối kết nối.");
            }
            catch
            {
                Close();
                throw;
            }
        }

        public void Close()
        {
            try { if (channel != null) ((ICommunicationObject)channel).Abort(); } catch { }
            try { if (factory != null) factory.Abort(); } catch { }
            channel = null;
            factory = null;
        }

        public void Dispose() { Close(); }

        private IronCity.Core.IXtuTuning Ch
        {
            get
            {
                if (channel == null) throw new InvalidOperationException("Chưa kết nối.");
                return channel;
            }
        }

        // The service keeps a cache of "active" values that can be empty (0) until it is told to
        // re-read the hardware, so refresh it before every read.
        public void RefreshFromHardware() { Ch.RefreshActiveValuesFromHardwareAccess(); }

        // GetTuningControlByID does not return the live value on this build (it returns 0),
        // so read ActiveValue from the full control record instead.
        public decimal Active(uint id) { return Ch.GetControl(id).ActiveValue; }
        public IronCity.Core.XtuTuningControl Control(uint id) { return Ch.GetControl(id); }
        public bool RestoreOnBoot() { return Ch.GetDoRestoreUserValuesOnBoot(); }

        public string Apply(Profile p)
        {
            Validate(p);

            RefreshFromHardware();
            if (AlreadyApplied(p)) return "Đã ở sẵn giá trị của profile này, không cần đổi.";

            IronCity.Core.XtuTuningControl c1 = Control(Ids.Pl1);
            IronCity.Core.XtuTuningControl c2 = Control(Ids.Pl2);
            if (c1.ReadOnly || !c1.Enabled) throw new InvalidOperationException("PL1 đang bị khoá (chỉ đọc hoặc bị tắt).");
            if (c2.ReadOnly || !c2.Enabled) throw new InvalidOperationException("PL2 đang bị khoá (chỉ đọc hoặc bị tắt).");
            RequireSupported(c1, p.Pl1, "PL1");
            RequireSupported(c2, p.Pl2, "PL2");

            IronCity.Common.SdkClientToken token = IronCity.Common.SdkClientToken.Default;
            List<IronCity.Core.XtuTuningProposalResult> delta;
            List<IronCity.Core.XtuTuningProposalResult> discarded;

            Ch.DiscardChanges(token, out discarded);

            List<IronCity.Core.XtuTuningProposal> proposals = new List<IronCity.Core.XtuTuningProposal>();
            proposals.Add(Prop(Ids.Pl2Enable, p.Pl2Enable ? 1m : 0m));
            proposals.Add(Prop(Ids.Pl2, p.Pl2));
            proposals.Add(Prop(Ids.Pl1, p.Pl1));
            proposals.Add(Prop(Ids.Window, p.Window));

            bool requiresReboot;
            IronCity.Common.TuningResult proposed = Ch.ProposeChange(proposals, token, out delta, out requiresReboot, false);
            if (proposed.GeneralCode != IronCity.Common.TuningErrorCode.Success || requiresReboot)
            {
                Ch.DiscardChanges(token, out discarded);
                throw new InvalidOperationException("XTU từ chối giá trị: " + Describe(proposed)
                    + (requiresReboot ? " (cần khởi động lại)" : ""));
            }

            IronCity.Common.TuningResult applied = Ch.ApplyChanges(false, 1, token);
            if (applied.GeneralCode != IronCity.Common.TuningErrorCode.Success)
            {
                Ch.DiscardChanges(token, out discarded);
                throw new InvalidOperationException("Áp giá trị thất bại: " + Describe(applied));
            }

            // Read back, so the caller sees what the hardware really has.
            RefreshFromHardware();
            decimal a1 = Active(Ids.Pl1), a2 = Active(Ids.Pl2), aw = Active(Ids.Window);
            return string.Format("Đã áp dụng. PL1 = {0:0.###} W, PL2 = {1:0.###} W, Time Window = {2:0.###} s.", a1, a2, aw);
        }

        private bool AlreadyApplied(Profile p)
        {
            return Math.Abs(Active(Ids.Pl1) - p.Pl1) < 0.01m
                && Math.Abs(Active(Ids.Pl2) - p.Pl2) < 0.01m
                && (Active(Ids.Pl2Enable) != 0m) == p.Pl2Enable
                && Math.Abs(Active(Ids.Window) - p.Window) < 0.01m;
        }

        private static IronCity.Core.XtuTuningProposal Prop(uint id, decimal value)
        {
            IronCity.Core.XtuTuningProposal p = new IronCity.Core.XtuTuningProposal();
            p.Id = id;
            p.Value = value;
            return p;
        }

        private static string Describe(IronCity.Common.TuningResult r)
        {
            StringBuilder sb = new StringBuilder(r.GeneralCode.ToString());
            if (r.AllCodes != null)
            {
                foreach (KeyValuePair<uint, IronCity.Common.TuningErrorCode> kv in r.AllCodes)
                    sb.Append("; control ").Append(kv.Key).Append(" = ").Append(kv.Value);
            }
            return sb.ToString();
        }

        private static void RequireSupported(IronCity.Core.XtuTuningControl c, decimal value, string label)
        {
            if (c.SupportedValues == null) return;
            foreach (decimal v in c.SupportedValues)
                if (v == value) return;
            throw new InvalidOperationException(label + " = " + value + " W không nằm trong danh sách giá trị XTU hỗ trợ.");
        }

        public static void Validate(Profile p)
        {
            if (p.Pl1 < Limits.MinPl1 || p.Pl1 > Limits.MaxPl1)
                throw new InvalidOperationException(string.Format("PL1 = {0} W nằm ngoài khoảng an toàn {1}–{2} W.", p.Pl1, Limits.MinPl1, Limits.MaxPl1));
            if (p.Pl2 < p.Pl1 || p.Pl2 > Limits.MaxPl2)
                throw new InvalidOperationException(string.Format("PL2 = {0} W phải nằm trong khoảng PL1–{1} W.", p.Pl2, Limits.MaxPl2));
            if (p.Window < Limits.MinWindow || p.Window > Limits.MaxWindow)
                throw new InvalidOperationException(string.Format("Time Window = {0} s nằm ngoài khoảng {1}–{2} s.", p.Window, Limits.MinWindow, Limits.MaxWindow));
        }
    }

    internal static class Model
    {
        public static List<Profile> LoadProfiles(Snapshot defaults)
        {
            List<Profile> list = new List<Profile>();

            Profile def = new Profile();
            def.Name = "Default (BIOS)";
            def.Builtin = true;
            def.Pl1 = defaults != null && defaults.Connected ? defaults.DefPl1 : 90m;
            def.Pl2 = defaults != null && defaults.Connected ? defaults.DefPl2 : 115m;
            def.Pl2Enable = defaults != null && defaults.Connected ? defaults.DefPl2Enable : true;
            def.Window = defaults != null && defaults.Connected ? defaults.DefWindow : 56m;
            list.Add(def);

            if (!File.Exists(Paths.ProfilesXml)) return list;

            XmlDocument doc = new XmlDocument();
            doc.Load(Paths.ProfilesXml);
            foreach (XmlNode node in doc.SelectNodes("//DatabaseProfile"))
            {
                XmlNode nameNode = node.SelectSingleNode("Name");
                XmlNode controls = node.SelectSingleNode("ControlSettings");
                if (nameNode == null || controls == null) continue;

                Profile p = new Profile();
                p.Name = nameNode.InnerText;
                bool have1 = false, have2 = false, haveEn = false, haveW = false;
                foreach (XmlNode c in controls.ChildNodes)
                {
                    XmlNode idNode = c.SelectSingleNode("Id");
                    XmlNode valNode = c.SelectSingleNode("Value");
                    uint id;
                    decimal val;
                    if (idNode == null || valNode == null) continue;
                    if (!uint.TryParse(idNode.InnerText, out id)) continue;
                    if (!decimal.TryParse(valNode.InnerText, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out val)) continue;
                    if (id == Ids.Pl1) { p.Pl1 = val; have1 = true; }
                    else if (id == Ids.Pl2) { p.Pl2 = val; have2 = true; }
                    else if (id == Ids.Pl2Enable) { p.Pl2Enable = val != 0m; haveEn = true; }
                    else if (id == Ids.Window) { p.Window = val; haveW = true; }
                }
                if (have1 && have2 && haveEn && haveW) list.Add(p);
            }
            return list;
        }

        public static Snapshot Read(XtuClient client)
        {
            Snapshot s = new Snapshot();

            try
            {
                using (ServiceController sc = new ServiceController(Paths.ServiceName))
                    s.ServiceState = sc.Status.ToString();
            }
            catch (Exception ex) { s.ServiceState = "không tìm thấy (" + ex.Message + ")"; }

            object fs = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power",
                "HiberbootEnabled", null);
            s.FastStartup = fs == null ? "?" : (Convert.ToInt32(fs) == 0 ? "Tắt" : "Bật");

            try
            {
                if (!client.IsOpen) client.Connect();
                client.RefreshFromHardware();
                s.Pl1 = client.Active(Ids.Pl1);
                s.Pl2 = client.Active(Ids.Pl2);
                s.Pl2Enable = client.Active(Ids.Pl2Enable) != 0m;
                s.Window = client.Active(Ids.Window);

                IronCity.Core.XtuTuningControl c1 = client.Control(Ids.Pl1);
                IronCity.Core.XtuTuningControl c2 = client.Control(Ids.Pl2);
                IronCity.Core.XtuTuningControl ce = client.Control(Ids.Pl2Enable);
                IronCity.Core.XtuTuningControl cw = client.Control(Ids.Window);
                s.DefPl1 = c1.DefaultValue;
                s.DefPl2 = c2.DefaultValue;
                s.DefPl2Enable = ce.DefaultValue != 0m;
                s.DefWindow = cw.DefaultValue;
                s.RestoreOnBoot = client.RestoreOnBoot();
                s.Connected = true;
            }
            catch (Exception ex)
            {
                client.Close();
                s.Connected = false;
                s.Error = ex.Message;
            }
            return s;
        }

        public static Profile ActiveProfile(List<Profile> profiles, Snapshot s)
        {
            foreach (Profile p in profiles)
                if (p.Matches(s)) return p;
            return null;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly XtuClient client = new XtuClient();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly Timer timer = new Timer();

        private readonly Label lblService = new Label();
        private readonly Label lblPipe = new Label();
        private readonly Label lblRestore = new Label();
        private readonly Label lblFast = new Label();
        private readonly Label lblPl = new Label();
        private readonly Label lblActive = new Label();
        private readonly ListView list = new ListView();
        private readonly Button btnApply = new Button();
        private readonly Button btnRefresh = new Button();
        private readonly Button btnStart = new Button();
        private readonly Label status = new Label();

        private List<Profile> profiles = new List<Profile>();
        private Snapshot last = new Snapshot();
        private bool reallyExit;

        public MainForm()
        {
            Text = "XTU Profile Switcher";
            ClientSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            MinimumSize = new Size(560, 520);

            Label title = new Label();
            title.Text = "XTU Profile Switcher";
            title.Font = new Font("Segoe UI Semibold", 14f);
            title.AutoSize = true;
            title.Location = new Point(16, 12);
            Controls.Add(title);

            GroupBox gbStatus = new GroupBox();
            gbStatus.Text = "Trạng thái";
            gbStatus.SetBounds(16, 52, 528, 112);
            gbStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            AddRow(gbStatus, "Dịch vụ XTU", lblService, 22);
            AddRow(gbStatus, "Kết nối tới dịch vụ", lblPipe, 44);
            AddRow(gbStatus, "Khôi phục sau reboot", lblRestore, 66);
            AddRow(gbStatus, "Fast Startup", lblFast, 88);
            Controls.Add(gbStatus);

            GroupBox gbNow = new GroupBox();
            gbNow.Text = "Đang áp dụng (đọc từ dịch vụ)";
            gbNow.SetBounds(16, 172, 528, 72);
            gbNow.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            AddRow(gbNow, "Giới hạn công suất", lblPl, 22);
            AddRow(gbNow, "Profile khớp", lblActive, 44);
            Controls.Add(gbNow);

            list.SetBounds(16, 254, 528, 170);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Columns.Add("Profile", 170);
            list.Columns.Add("PL1 (W)", 70);
            list.Columns.Add("PL2 (W)", 70);
            list.Columns.Add("Window (s)", 80);
            list.Columns.Add("Trạng thái", 110);
            list.DoubleClick += delegate { ApplySelected(); };
            Controls.Add(list);

            btnApply.Text = "Áp dụng profile đã chọn";
            btnApply.SetBounds(16, 432, 200, 32);
            btnApply.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnApply.Click += delegate { ApplySelected(); };
            Controls.Add(btnApply);

            btnRefresh.Text = "Làm mới";
            btnRefresh.SetBounds(226, 432, 100, 32);
            btnRefresh.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnRefresh.Click += delegate { RefreshAll(); };
            Controls.Add(btnRefresh);

            btnStart.Text = "Khởi động dịch vụ";
            btnStart.SetBounds(336, 432, 140, 32);
            btnStart.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnStart.Click += delegate { StartService(); };
            Controls.Add(btnStart);

            status.SetBounds(16, 474, 528, 36);
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            status.ForeColor = Color.DimGray;
            Controls.Add(status);

            tray.Icon = SystemIcons.Application;
            tray.Visible = true;
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowWindow(); };

            timer.Interval = 3000;
            timer.Tick += delegate { RefreshAll(); };

            Load += delegate { RefreshAll(); timer.Start(); };
            FormClosing += OnFormClosing;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        }

        private static void AddRow(GroupBox box, string caption, Label value, int y)
        {
            Label cap = new Label();
            cap.Text = caption;
            cap.AutoSize = true;
            cap.Location = new Point(12, y);
            cap.ForeColor = Color.DimGray;
            box.Controls.Add(cap);

            value.AutoSize = true;
            value.Location = new Point(190, y);
            box.Controls.Add(value);
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(2000, "XTU Profile Switcher", "Vẫn chạy ở khay hệ thống. Chuột phải vào biểu tượng để thoát.", ToolTipIcon.Info);
                return;
            }
            timer.Stop();
            tray.Visible = false;
            client.Dispose();
        }

        private void Say(string text, bool error)
        {
            status.Text = text;
            status.ForeColor = error ? Color.Firebrick : Color.DimGray;
            Log.Write((error ? "ERROR " : "INFO  ") + text);
        }

        private void RefreshAll()
        {
            last = Model.Read(client);
            profiles = Model.LoadProfiles(last);
            Profile active = Model.ActiveProfile(profiles, last);

            lblService.Text = last.ServiceState;
            lblService.ForeColor = last.ServiceState == "Running" ? Color.SeaGreen : Color.Firebrick;
            lblPipe.Text = last.Connected ? "Kết nối được" : "Không kết nối được" + (last.Error.Length > 0 ? " – " + Shorten(last.Error) : "");
            lblPipe.ForeColor = last.Connected ? Color.SeaGreen : Color.Firebrick;
            lblRestore.Text = last.Connected ? (last.RestoreOnBoot ? "Bật" : "Tắt") : "?";
            lblFast.Text = last.FastStartup;
            lblFast.ForeColor = last.FastStartup == "Bật" ? Color.DarkOrange : Color.SeaGreen;

            if (last.Connected)
            {
                lblPl.Text = string.Format("PL1 {0:0.###} W  /  PL2 {1:0.###} W{2}  /  Window {3:0.###} s",
                    last.Pl1, last.Pl2, last.Pl2Enable ? "" : " (tắt)", last.Window);
                lblActive.Text = active != null ? active.Name : "(không khớp profile nào đã lưu)";
                lblActive.ForeColor = active != null ? Color.SeaGreen : Color.DarkOrange;
            }
            else
            {
                lblPl.Text = "?";
                lblActive.Text = "?";
                lblActive.ForeColor = Color.DimGray;
            }

            string selected = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Text : null;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Profile p in profiles)
            {
                ListViewItem item = new ListViewItem(p.Name);
                item.SubItems.Add(p.Pl1.ToString("0.###"));
                item.SubItems.Add(p.Pl2.ToString("0.###") + (p.Pl2Enable ? "" : " (tắt)"));
                item.SubItems.Add(p.Window.ToString("0.###"));
                item.SubItems.Add(p == active ? "ĐANG DÙNG" : "");
                item.Tag = p;
                if (p == active) item.Font = new Font(list.Font, FontStyle.Bold);
                list.Items.Add(item);
                if (selected != null && p.Name == selected) item.Selected = true;
            }
            list.EndUpdate();

            btnStart.Enabled = last.ServiceState != "Running";
            btnApply.Enabled = last.Connected;
            RebuildTray(active);
        }

        private static string Shorten(string s)
        {
            return s.Length > 70 ? s.Substring(0, 70) + "…" : s;
        }

        private void RebuildTray(Profile active)
        {
            trayMenu.Items.Clear();
            foreach (Profile p in profiles)
            {
                Profile captured = p;
                ToolStripMenuItem mi = new ToolStripMenuItem(p.Name + "   (" + p.Pl1.ToString("0.###") + "/" + p.Pl2.ToString("0.###") + " W)");
                mi.Checked = p == active;
                mi.Click += delegate { ApplyProfile(captured, true); };
                trayMenu.Items.Add(mi);
            }
            trayMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem open = new ToolStripMenuItem("Mở cửa sổ");
            open.Click += delegate { ShowWindow(); };
            trayMenu.Items.Add(open);
            ToolStripMenuItem exit = new ToolStripMenuItem("Thoát");
            exit.Click += delegate { reallyExit = true; Close(); Application.Exit(); };
            trayMenu.Items.Add(exit);

            string tip = last.Connected
                ? string.Format("PL {0:0.###}/{1:0.###} W – {2}", last.Pl1, last.Pl2, active != null ? active.Name : "không khớp profile")
                : "XTU: không kết nối";
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        private void ApplySelected()
        {
            if (list.SelectedItems.Count == 0) { Say("Chọn một profile trong danh sách trước.", true); return; }
            ApplyProfile((Profile)list.SelectedItems[0].Tag, false);
        }

        private void ApplyProfile(Profile p, bool fromTray)
        {
            try
            {
                if (!client.IsOpen) client.Connect();
                string msg = client.Apply(p);
                Say("[" + p.Name + "] " + msg, false);
                if (fromTray) tray.ShowBalloonTip(2000, "XTU Profile Switcher", p.Name + ": " + msg, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                client.Close();
                Say("Không áp dụng được [" + p.Name + "]: " + ex.Message, true);
                if (fromTray) tray.ShowBalloonTip(3000, "XTU Profile Switcher", ex.Message, ToolTipIcon.Error);
            }
            RefreshAll();
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
                Say("Đã khởi động dịch vụ XTU.", false);
            }
            catch (Exception ex) { Say("Không khởi động được dịch vụ: " + ex.Message, true); }
            RefreshAll();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveIntel;
            return Run(args);
        }

        private static Assembly ResolveIntel(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(Paths.XtuDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            string outFile = null, applyName = null;
            bool status = false, listOnly = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--status") status = true;
                else if (args[i] == "--list") listOnly = true;
                else if (args[i] == "--apply" && i + 1 < args.Length) applyName = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
            }

            if (status || listOnly || applyName != null) return RunCli(status, listOnly, applyName, outFile);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static int RunCli(bool status, bool listOnly, string applyName, string outFile)
        {
            StringBuilder sb = new StringBuilder();
            int code = 0;
            using (XtuClient client = new XtuClient())
            {
                Snapshot s = Model.Read(client);
                List<Profile> profiles = Model.LoadProfiles(s);
                Profile active = Model.ActiveProfile(profiles, s);

                if (status || listOnly || applyName != null)
                {
                    sb.AppendLine("service: " + s.ServiceState);
                    sb.AppendLine("connected: " + s.Connected + (s.Error.Length > 0 ? " (" + s.Error + ")" : ""));
                    if (s.Connected)
                    {
                        sb.AppendLine(string.Format("active: PL1={0:0.###} PL2={1:0.###} pl2enable={2} window={3:0.###}", s.Pl1, s.Pl2, s.Pl2Enable, s.Window));
                        sb.AppendLine("matching profile: " + (active != null ? active.Name : "(none)"));
                        sb.AppendLine("restore after reboot: " + s.RestoreOnBoot + "   fast startup: " + s.FastStartup);
                    }
                }
                if (listOnly || status)
                    foreach (Profile p in profiles)
                        sb.AppendLine("profile: " + p.Name + "  " + p.Describe() + (p == active ? "   <== active" : ""));

                if (applyName != null)
                {
                    Profile target = null;
                    foreach (Profile p in profiles)
                        if (string.Equals(p.Name, applyName, StringComparison.OrdinalIgnoreCase)) target = p;
                    if (target == null) { sb.AppendLine("apply: profile not found: " + applyName); code = 2; }
                    else
                    {
                        try
                        {
                            if (!client.IsOpen) client.Connect();
                            sb.AppendLine("apply [" + target.Name + "] " + target.Describe());
                            sb.AppendLine("result: " + client.Apply(target));
                        }
                        catch (Exception ex) { sb.AppendLine("apply failed: " + ex.Message); code = 1; }
                    }
                }
            }

            string text = sb.ToString();
            Log.Write("CLI " + string.Join(" ", Environment.GetCommandLineArgs()) + Environment.NewLine + text);
            if (outFile != null) File.WriteAllText(outFile, text, Encoding.UTF8);
            return code;
        }
    }
}
