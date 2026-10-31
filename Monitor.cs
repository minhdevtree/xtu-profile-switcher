// Monitoring and logging.
//
// Readings come from the XTU service's monitoring pipe (the same data XTU's own Monitor and log show),
// plus three sources XTU does not give us: the Windows CPU counter, the NVIDIA GPU (NVML) and the
// battery (WMI). One sampler thread reads everything about once a second; the window draws from the
// history it keeps, and an optional recorder writes rows to a CSV file.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XtuProfileSwitcher
{
    // XTU monitor ids (from IronCity.Common.MonitorIdHelper).
    internal static class Mon
    {
        public const int Cores = 8;
        public const uint CpuThrottle = 3, ActiveCores = 4, GfxFreq = 6, CoreTemp0 = 7, RefClock = 15, MemSpeed = 17, Uncore = 20,
            PowerThrottle = 22, CurrentThrottle = 23, PkgPower = 1000, CorePower = 1001, GfxPower = 1002, VrThrottle = 1003,
            MaxPFreq = 1010, CoreFreq0 = 2000, CoreVolt0 = 3000, CoreUtil0 = 4000;
        public const uint PkgTemp = 2701131777u, SaVolt = 2717909008u, GfxVolt = 2768240652u, GfxTemp = 2768240653u, CacheVolt = 2768240654u;
    }

    internal sealed class Sample
    {
        public DateTime Time;
        public double ElapsedS;
        public string Status = "ok";
        public bool Warmup;     // XTU still returning the values it kept from the last run: not shown, not recorded
        public string Profile = "";
        public decimal Pl1, Pl2;
        public bool HaveLimits;
        public string PowerSource = "";
        public double BatteryPct = double.NaN, BatteryDischargeW = double.NaN, BatteryChargeW = double.NaN;
        public Dictionary<uint, decimal> Xtu = new Dictionary<uint, decimal>();
        public double CpuUtil = double.NaN, MemUtil = double.NaN;
        public double GpuTemp = double.NaN, GpuPower = double.NaN, GpuUtil = double.NaN, GpuClock = double.NaN, GpuMemClock = double.NaN;

        public double X(uint id)
        {
            decimal v;
            return Xtu.TryGetValue(id, out v) ? (double)v : double.NaN;
        }

        public double MaxCoreTemp()
        {
            double m = double.NaN;
            for (uint i = 0; i < Mon.Cores; i++)
            {
                double t = X(Mon.CoreTemp0 + i);
                if (!double.IsNaN(t) && (double.IsNaN(m) || t > m)) m = t;
            }
            return m;
        }
    }

    internal sealed class Col
    {
        public readonly string Name, Unit, Desc;
        public readonly bool PerCore, Gpu;
        public readonly Func<Sample, string> Get;
        public Col(string name, string unit, string desc, bool perCore, bool gpu, Func<Sample, string> get)
        { Name = name; Unit = unit; Desc = desc; PerCore = perCore; Gpu = gpu; Get = get; }
    }

    // The CSV columns. Numbers use "." and no thousands separators; a missing reading is an empty cell.
    internal static class LogColumns
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string N(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? "" : Math.Round(v, 3).ToString("0.###", Inv);
        }

        private static Col Xm(string name, string unit, string desc, uint id, bool perCore)
        {
            return new Col(name, unit, desc, perCore, false, delegate (Sample s) { return N(s.X(id)); });
        }

        public static List<Col> Build(bool perCore, bool gpu)
        {
            List<Col> c = new List<Col>();
            c.Add(new Col("timestamp", "", "Local time, ISO 8601 with UTC offset", false, false, delegate (Sample s) { return s.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", Inv); }));
            c.Add(new Col("elapsed_s", "s", "Seconds since the recording started", false, false, delegate (Sample s) { return N(s.ElapsedS); }));
            c.Add(new Col("status", "", "ok, or xtu_unavailable when the XTU service did not answer (XTU columns are then empty)", false, false, delegate (Sample s) { return s.Status; }));
            c.Add(new Col("profile", "", "Saved profile whose limits match the active ones; empty if none matches", false, false, delegate (Sample s) { return s.Profile.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Profile.Replace("\"", "\"\"") + "\"" : s.Profile; }));
            c.Add(new Col("pl1_w", "W", "PL1 (sustained power limit) active when the row was taken; refreshed every 5 s", false, false, delegate (Sample s) { return s.HaveLimits ? N((double)s.Pl1) : ""; }));
            c.Add(new Col("pl2_w", "W", "PL2 (burst power limit) active when the row was taken; refreshed every 5 s", false, false, delegate (Sample s) { return s.HaveLimits ? N((double)s.Pl2) : ""; }));
            c.Add(new Col("power_source", "", "ac or battery", false, false, delegate (Sample s) { return s.PowerSource; }));
            c.Add(new Col("battery_pct", "%", "Battery charge level", false, false, delegate (Sample s) { return N(s.BatteryPct); }));
            c.Add(new Col("battery_discharge_w", "W", "Battery drain reported by the battery (0 when not discharging); a measure of whole-system power on battery", false, false, delegate (Sample s) { return N(s.BatteryDischargeW); }));
            c.Add(new Col("battery_charge_w", "W", "Charging power reported by the battery (0 when not charging)", false, false, delegate (Sample s) { return N(s.BatteryChargeW); }));

            c.Add(Xm("pkg_temp_c", "°C", "CPU package temperature (XTU \"CPU Core Temperature\")", Mon.PkgTemp, false));
            c.Add(new Col("core_temp_max_c", "°C", "Hottest of the 8 core temperatures", false, false, delegate (Sample s) { return N(s.MaxCoreTemp()); }));
            c.Add(new Col("cpu_util_pct", "%", "Total CPU use from the Windows counter \"% Processor Utility\" (as Task Manager shows it)", false, false, delegate (Sample s) { return N(s.CpuUtil); }));
            c.Add(new Col("mem_util_pct", "%", "Physical memory in use", false, false, delegate (Sample s) { return N(s.MemUtil); }));
            c.Add(Xm("pkg_power_w", "W", "CPU package power (XTU \"Package TDP\")", Mon.PkgPower, false));
            c.Add(Xm("core_power_w", "W", "Power of the CPU cores (XTU \"Core TDP\")", Mon.CorePower, false));
            c.Add(Xm("igpu_power_w", "W", "Power of the integrated graphics (XTU \"Graphics TDP\")", Mon.GfxPower, false));
            c.Add(Xm("cpu_freq_max_ghz", "GHz", "Highest performance-core frequency", Mon.MaxPFreq, false));
            c.Add(Xm("uncore_ghz", "GHz", "Uncore (cache/ring) frequency", Mon.Uncore, false));
            c.Add(Xm("active_cores", "", "Cores that were active", Mon.ActiveCores, false));
            c.Add(Xm("ref_clock_mhz", "MHz", "Reference clock (BCLK)", Mon.RefClock, false));
            c.Add(Xm("mem_speed_mts", "MT/s", "Memory speed", Mon.MemSpeed, false));
            c.Add(Xm("igpu_freq_mhz", "MHz", "Integrated graphics frequency", Mon.GfxFreq, false));
            c.Add(Xm("igpu_temp_c", "°C", "Integrated graphics temperature", Mon.GfxTemp, false));
            c.Add(Xm("igpu_volt_v", "V", "Integrated graphics voltage", Mon.GfxVolt, false));
            c.Add(Xm("cache_volt_v", "V", "Cache voltage", Mon.CacheVolt, false));
            c.Add(Xm("sa_volt_v", "V", "System agent voltage", Mon.SaVolt, false));
            c.Add(Xm("thermal_throttle_pct", "%", "XTU CPU throttle indicator (thermal), 0-100", Mon.CpuThrottle, false));
            c.Add(Xm("power_throttle_pct", "%", "XTU power-limit throttle indicator, 0-100", Mon.PowerThrottle, false));
            c.Add(Xm("current_throttle_pct", "%", "XTU current (EDP) limit throttle indicator, 0-100", Mon.CurrentThrottle, false));
            c.Add(Xm("vr_throttle_pct", "%", "XTU motherboard VR throttle indicator, 0-100", Mon.VrThrottle, false));

            if (perCore)
            {
                for (int i = 0; i < Mon.Cores; i++)
                {
                    int k = i;
                    c.Add(Xm("core" + k + "_temp_c", "°C", "Core " + k + " temperature", Mon.CoreTemp0 + (uint)k, true));
                    c.Add(Xm("core" + k + "_freq_ghz", "GHz", "Core " + k + " frequency", Mon.CoreFreq0 + (uint)k, true));
                    c.Add(Xm("core" + k + "_volt_v", "V", "Core " + k + " voltage", Mon.CoreVolt0 + (uint)k, true));
                    c.Add(Xm("core" + k + "_util_pct", "%", "Core " + k + " utilization (XTU)", Mon.CoreUtil0 + (uint)k, true));
                }
            }
            if (gpu)
            {
                c.Add(new Col("gpu_temp_c", "°C", "NVIDIA GPU temperature (NVML)", false, true, delegate (Sample s) { return N(s.GpuTemp); }));
                c.Add(new Col("gpu_power_w", "W", "NVIDIA GPU board power (NVML)", false, true, delegate (Sample s) { return N(s.GpuPower); }));
                c.Add(new Col("gpu_util_pct", "%", "NVIDIA GPU utilization (NVML)", false, true, delegate (Sample s) { return N(s.GpuUtil); }));
                c.Add(new Col("gpu_clock_mhz", "MHz", "NVIDIA GPU graphics clock (NVML)", false, true, delegate (Sample s) { return N(s.GpuClock); }));
                c.Add(new Col("gpu_mem_clock_mhz", "MHz", "NVIDIA GPU memory clock (NVML)", false, true, delegate (Sample s) { return N(s.GpuMemClock); }));
            }
            return c;
        }
    }

    // NVIDIA GPU through NVML (nvml.dll ships with the NVIDIA driver). Everything is optional.
    internal static class Nvml
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Util { public uint Gpu; public uint Memory; }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int Init();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int HandleByIndex(uint index, out IntPtr dev);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] private static extern int GetTemp(IntPtr dev, int sensor, out uint temp);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")] private static extern int GetPower(IntPtr dev, out uint milliwatts);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int GetUtil(IntPtr dev, out Util u);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int GetClock(IntPtr dev, int type, out uint mhz);

        private static bool tried, ok;
        private static IntPtr dev;

        public static bool Available
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    try { ok = Init() == 0 && HandleByIndex(0, out dev) == 0; } catch { ok = false; }
                }
                return ok;
            }
        }

        public static void Read(Sample s)
        {
            if (!Available) return;
            try
            {
                uint v; Util u;
                if (GetTemp(dev, 0, out v) == 0) s.GpuTemp = v;
                if (GetPower(dev, out v) == 0) s.GpuPower = v / 1000.0;
                if (GetUtil(dev, out u) == 0) s.GpuUtil = u.Gpu;
                if (GetClock(dev, 0, out v) == 0) s.GpuClock = v;
                if (GetClock(dev, 2, out v) == 0) s.GpuMemClock = v;
            }
            catch { ok = false; }
        }
    }

    internal sealed class BatteryReader
    {
        private DateTime at = DateTime.MinValue;
        public double DischargeW = double.NaN, ChargeW = double.NaN;

        public void Read()
        {
            if ((DateTime.Now - at).TotalSeconds < 2) return;
            at = DateTime.Now;
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus"))
                {
                    foreach (ManagementBaseObject o in q.Get())
                    {
                        bool charging = Convert.ToBoolean(o["Charging"]), discharging = Convert.ToBoolean(o["Discharging"]);
                        DischargeW = discharging ? Convert.ToDouble(o["DischargeRate"]) / 1000.0 : 0.0;
                        ChargeW = charging ? Convert.ToDouble(o["ChargeRate"]) / 1000.0 : 0.0;
                        return;
                    }
                }
                DischargeW = ChargeW = double.NaN;
            }
            catch { DischargeW = ChargeW = double.NaN; }
        }
    }

    internal static class SystemMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

        public static double LoadPercent()
        {
            try { MemoryStatus m = new MemoryStatus(); return GlobalMemoryStatusEx(m) ? m.MemoryLoad : double.NaN; }
            catch { return double.NaN; }
        }
    }

    // The XTU service's monitoring pipe. A client registers with its process id (Connect + Start) and
    // unregisters on exit (Stop); the polling period is left alone because XTU's own window shares it.
    internal sealed class XtuMonitorClient : IDisposable
    {
        private ChannelFactory<IronCity.Core.IXtuMonitoring> factory;
        private IronCity.Core.IXtuMonitoring channel;
        private readonly int pid = Process.GetCurrentProcess().Id;

        // True once after a (re)connect: the service may still hold values from before.
        public bool JustOpened;

        public Dictionary<uint, decimal> GetValues()
        {
            if (channel == null) { Open(); JustOpened = true; }
            return channel.GetValues();
        }

        private void Open()
        {
            NetNamedPipeBinding binding = new NetNamedPipeBinding();
            binding.OpenTimeout = TimeSpan.FromSeconds(5);
            binding.SendTimeout = TimeSpan.FromSeconds(10);
            binding.ReceiveTimeout = TimeSpan.FromSeconds(10);
            binding.MaxReceivedMessageSize = 67108864;
            binding.MaxBufferSize = 67108864;
            binding.ReaderQuotas.MaxArrayLength = 67108864;
            binding.ReaderQuotas.MaxStringContentLength = 67108864;

            factory = new ChannelFactory<IronCity.Core.IXtuMonitoring>(binding,
                new EndpointAddress("net.pipe://localhost/ExtremeTuningUtility/Monitoring"));
            foreach (OperationDescription op in factory.Endpoint.Contract.Operations)
            {
                DataContractSerializerOperationBehavior dc = op.Behaviors.Find<DataContractSerializerOperationBehavior>();
                if (dc != null) dc.MaxItemsInObjectGraph = int.MaxValue;
            }
            channel = factory.CreateChannel();
            try
            {
                if (!channel.Connect(pid)) throw new InvalidOperationException(Loc.T("err.refused"));
                channel.Start(pid);
            }
            catch { Close(false); throw; }
        }

        // Forget the connection so the next read opens a new one.
        public void Reset() { Close(false); }

        private void Close(bool stop)
        {
            try { if (stop && channel != null) channel.Stop(pid); } catch { }
            try { if (channel != null) ((ICommunicationObject)channel).Abort(); } catch { }
            try { if (factory != null) factory.Abort(); } catch { }
            channel = null;
            factory = null;
        }

        public void Dispose() { Close(true); }
    }

    // Writes rows to a CSV file (plus a .meta.json that describes the columns and the machine).
    internal sealed class Recorder
    {
        private readonly StreamWriter writer;
        private readonly List<Col> cols;
        private readonly double minutes;
        private DateTime start;
        private DateTime? endAt;
        private bool started;
        private DateTime nextDue;
        public readonly string Path, MetaPath;
        public readonly int EverySeconds;
        public readonly bool PerCore, Gpu;
        public long Rows;

        public int EveryMs { get { return EverySeconds * 1000; } }
        public DateTime Started { get { return start; } }

        public Recorder(string path, int everySeconds, double minutes, bool perCore, bool gpu)
        {
            Path = path;
            MetaPath = System.IO.Path.ChangeExtension(path, null) + ".meta.json";
            EverySeconds = Math.Max(1, everySeconds);
            PerCore = perCore;
            Gpu = gpu;
            this.minutes = minutes;
            cols = LogColumns.Build(perCore, gpu);
            start = DateTime.Now;      // replaced by the time of the first row, which is what the duration counts from
            nextDue = start;

            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Readable by other programs while it is being written (Excel, tail, pandas).
            FileStream fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(fs, new UTF8Encoding(false));
            List<string> names = new List<string>();
            foreach (Col c in cols) names.Add(c.Name);
            writer.WriteLine(string.Join(",", names.ToArray()));
            writer.Flush();
            WriteMeta(null);
        }

        // Writes the row if one is due. Returns false once the requested duration is over.
        public bool Offer(Sample s)
        {
            if (!started)
            {
                started = true;
                start = s.Time;
                nextDue = s.Time;
                endAt = minutes > 0 ? (DateTime?)start.AddMinutes(minutes) : null;
            }
            // A little tolerance so a sample that arrives a few ms early is not pushed to the next slot.
            if (s.Time >= nextDue.AddMilliseconds(-200))
            {
                s.ElapsedS = (s.Time - start).TotalSeconds;
                List<string> cells = new List<string>(cols.Count);
                foreach (Col c in cols) cells.Add(c.Get(s));
                writer.WriteLine(string.Join(",", cells.ToArray()));
                writer.Flush();
                Rows++;
                nextDue = nextDue.AddSeconds(EverySeconds);
                if (nextDue < s.Time) nextDue = s.Time.AddSeconds(EverySeconds);
            }
            return !(endAt.HasValue && s.Time >= endAt.Value);
        }

        public void Close()
        {
            try { writer.Flush(); writer.Dispose(); } catch { }
            WriteMeta(DateTime.Now);
        }

        private static string Esc(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        private static string Wmi(string query, string prop)
        {
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(query))
                    foreach (ManagementBaseObject o in q.Get()) return Convert.ToString(o[prop]).Trim();
            }
            catch { }
            return "";
        }

        private void WriteMeta(DateTime? end)
        {
            try
            {
                string xtu = "";
                try { xtu = FileVersionInfo.GetVersionInfo(System.IO.Path.Combine(Paths.XtuDir, "XtuService.exe")).FileVersion; } catch { }
                StringBuilder j = new StringBuilder();
                j.AppendLine("{");
                j.AppendLine("  \"tool\": \"XTU Profile Switcher\",");
                j.AppendLine("  \"csv\": " + Esc(System.IO.Path.GetFileName(Path)) + ",");
                j.AppendLine("  \"started\": " + Esc(start.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)) + ",");
                j.AppendLine("  \"ended\": " + (end.HasValue ? Esc(end.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)) : "null") + ",");
                j.AppendLine("  \"interval_s\": " + EverySeconds + ",");
                j.AppendLine("  \"rows\": " + Rows + ",");
                j.AppendLine("  \"machine\": {");
                j.AppendLine("    \"manufacturer\": " + Esc(Wmi("SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer")) + ",");
                j.AppendLine("    \"model\": " + Esc(Wmi("SELECT Model FROM Win32_ComputerSystem", "Model")) + ",");
                j.AppendLine("    \"cpu\": " + Esc(Wmi("SELECT Name FROM Win32_Processor", "Name")) + ",");
                j.AppendLine("    \"os\": " + Esc(Environment.OSVersion.VersionString) + ",");
                j.AppendLine("    \"xtu_service\": " + Esc(xtu));
                j.AppendLine("  },");
                j.AppendLine("  \"notes\": [");
                j.AppendLine("    \"XTU columns come from the XTU service, which polls about once a second; sampling faster only repeats values.\",");
                j.AppendLine("    \"pl1_w, pl2_w and profile are re-read every 5 s, so a change shows up in the next row after that.\",");
                j.AppendLine("    \"An empty cell means the reading was not available at that moment.\"");
                j.AppendLine("  ],");
                j.AppendLine("  \"columns\": [");
                for (int i = 0; i < cols.Count; i++)
                    j.AppendLine("    {\"name\": " + Esc(cols[i].Name) + ", \"unit\": " + Esc(cols[i].Unit) + ", \"description\": " + Esc(cols[i].Desc) + "}" + (i < cols.Count - 1 ? "," : ""));
                j.AppendLine("  ]");
                j.AppendLine("}");
                File.WriteAllText(MetaPath, j.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("meta file: " + ex.Message); }
        }
    }

    internal sealed class MonitorService : IDisposable
    {
        private readonly object gate = new object();
        private Thread thread;
        private volatile bool quit;
        private int viewers;
        private Recorder rec;
        private Sample latest;
        private readonly LinkedList<Sample> history = new LinkedList<Sample>();
        private const int HistoryMax = 900;

        public volatile List<Profile> Profiles = new List<Profile>();
        public volatile bool IncludeGpu = true;

        // Raised (on the sampler thread) when a timed recording finishes by itself.
        public event EventHandler RecordingEnded;
        public string LastPath = "";
        public long LastRows;

        public Sample Latest { get { lock (gate) return latest; } }
        public bool Recording { get { lock (gate) return rec != null; } }
        public long RecRows { get { lock (gate) return rec != null ? rec.Rows : 0; } }
        public string RecPath { get { lock (gate) return rec != null ? rec.Path : ""; } }
        public DateTime RecStarted { get { lock (gate) return rec != null ? rec.Started : DateTime.MinValue; } }

        public List<Sample> History(int seconds)
        {
            lock (gate)
            {
                DateTime from = DateTime.Now.AddSeconds(-seconds);
                List<Sample> list = new List<Sample>();
                foreach (Sample s in history) if (s.Time >= from) list.Add(s);
                return list;
            }
        }

        // Somebody is looking at live values: keep sampling.
        public void Retain() { lock (gate) { viewers++; EnsureThread(); } }
        public void Release() { lock (gate) { if (viewers > 0) viewers--; } }

        private void EnsureThread()
        {
            if (thread != null && thread.IsAlive) return;
            quit = false;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "monitor";
            thread.Start();
        }

        public void StartRecording(string path, int everySeconds, double minutes, bool perCore, bool gpu)
        {
            lock (gate)
            {
                if (rec != null) throw new InvalidOperationException("Already recording.");
                IncludeGpu = gpu;
                rec = new Recorder(path, everySeconds, minutes, perCore, gpu);
                EnsureThread();
            }
        }

        // Returns the path and the number of rows written.
        public string StopRecording(out long rows)
        {
            lock (gate)
            {
                rows = 0;
                if (rec == null) return LastPath;
                Recorder r = rec;
                rec = null;
                r.Close();
                LastPath = r.Path;
                LastRows = r.Rows;
                rows = r.Rows;
                return r.Path;
            }
        }

        private void Loop()
        {
            XtuMonitorClient mon = new XtuMonitorClient();
            XtuClient tuning = new XtuClient();
            BatteryReader bat = new BatteryReader();
            PerformanceCounter cpu = null;
            try { cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); cpu.NextValue(); }
            catch
            {
                try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpu.NextValue(); }
                catch { cpu = null; }
            }

            string profile = "";
            decimal pl1 = 0, pl2 = 0;
            bool haveLimits = false;
            DateTime ctxAt = DateTime.MinValue;

            // Fixed schedule: the next slot is the previous slot plus the period, not "after the work".
            Stopwatch clock = Stopwatch.StartNew();
            long nextAt = 0;
            bool warming = false;
            DateTime warmSince = DateTime.MinValue;
            Dictionary<uint, decimal> warmFirst = null;
            uint[] probe = { Mon.PkgPower, Mon.CorePower, Mon.PkgTemp, Mon.MaxPFreq, Mon.CoreFreq0 };

            try
            {
                while (!quit)
                {
                    int periodMs;
                    lock (gate)
                    {
                        if (viewers <= 0 && rec == null) { thread = null; return; }
                        periodMs = viewers > 0 ? 1000 : Math.Max(1000, rec.EveryMs);
                    }

                    Sample s = new Sample();
                    s.Time = DateTime.Now;

                    try { s.Xtu = mon.GetValues(); }
                    catch (Exception ex) { s.Status = "xtu_unavailable"; mon.Reset(); Log.Write("monitor: " + ex.Message); }

                    // Right after connecting, XTU returns what it measured last time. Skip samples until
                    // the values move (or a few seconds have passed).
                    if (mon.JustOpened) { mon.JustOpened = false; warming = true; warmSince = s.Time; warmFirst = null; }
                    if (warming && s.Status == "ok")
                    {
                        if (warmFirst == null)
                        {
                            warmFirst = new Dictionary<uint, decimal>();
                            foreach (uint id in probe) { decimal v; if (s.Xtu.TryGetValue(id, out v)) warmFirst[id] = v; }
                        }
                        else
                        {
                            bool moved = false;
                            foreach (uint id in probe) { decimal v, w; if (s.Xtu.TryGetValue(id, out v) && warmFirst.TryGetValue(id, out w) && v != w) moved = true; }
                            if (moved || (s.Time - warmSince).TotalSeconds >= 4) warming = false;
                        }
                    }
                    else if (s.Status != "ok") warming = false;
                    s.Warmup = warming;

                    if ((s.Time - ctxAt).TotalSeconds >= 5)
                    {
                        ctxAt = s.Time;
                        try
                        {
                            Snapshot sn = Model.Read(tuning);
                            if (sn.Connected)
                            {
                                haveLimits = true; pl1 = sn.Pl1; pl2 = sn.Pl2;
                                Profile a = Model.ActiveProfile(Profiles, sn);
                                profile = a != null ? a.Name : "";
                            }
                        }
                        catch { }
                    }
                    s.HaveLimits = haveLimits; s.Pl1 = pl1; s.Pl2 = pl2; s.Profile = profile;

                    try { if (cpu != null) s.CpuUtil = Math.Max(0, Math.Min(100, cpu.NextValue())); } catch { }
                    s.MemUtil = SystemMemory.LoadPercent();

                    PowerStatus ps = SystemInformation.PowerStatus;
                    s.PowerSource = ps.PowerLineStatus == PowerLineStatus.Online ? "ac" : ps.PowerLineStatus == PowerLineStatus.Offline ? "battery" : "";
                    s.BatteryPct = ps.BatteryLifePercent <= 1f ? ps.BatteryLifePercent * 100.0 : double.NaN;
                    bat.Read();
                    s.BatteryDischargeW = bat.DischargeW;
                    s.BatteryChargeW = bat.ChargeW;
                    if (IncludeGpu) Nvml.Read(s);

                    bool ended = false;
                    lock (gate)
                    {
                        if (!s.Warmup)
                        {
                        latest = s;
                        history.AddLast(s);
                        while (history.Count > HistoryMax) history.RemoveFirst();
                        }
                        if (rec != null && !s.Warmup)
                        {
                            try { if (!rec.Offer(s)) ended = true; }
                            catch (Exception ex) { Log.Write("recorder: " + ex.Message); ended = true; }
                            if (ended)
                            {
                                Recorder r = rec;
                                rec = null;
                                r.Close();
                                LastPath = r.Path;
                                LastRows = r.Rows;
                            }
                        }
                    }
                    if (ended && RecordingEnded != null) RecordingEnded(this, EventArgs.Empty);

                    nextAt += periodMs;
                    if (clock.ElapsedMilliseconds > nextAt + periodMs) nextAt = clock.ElapsedMilliseconds;   // fell behind: do not burst
                    while (!quit && clock.ElapsedMilliseconds < nextAt)
                    {
                        lock (gate) { if (viewers <= 0 && rec == null) break; }
                        Thread.Sleep((int)Math.Max(1, Math.Min(50, nextAt - clock.ElapsedMilliseconds)));
                    }
                }
            }
            finally
            {
                mon.Dispose();
                tuning.Dispose();
                if (cpu != null) cpu.Dispose();
            }
        }

        public void Dispose()
        {
            long rows;
            StopRecording(out rows);
            quit = true;
            Thread t = thread;
            if (t != null && t.IsAlive) t.Join(3000);
        }
    }
}
