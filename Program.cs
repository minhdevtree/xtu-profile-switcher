// XTU Profile Switcher
//
// Small helper for Intel Extreme Tuning Utility (XTU) 7.14.x.
//   * shows whether the XTU service is running and reachable
//   * shows the power limits that are really active right now (read from the service)
//   * works out which saved XTU profile those values correspond to
//   * applies the power-limit part of a saved profile (PL1, PL2, PL2 enable, time window),
//     from the window, the tray menu or a global hotkey, with an on-screen notification
//   * can start with Windows (hidden in the tray) through a scheduled task
//
// It talks to the XTU service over its WCF named pipe, using Intel's own contract
// assemblies from the XTU install folder. That interface is internal and unsupported,
// so this tool only touches four controls and validates every value before sending it.
//
// Targets .NET Framework 4.8 and C# 5 (built with the csc.exe that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace XtuProfileSwitcher
{
    internal static class Paths
    {
        public const string XtuDir = @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers";
        public const string XtuData = @"C:\ProgramData\Intel\Intel Extreme Tuning Utility";
        public const string ProfilesXml = XtuData + @"\Profiles.xml";
        public const string XtuUiSettings = XtuData + @"\uisettings.xml";
        public const string XtuHotkeys = XtuData + @"\xtu.hotkey.settings";
        public const string ServiceName = "XTU3SERVICE";
        public const string PipeUri = "net.pipe://localhost/ExtremeTuningUtility/Tuning";
        public const string TaskName = "XtuProfileSwitcher";

        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XtuProfileSwitcher");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string LogFile { get { return Path.Combine(DataDir, "app.log"); } }
        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.ini"); } }
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

    // A global hotkey: modifiers + one key.
    internal sealed class Hotkey
    {
        public const uint Alt = 1, Ctrl = 2, Shift = 4, NoRepeat = 0x4000;

        public uint Mods;
        public Keys Key;

        public override string ToString()
        {
            List<string> parts = new List<string>();
            if ((Mods & Ctrl) != 0) parts.Add("Ctrl");
            if ((Mods & Alt) != 0) parts.Add("Alt");
            if ((Mods & Shift) != 0) parts.Add("Shift");
            parts.Add(Key.ToString());
            return string.Join("+", parts.ToArray());
        }

        public string Canonical() { return ToString().ToUpperInvariant(); }

        public static Hotkey Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] tokens = text.Split('+');
            Hotkey h = new Hotkey();
            bool haveKey = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i].Trim();
                string u = t.ToUpperInvariant();
                if (u == "CTRL" || u == "CONTROL") h.Mods |= Ctrl;
                else if (u == "ALT") h.Mods |= Alt;
                else if (u == "SHIFT") h.Mods |= Shift;
                else if (i == tokens.Length - 1)
                {
                    try { h.Key = (Keys)Enum.Parse(typeof(Keys), t, true); haveKey = true; }
                    catch { return null; }
                }
                else return null;
            }
            // Require Ctrl or Alt so a hotkey never steals plain typing.
            if (!haveKey || (h.Mods & (Ctrl | Alt)) == 0) return null;
            return h;
        }

        // Combos XTU's own hotkey pairing listens for (it uses a global keyboard hook, so it
        // would act on the very same keys at the same time as this tool).
        public static HashSet<string> XtuCombos()
        {
            HashSet<string> set = new HashSet<string>();
            try
            {
                if (!XtuPairingOn()) return set;
                string s = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(Paths.XtuHotkeys));
                foreach (Match m in Regex.Matches(s, @"(?:(?:CONTROL|CTRL|SHIFT|ALT)\+)+[A-Za-z0-9]+"))
                {
                    Hotkey h = Parse(m.Value);
                    if (h != null) set.Add(h.Canonical());
                }
            }
            catch { }
            return set;
        }

        public static bool XtuPairingOn()
        {
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(Paths.XtuUiSettings);
                foreach (XmlNode n in doc.SelectNodes("//NameValuePair"))
                {
                    XmlNode name = n.SelectSingleNode("Name");
                    XmlNode val = n.SelectSingleNode("Value");
                    if (name != null && val != null && name.InnerText == "HotkeyPairingOn")
                        return string.Equals(val.InnerText, "True", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }
    }

    // Per-user settings: profile name -> hotkey text, stored as "name<TAB>hotkey" lines.
    internal static class Settings
    {
        public static Dictionary<string, string> LoadHotkeys()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string file = Paths.SettingsFile;
            if (!File.Exists(file))
            {
                // First run: mirror the keys already used in XTU, but on Ctrl+Alt so they do not clash with XTU's.
                map["PL64-90W"] = "Ctrl+Alt+F1";
                map["PL55-60W"] = "Ctrl+Alt+F2";
                map["Default (BIOS)"] = "Ctrl+Alt+Home";
                SaveHotkeys(map);
                return map;
            }
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string name = line.Substring(0, tab);
                string key = line.Substring(tab + 1).Trim();
                if (key.Length > 0) map[name] = key;
            }
            return map;
        }

        public static void SaveHotkeys(Dictionary<string, string> map)
        {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> kv in map)
                if (!string.IsNullOrEmpty(kv.Value)) lines.Add(kv.Key + "\t" + kv.Value);
            File.WriteAllLines(Paths.SettingsFile, lines.ToArray(), Encoding.UTF8);
        }
    }

    // "Start with Windows" through a scheduled task that runs elevated at logon, without any prompt.
    internal static class StartupTask
    {
        private static int Run(string args, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                return p.ExitCode;
            }
        }

        public static bool Exists()
        {
            string o;
            return Run("/Query /TN \"" + Paths.TaskName + "\"", out o) == 0;
        }

        public static void Enable()
        {
            string user = WindowsIdentity.GetCurrent().Name;
            string exe = Application.ExecutablePath;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Start XTU Profile Switcher in the tray at logon.</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + Esc(exe) + "</Command><Arguments>--tray</Arguments>" +
                "<WorkingDirectory>" + Esc(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
                "</Task>\r\n";

            string tmp = Path.Combine(Path.GetTempPath(), "xtu-switcher-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            try
            {
                string o;
                int code = Run("/Create /TN \"" + Paths.TaskName + "\" /XML \"" + tmp + "\" /F", out o);
                if (code != 0) throw new InvalidOperationException("schtasks: " + o.Trim());
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public static void Disable()
        {
            string o;
            int code = Run("/Delete /TN \"" + Paths.TaskName + "\" /F", out o);
            if (code != 0 && Exists()) throw new InvalidOperationException("schtasks: " + o.Trim());
        }

        private static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }
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

        // Returns a short result text; throws on any problem.
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

                // One record per control; each carries both the live and the default value.
                IronCity.Core.XtuTuningControl c1 = client.Control(Ids.Pl1);
                IronCity.Core.XtuTuningControl c2 = client.Control(Ids.Pl2);
                IronCity.Core.XtuTuningControl ce = client.Control(Ids.Pl2Enable);
                IronCity.Core.XtuTuningControl cw = client.Control(Ids.Window);
                s.Pl1 = c1.ActiveValue;
                s.Pl2 = c2.ActiveValue;
                s.Pl2Enable = ce.ActiveValue != 0m;
                s.Window = cw.ActiveValue;
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

    // Small borderless, non-activating pop-up that shows for a couple of seconds.
    internal sealed class Osd : Form
    {
        private static Osd current;
        private readonly System.Windows.Forms.Timer life = new System.Windows.Forms.Timer();

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
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = error ? Color.FromArgb(120, 28, 28) : Color.FromArgb(32, 36, 44);
            Opacity = 0.94;
            Size = new Size(440, 78);

            Label t = new Label();
            t.Text = title;
            t.ForeColor = Color.White;
            t.Font = new Font("Segoe UI Semibold", 13f);
            t.AutoSize = false;
            t.SetBounds(16, 8, 408, 30);
            Controls.Add(t);

            Label d = new Label();
            d.Text = detail;
            d.ForeColor = Color.FromArgb(205, 210, 220);
            d.Font = new Font("Segoe UI", 10f);
            d.AutoSize = false;
            d.SetBounds(16, 40, 408, 30);
            Controls.Add(d);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 60);

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
                // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT (click-through)
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) life.Dispose();
            base.Dispose(disposing);
        }
    }

    // Dialog that records a key combination.
    internal sealed class HotkeyDialog : Form
    {
        private readonly Label shown = new Label();
        public string Result;   // null = unchanged, "" = cleared, otherwise hotkey text

        public HotkeyDialog(string profileName, string current)
        {
            Text = "Phím tắt – " + profileName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 150);
            Font = new Font("Segoe UI", 9.5f);

            Label hint = new Label();
            hint.Text = "Nhấn tổ hợp phím mới (cần Ctrl hoặc Alt):";
            hint.AutoSize = true;
            hint.Location = new Point(16, 14);
            Controls.Add(hint);

            shown.Text = string.IsNullOrEmpty(current) ? "(chưa gán)" : current;
            shown.Font = new Font("Segoe UI Semibold", 13f);
            shown.BorderStyle = BorderStyle.FixedSingle;
            shown.TextAlign = ContentAlignment.MiddleCenter;
            shown.SetBounds(16, 42, 348, 40);
            Controls.Add(shown);

            Button ok = new Button();
            ok.Text = "Lưu";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(16, 100, 90, 32);
            Controls.Add(ok);

            Button clear = new Button();
            clear.Text = "Xoá phím";
            clear.SetBounds(116, 100, 100, 32);
            clear.Click += delegate { Result = ""; shown.Text = "(chưa gán)"; };
            Controls.Add(clear);

            Button cancel = new Button();
            cancel.Text = "Huỷ";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(274, 100, 90, 32);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.None)
                return base.ProcessCmdKey(ref msg, keyData);
            if (key == Keys.Escape || key == Keys.Enter || key == Keys.Tab)
                return base.ProcessCmdKey(ref msg, keyData);

            Hotkey h = new Hotkey();
            h.Key = key;
            if ((keyData & Keys.Control) != 0) h.Mods |= Hotkey.Ctrl;
            if ((keyData & Keys.Alt) != 0) h.Mods |= Hotkey.Alt;
            if ((keyData & Keys.Shift) != 0) h.Mods |= Hotkey.Shift;
            if ((h.Mods & (Hotkey.Ctrl | Hotkey.Alt)) == 0) return true;   // ignore until Ctrl/Alt is held

            Result = h.ToString();
            shown.Text = Result;
            return true;
        }
    }

    internal sealed class MainForm : Form
    {
        private const int WmHotkey = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly XtuClient client = new XtuClient();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        private readonly Label lblService = new Label();
        private readonly Label lblPipe = new Label();
        private readonly Label lblRestore = new Label();
        private readonly Label lblFast = new Label();
        private readonly Label lblPl = new Label();
        private readonly Label lblActive = new Label();
        private readonly ListView list = new ListView();
        private readonly Button btnApply = new Button();
        private readonly Button btnHotkey = new Button();
        private readonly Button btnRefresh = new Button();
        private readonly Button btnStart = new Button();
        private readonly CheckBox chkStartup = new CheckBox();
        private readonly Label status = new Label();

        private List<Profile> profiles = new List<Profile>();
        private Snapshot last = new Snapshot();
        private Dictionary<string, string> hotkeys = Settings.LoadHotkeys();
        private readonly Dictionary<int, string> registered = new Dictionary<int, string>();
        private bool reallyExit;
        private bool startHidden;
        private bool busy;
        private bool suppressStartupEvent;
        private int tick;

        public MainForm(bool startHidden, EventWaitHandle showSignal)
        {
            this.startHidden = startHidden;

            Text = "XTU Profile Switcher";
            ClientSize = new Size(560, 570);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            MinimumSize = new Size(560, 570);

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
            list.Columns.Add("Profile", 140);
            list.Columns.Add("PL1 (W)", 60);
            list.Columns.Add("PL2 (W)", 70);
            list.Columns.Add("Window (s)", 75);
            list.Columns.Add("Phím tắt", 100);
            list.Columns.Add("Trạng thái", 80);
            list.DoubleClick += delegate { ApplySelected(); };
            Controls.Add(list);

            btnApply.Text = "Áp dụng profile đã chọn";
            btnApply.SetBounds(16, 432, 200, 32);
            btnApply.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnApply.Click += delegate { ApplySelected(); };
            Controls.Add(btnApply);

            btnHotkey.Text = "Gán phím tắt…";
            btnHotkey.SetBounds(226, 432, 140, 32);
            btnHotkey.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnHotkey.Click += delegate { EditHotkey(); };
            Controls.Add(btnHotkey);

            btnRefresh.Text = "Làm mới";
            btnRefresh.SetBounds(376, 432, 100, 32);
            btnRefresh.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnRefresh.Click += delegate { RefreshAll(); };
            Controls.Add(btnRefresh);

            btnStart.Text = "Khởi động dịch vụ";
            btnStart.SetBounds(16, 472, 160, 32);
            btnStart.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnStart.Click += delegate { StartService(); };
            Controls.Add(btnStart);

            chkStartup.Text = "Mở cùng Windows (chạy ẩn ở khay)";
            chkStartup.AutoSize = true;
            chkStartup.Location = new Point(190, 478);
            chkStartup.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            chkStartup.CheckedChanged += OnStartupChanged;
            Controls.Add(chkStartup);

            status.SetBounds(16, 514, 528, 46);
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            status.ForeColor = Color.DimGray;
            Controls.Add(status);

            tray.Icon = SystemIcons.Application;
            tray.Visible = true;
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowWindow(); };

            timer.Interval = 5000;
            timer.Tick += delegate
            {
                // Refresh less often while hidden in the tray.
                tick++;
                if (Visible || tick % 3 == 0) RefreshAll();
            };

            // Load does not fire when the window starts hidden in the tray, so initialise explicitly.
            Load += delegate { Init(); };
            FormClosing += OnFormClosing;
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };

            if (showSignal != null)
            {
                ThreadPool.RegisterWaitForSingleObject(showSignal, delegate
                {
                    try { if (IsHandleCreated) BeginInvoke(new Action(ShowWindow)); } catch { }
                }, null, -1, false);
            }
        }

        private bool inited;

        private void Init()
        {
            if (inited) return;
            inited = true;
            suppressStartupEvent = true;
            try { chkStartup.Checked = StartupTask.Exists(); } catch { }
            suppressStartupEvent = false;
            RefreshAll();
            timer.Start();
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotkeys();
            base.OnHandleDestroyed(e);
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
            startHidden = false;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshAll();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                Osd.Flash("XTU Profile Switcher", "Vẫn chạy ở khay hệ thống (chuột phải biểu tượng để thoát).", false);
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

        // ---- hotkeys ------------------------------------------------------------------

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();
            List<string> failed = new List<string>();
            int id = 1;
            foreach (KeyValuePair<string, string> kv in hotkeys)
            {
                Hotkey h = Hotkey.Parse(kv.Value);
                if (h == null) { failed.Add(kv.Key + " (" + kv.Value + ")"); continue; }
                if (RegisterHotKey(Handle, id, h.Mods | Hotkey.NoRepeat, (uint)h.Key))
                {
                    registered[id] = kv.Key;
                    Log.Write("hotkey " + h + " -> " + kv.Key);
                    id++;
                }
                else failed.Add(kv.Key + " (" + h + ")");
            }
            if (failed.Count > 0)
                Say("Không đăng ký được phím tắt (đã bị ứng dụng khác dùng?): " + string.Join(", ", failed.ToArray()), true);
        }

        private void UnregisterHotkeys()
        {
            if (!IsHandleCreated) return;
            foreach (int id in registered.Keys) UnregisterHotKey(Handle, id);
            registered.Clear();
        }

        private void EditHotkey()
        {
            if (list.SelectedItems.Count == 0) { Say("Chọn một profile trong danh sách trước.", true); return; }
            Profile p = (Profile)list.SelectedItems[0].Tag;
            string current;
            hotkeys.TryGetValue(p.Name, out current);

            using (HotkeyDialog dlg = new HotkeyDialog(p.Name, current))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;

                string text = dlg.Result;
                if (text.Length > 0)
                {
                    Hotkey h = Hotkey.Parse(text);
                    if (h == null) { Say("Tổ hợp phím không hợp lệ (cần Ctrl hoặc Alt).", true); return; }

                    foreach (KeyValuePair<string, string> kv in hotkeys)
                    {
                        Hotkey other = Hotkey.Parse(kv.Value);
                        if (other != null && kv.Key != p.Name && other.Canonical() == h.Canonical())
                        { Say("Tổ hợp " + h + " đã gán cho profile \"" + kv.Key + "\".", true); return; }
                    }
                    if (Hotkey.XtuCombos().Contains(h.Canonical()))
                    {
                        Say("XTU cũng đang bắt tổ hợp " + h + " (Hotkey-Profile Pairing). Nó sẽ tự áp profile cùng lúc, "
                            + "không có thông báo. Hãy chọn tổ hợp khác hoặc tắt tính năng đó trong XTU.", true);
                        return;
                    }
                    text = h.ToString();
                }

                if (text.Length == 0) hotkeys.Remove(p.Name); else hotkeys[p.Name] = text;
                Settings.SaveHotkeys(hotkeys);
                RegisterHotkeys();
                RefreshAll();
                Say(text.Length == 0 ? "Đã xoá phím tắt của " + p.Name + "." : "Đã gán " + text + " cho " + p.Name + ".", false);
            }
        }

        private void ApplyByName(string name)
        {
            foreach (Profile p in profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { ApplyProfile(p, true); return; }
            Osd.Flash("Không tìm thấy profile", name, true);
        }

        // ---- startup task -------------------------------------------------------------

        private void OnStartupChanged(object sender, EventArgs e)
        {
            if (suppressStartupEvent) return;
            try
            {
                if (chkStartup.Checked) { StartupTask.Enable(); Say("Đã bật: mở cùng Windows (chạy ẩn ở khay, trễ 15 giây sau khi đăng nhập).", false); }
                else { StartupTask.Disable(); Say("Đã tắt mở cùng Windows.", false); }
            }
            catch (Exception ex)
            {
                Say("Không đổi được tác vụ khởi động: " + ex.Message, true);
                suppressStartupEvent = true;
                try { chkStartup.Checked = StartupTask.Exists(); } catch { }
                suppressStartupEvent = false;
            }
        }

        // ---- refresh / apply ----------------------------------------------------------

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
                string hk;
                hotkeys.TryGetValue(p.Name, out hk);
                ListViewItem item = new ListViewItem(p.Name);
                item.SubItems.Add(p.Pl1.ToString("0.###"));
                item.SubItems.Add(p.Pl2.ToString("0.###") + (p.Pl2Enable ? "" : " (tắt)"));
                item.SubItems.Add(p.Window.ToString("0.###"));
                item.SubItems.Add(hk ?? "");
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
                string hk;
                hotkeys.TryGetValue(p.Name, out hk);
                ToolStripMenuItem mi = new ToolStripMenuItem(p.Name + "   (" + p.Pl1.ToString("0.###") + "/" + p.Pl2.ToString("0.###") + " W)"
                    + (string.IsNullOrEmpty(hk) ? "" : "   " + hk));
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

        private void ApplyProfile(Profile p, bool notify)
        {
            if (busy) return;
            busy = true;
            try
            {
                try
                {
                    if (!client.IsOpen) client.Connect();
                    string msg = client.Apply(p);
                    Say("[" + p.Name + "] " + msg, false);
                    RefreshAll();
                    if (notify)
                    {
                        string values = last.Connected
                            ? string.Format("PL1 {0:0.###} W · PL2 {1:0.###} W · {2:0.###} s", last.Pl1, last.Pl2, last.Window)
                            : msg;
                        Osd.Flash(p.Name, msg.StartsWith("Đã ở sẵn") ? "Đã ở sẵn · " + values : values, false);
                    }
                }
                catch (Exception ex)
                {
                    client.Close();
                    Say("Không áp dụng được [" + p.Name + "]: " + ex.Message, true);
                    RefreshAll();
                    if (notify) Osd.Flash("Không áp dụng được " + p.Name, Shorten(ex.Message), true);
                }
            }
            finally { busy = false; }
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
            string outFile = null, applyName = null, startup = null;
            bool status = false, listOnly = false, tray = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--status") status = true;
                else if (args[i] == "--list") listOnly = true;
                else if (args[i] == "--tray") tray = true;
                else if (args[i] == "--apply" && i + 1 < args.Length) applyName = args[++i];
                else if (args[i] == "--startup" && i + 1 < args.Length) startup = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
            }

            if (status || listOnly || applyName != null || startup != null)
                return RunCli(status, listOnly, applyName, startup, outFile);

            // One instance only: a second launch just brings the first one to the front.
            bool created;
            using (Mutex mutex = new Mutex(true, "XtuProfileSwitcher.Singleton", out created))
            using (EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "XtuProfileSwitcher.Show"))
            {
                if (!created)
                {
                    if (!tray) showSignal.Set();
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(tray, showSignal));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static int RunCli(bool status, bool listOnly, string applyName, string startup, string outFile)
        {
            StringBuilder sb = new StringBuilder();
            int code = 0;

            if (startup != null)
            {
                try
                {
                    if (startup == "on") StartupTask.Enable();
                    else if (startup == "off") StartupTask.Disable();
                    sb.AppendLine("startup task exists: " + StartupTask.Exists());
                }
                catch (Exception ex) { sb.AppendLine("startup failed: " + ex.Message); code = 1; }
            }

            if (status || listOnly || applyName != null)
            {
                using (XtuClient client = new XtuClient())
                {
                    Snapshot s = Model.Read(client);
                    List<Profile> profiles = Model.LoadProfiles(s);
                    Profile active = Model.ActiveProfile(profiles, s);

                    sb.AppendLine("service: " + s.ServiceState);
                    sb.AppendLine("connected: " + s.Connected + (s.Error.Length > 0 ? " (" + s.Error + ")" : ""));
                    if (s.Connected)
                    {
                        sb.AppendLine(string.Format("active: PL1={0:0.###} PL2={1:0.###} pl2enable={2} window={3:0.###}", s.Pl1, s.Pl2, s.Pl2Enable, s.Window));
                        sb.AppendLine("matching profile: " + (active != null ? active.Name : "(none)"));
                        sb.AppendLine("restore after reboot: " + s.RestoreOnBoot + "   fast startup: " + s.FastStartup);
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
            }

            string text = sb.ToString();
            Log.Write("CLI " + string.Join(" ", Environment.GetCommandLineArgs()) + Environment.NewLine + text);
            if (outFile != null) File.WriteAllText(outFile, text, Encoding.UTF8);
            return code;
        }
    }
}
