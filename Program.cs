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
        public bool Custom;     // created in this tool, not in XTU

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

    // Per-user settings, stored as tab-separated lines in settings.ini:
    //   <profile name> TAB <hotkey>                      a hotkey
    //   @auto TAB 1                                      switch profile when the power source changes
    //   @ac TAB <profile name>   /   @battery TAB <profile name>
    //   @custom TAB <name> TAB <PL1> TAB <PL2> TAB <window>   a profile created in this tool
    internal sealed class AppSettings
    {
        public readonly Dictionary<string, string> Hotkeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<Profile> Customs = new List<Profile>();
        public bool AutoSwitch;
        public string Lang = "en";        // "en" | "vi"
        public string Theme = "auto";     // "auto" (follow Windows) | "light" | "dark"
        public string AcProfile = "";
        public string BatteryProfile = "";

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            string file = Paths.SettingsFile;
            if (!File.Exists(file))
            {
                // First run: mirror the keys already used in XTU, but on Ctrl+Alt so they do not clash with XTU's.
                s.Hotkeys["PL64-90W"] = "Ctrl+Alt+F1";
                s.Hotkeys["PL55-60W"] = "Ctrl+Alt+F2";
                s.Hotkeys["Default (BIOS)"] = "Ctrl+Alt+Home";
                s.Save();
                return s;
            }
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                string[] f = line.Split('\t');
                if (f.Length < 2 || f[0].Length == 0) continue;
                string tag = f[0];
                if (tag == "@auto") s.AutoSwitch = f[1].Trim() == "1";
                else if (tag == "@ac") s.AcProfile = f[1].Trim();
                else if (tag == "@battery") s.BatteryProfile = f[1].Trim();
                else if (tag == "@lang") s.Lang = f[1].Trim() == "vi" ? "vi" : "en";
                else if (tag == "@theme") s.Theme = (f[1].Trim() == "light" || f[1].Trim() == "dark") ? f[1].Trim() : "auto";
                else if (tag == "@custom" && f.Length >= 5)
                {
                    Profile p = new Profile();
                    p.Name = f[1];
                    p.Custom = true;
                    p.Pl2Enable = true;
                    if (decimal.TryParse(f[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out p.Pl1)
                        && decimal.TryParse(f[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out p.Pl2)
                        && decimal.TryParse(f[4], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out p.Window))
                        s.Customs.Add(p);
                }
                else if (!tag.StartsWith("@") && f[1].Trim().Length > 0) s.Hotkeys[tag] = f[1].Trim();
            }
            return s;
        }

        public void Save()
        {
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> kv in Hotkeys)
                if (!string.IsNullOrEmpty(kv.Value)) lines.Add(kv.Key + "\t" + kv.Value);
            lines.Add("@auto\t" + (AutoSwitch ? "1" : "0"));
            lines.Add("@lang\t" + Lang);
            if (Theme != "auto") lines.Add("@theme\t" + Theme);
            if (AcProfile.Length > 0) lines.Add("@ac\t" + AcProfile);
            if (BatteryProfile.Length > 0) lines.Add("@battery\t" + BatteryProfile);
            foreach (Profile p in Customs)
                lines.Add("@custom\t" + p.Name + "\t" + p.Pl1.ToString(inv) + "\t" + p.Pl2.ToString(inv) + "\t" + p.Window.ToString(inv));
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
                if (!channel.Connect()) throw new InvalidOperationException(Loc.T("err.refused"));
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
                if (channel == null) throw new InvalidOperationException(Loc.T("err.notconnected"));
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
            if (AlreadyApplied(p)) return Loc.T("msg.already");

            IronCity.Core.XtuTuningControl c1 = Control(Ids.Pl1);
            IronCity.Core.XtuTuningControl c2 = Control(Ids.Pl2);
            if (c1.ReadOnly || !c1.Enabled) throw new InvalidOperationException(Loc.F("err.locked", "PL1"));
            if (c2.ReadOnly || !c2.Enabled) throw new InvalidOperationException(Loc.F("err.locked", "PL2"));
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
                throw new InvalidOperationException(Loc.F("err.rejected", Describe(proposed))
                    + (requiresReboot ? Loc.T("err.reboot") : ""));
            }

            IronCity.Common.TuningResult applied = Ch.ApplyChanges(false, 1, token);
            if (applied.GeneralCode != IronCity.Common.TuningErrorCode.Success)
            {
                Ch.DiscardChanges(token, out discarded);
                throw new InvalidOperationException(Loc.F("err.applyfail", Describe(applied)));
            }

            // Read back, so the caller sees what the hardware really has.
            RefreshFromHardware();
            decimal a1 = Active(Ids.Pl1), a2 = Active(Ids.Pl2), aw = Active(Ids.Window);
            return Loc.F("msg.applied", a1.ToString("0.###"), a2.ToString("0.###"), aw.ToString("0.###"));
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
            throw new InvalidOperationException(Loc.F("err.unsupported", label, value));
        }

        public static void Validate(Profile p)
        {
            if (p.Pl1 < Limits.MinPl1 || p.Pl1 > Limits.MaxPl1)
                throw new InvalidOperationException(Loc.F("val.pl1", Limits.MinPl1, Limits.MaxPl1));
            if (p.Pl2 < p.Pl1 || p.Pl2 > Limits.MaxPl2)
                throw new InvalidOperationException(Loc.F("val.pl2", Limits.MaxPl2));
            if (p.Window < Limits.MinWindow || p.Window > Limits.MaxWindow)
                throw new InvalidOperationException(Loc.F("val.window", Limits.MinWindow, Limits.MaxWindow));
        }
    }

    internal static class Model
    {
        public static List<Profile> LoadProfiles(Snapshot defaults, List<Profile> customs)
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

            if (File.Exists(Paths.ProfilesXml)) LoadXtuProfiles(list);

            // Profiles created in this tool; a name that XTU already uses wins.
            if (customs != null)
                foreach (Profile c in customs)
                {
                    bool taken = false;
                    foreach (Profile p in list)
                        if (string.Equals(p.Name, c.Name, StringComparison.OrdinalIgnoreCase)) taken = true;
                    if (!taken) list.Add(c);
                }
            return list;
        }

        private static void LoadXtuProfiles(List<Profile> list)
        {
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
        }

        public static Snapshot Read(XtuClient client)
        {
            Snapshot s = new Snapshot();

            try
            {
                using (ServiceController sc = new ServiceController(Paths.ServiceName))
                    s.ServiceState = sc.Status.ToString();
            }
            catch (Exception) { s.ServiceState = "NotFound"; }

            object fs = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power",
                "HiberbootEnabled", null);
            s.FastStartup = fs == null ? "?" : (Convert.ToInt32(fs) == 0 ? "Off" : "On");

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
            string outFile = null, applyName = null, startup = null, snap = null, dialog = null, langArg = null, themeArg = null;
            bool status = false, listOnly = false, tray = false, offline = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--status") status = true;
                else if (args[i] == "--list") listOnly = true;
                else if (args[i] == "--tray") tray = true;
                else if (args[i] == "--apply" && i + 1 < args.Length) applyName = args[++i];
                else if (args[i] == "--startup" && i + 1 < args.Length) startup = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--snapshot" && i + 1 < args.Length) snap = args[++i];
                else if (args[i] == "--dialog" && i + 1 < args.Length) dialog = args[++i];
                else if (args[i] == "--lang" && i + 1 < args.Length) langArg = args[++i];
                else if (args[i] == "--theme" && i + 1 < args.Length) themeArg = args[++i];
                else if (args[i] == "--offline") offline = true;
            }

            AppSettings settings = AppSettings.Load();
            Loc.Lang = (langArg ?? settings.Lang) == "vi" ? "vi" : "en";
            Theme.Init(themeArg ?? settings.Theme);
            Ui.InitScale();

            if (status || listOnly || applyName != null || startup != null)
                return RunCli(status, listOnly, applyName, startup, outFile);

            // --snapshot FILE [--dialog hotkey|profile] [--lang vi] [--theme dark|light]:
            // draws the window off-screen into a PNG so the look can be checked without showing anything.
            if (snap != null)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (dialog == "hotkey")
                    using (HotkeyDialog d = new HotkeyDialog("PL55-60W", "Ctrl+Alt+F2")) Ui.Snapshot(d, snap, d.ClientSize);
                else if (dialog == "profile")
                    using (ProfileDialog d = new ProfileDialog(new List<string>())) Ui.Snapshot(d, snap, d.ClientSize);
                else
                    using (MainForm f = new MainForm(false, null, true, settings))
                    {
                        f.SimulateOffline = offline;
                        Ui.Snapshot(f, snap, new Size(Ui.S(640), Ui.S(800)));
                    }
                return 0;
            }

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
                Application.Run(new MainForm(tray, showSignal, false, settings));
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
                    List<Profile> profiles = Model.LoadProfiles(s, AppSettings.Load().Customs);
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
