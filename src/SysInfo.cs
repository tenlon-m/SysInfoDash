using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace SysInfoDash
{
    /// <summary>单个磁盘的容量信息。</summary>
    public class DiskInfo
    {
        public string Name;
        public ulong TotalBytes;
        public ulong FreeBytes;
    }

    /// <summary>一次采集得到的不可变快照。</summary>
    public class SysInfoSnapshot
    {
        public float CpuUsage = 0f;          // %
        public float CpuMhz = 0f;            // 当前频率 MHz
        public float CpuMaxMhz = 0f;         // 标称最大频率 MHz
        public float? CpuTemp = null;        // °C
        public ulong MemTotalMB = 0;
        public ulong MemUsedMB = 0;
        public float? GpuUsage = null;       // %
        public float? GpuTemp = null;        // °C
        public ulong GpuDedTotalMB = 0;      // 专用显存
        public ulong GpuDedUsedMB = 0;
        public ulong GpuSharedTotalMB = 0;   // 共享内存（非本地段）
        public ulong GpuSharedUsedMB = 0;
        public double NetDownBps = 0;        // 接收字节/秒
        public double NetUpBps = 0;          // 发送字节/秒
        public double NetDownPeak = 0;
        public double NetUpPeak = 0;
        public long NetLinkBits = 0;         // 参与统计的网卡链路速率总和（bit/s），用于把手按“链路利用率”显示网速
        public List<DiskInfo> Disks = new List<DiskInfo>();
        public double DiskReadBps = 0;
        public double DiskWriteBps = 0;
        public bool DiskReadActive = false;
        public bool DiskWriteActive = false;
    }

    /// <summary>
    /// 后台线程持续采集系统信息，UI 通过 Current 读取最近一次快照。
    /// </summary>
    public class SysInfoCollector : IDisposable
    {
        private PerformanceCounter _cpuPerf;
        private PerformanceCounter _cpuFreqPct;
        // 网络流量改用 NetworkInterface 累计字节数做差值，避免 PerformanceCounter 取不到值导致数字不变
        private DateTime _netPrevTime = DateTime.MinValue;
        private ulong _netPrevDown = 0;
        private ulong _netPrevUp = 0;
        private Dictionary<string, PerformanceCounter> _diskRead = new Dictionary<string, PerformanceCounter>();
        private Dictionary<string, PerformanceCounter> _diskWrite = new Dictionary<string, PerformanceCounter>();
        private Dictionary<string, PerformanceCounter> _gpuEng = new Dictionary<string, PerformanceCounter>();
        private Dictionary<string, PerformanceCounter> _gpuDedUsed = new Dictionary<string, PerformanceCounter>();
        private Dictionary<string, PerformanceCounter> _gpuShUsed = new Dictionary<string, PerformanceCounter>();

        private string[] _netInstances = new string[0];
        private string[] _diskInstances = new string[0];

        private float _cpuMaxMhz;
        private int _lastGpuMs = -100000;
        private int _lastTempMs = -100000;
        private bool _nvidiaAvailable;
        private bool _gpuAdapterMemAvailable;
        private bool _gpuEngineAvailable;

        // 最近一次有效值缓存：采集周期之间的快照沿用缓存，避免 UI 在 N/A 与数值间跳动
        private float? _lastCpuTemp;            // 最近有效 CPU 温度
        private int _lastCpuTempOkMs = -100000; // 该值获取时刻
        private float? _lastGpuUsage;           // 最近有效 GPU 利用率
        private float? _lastGpuTemp;            // 最近有效 GPU 温度
        private int _lastGpuOkMs = -100000;     // GPU 利用率获取时刻

        // 显存容量（专用/共享总量），由后台线程一次性获取
        private ulong _gpuDedTotalMB;
        private ulong _gpuSharedTotalMB;
        private double _peakDown, _peakUp;

        private volatile SysInfoSnapshot _current;
        private Thread _worker;
        private volatile bool _stop;

        public SysInfoSnapshot Current { get { return _current; } }

        public SysInfoCollector()
        {
            try { _cpuMaxMhz = ReadCpuMaxMhz(); }
            catch { _cpuMaxMhz = 0f; }

            try { _cpuPerf = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuPerf.NextValue(); }
            catch { _cpuPerf = null; }

            try { _cpuFreqPct = new PerformanceCounter("Processor Information", "% of Maximum Frequency", "_Total"); _cpuFreqPct.NextValue(); }
            catch { _cpuFreqPct = null; }

            try { _gpuEngineAvailable = PerformanceCounterCategory.Exists("GPU Engine"); }
            catch { _gpuEngineAvailable = false; }

            try { _gpuAdapterMemAvailable = PerformanceCounterCategory.Exists("GPU Adapter Memory"); }
            catch { _gpuAdapterMemAvailable = false; }

            _nvidiaAvailable = File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"));

            RefreshNetInstances();
            RefreshDiskInstances();

            // 后台获取显存容量（注册表/WMI/dxdiag）
            try
            {
                Thread capThread = new Thread(QueryGpuCapacityOnce);
                capThread.IsBackground = true;
                capThread.Start();
            }
            catch { }

            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Start();
        }

        // ---------------- 后台采集循环 ----------------

        private void WorkerLoop()
        {
            int tick = 0;
            while (!_stop)
            {
                try
                {
                    SysInfoSnapshot s = new SysInfoSnapshot();
                    int now = Environment.TickCount;

                    UpdateCpu(s);
                    UpdateMemory(s);
                    UpdateNet(s);
                    UpdateDisk(s);

                    // 鏄惧瓨瀹归噺姣忔閮藉～鍏咃紙瀹归噺鐢卞悗鍙颁竴娆℃€ц幏鍙栵級锛岄伩鍏嶅叡浜闅?UpdateGpu 鑺傚鎶栧姩
                    s.GpuDedTotalMB = _gpuDedTotalMB;
                    s.GpuSharedTotalMB = _gpuSharedTotalMB;

                    if (now - _lastGpuMs >= 2000) { UpdateGpu(s); _lastGpuMs = now; }
                    if (now - _lastTempMs >= 5000) { UpdateTemp(s); _lastTempMs = now; }

                    // 用最近一次有效值填充快照：采集失败或未到采集周期时保持稳定显示，
                    // 超过有效期（源持续不可用）才回到 N/A
                    if (s.CpuTemp.HasValue) { _lastCpuTemp = s.CpuTemp; _lastCpuTempOkMs = now; }
                    else if (_lastCpuTemp.HasValue && now - _lastCpuTempOkMs < 15000)
                        s.CpuTemp = _lastCpuTemp;

                    if (s.GpuTemp.HasValue) { _lastGpuTemp = s.GpuTemp; }
                    else if (_lastGpuTemp.HasValue && now - _lastGpuOkMs < 8000)
                        s.GpuTemp = _lastGpuTemp;

                    if (s.GpuUsage.HasValue) { _lastGpuUsage = s.GpuUsage; _lastGpuOkMs = now; }
                    else if (_lastGpuUsage.HasValue && now - _lastGpuOkMs < 8000)
                        s.GpuUsage = _lastGpuUsage;

                    _current = s;

                    if (++tick % 60 == 0)
                    {
                        RefreshNetInstances();
                        RefreshDiskInstances();
                    }
                }
                catch
                {
                    // 单轮采集失败不影响后续轮次
                }
                Thread.Sleep(1000);
            }
        }

        // ---------------- CPU ----------------

        private static float ReadCpuMaxMhz()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                if (key == null) return 0f;
                object mhz = key.GetValue("~MHz");
                object maxMhz = key.GetValue("MaxMHz");
                float v = 0f;
                if (maxMhz != null) { float.TryParse(maxMhz.ToString(), out v); }
                if (v <= 0f && mhz != null) { float.TryParse(mhz.ToString(), out v); }
                return v;
            }
        }

        private void UpdateCpu(SysInfoSnapshot s)
        {
            try
            {
                if (_cpuPerf != null)
                {
                    float v = (float)_cpuPerf.NextValue();
                    if (v < 0) v = 0; if (v > 100) v = 100;
                    s.CpuUsage = v;
                }
            }
            catch { }

            float mhz = _cpuMaxMhz;
            if (_cpuFreqPct != null)
            {
                try
                {
                    float pct = (float)_cpuFreqPct.NextValue();
                    if (pct > 0f && _cpuMaxMhz > 0f) mhz = _cpuMaxMhz * (pct / 100f);
                }
                catch { }
            }
            s.CpuMhz = mhz;
            s.CpuMaxMhz = _cpuMaxMhz;
        }

        // ---------------- 内存 ----------------

        private void UpdateMemory(SysInfoSnapshot s)
        {
            NativeMethods.MEMORYSTATUSEX m = new NativeMethods.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX));
            if (NativeMethods.GlobalMemoryStatusEx(ref m))
            {
                s.MemTotalMB = m.ullTotalPhys / (1024UL * 1024UL);
                s.MemUsedMB = (m.ullTotalPhys - m.ullAvailPhys) / (1024UL * 1024UL);
            }
        }

        // ---------------- 网络 ----------------

        private static bool IsVirtualNic(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("loopback") || n.Contains("isatap") || n.Contains("teredo")
                || n.Contains("vethernet") || n.Contains("hyper-v") || n.Contains("virtual");
        }

        private void RefreshNetInstances()
        {
            // 网络流量改为基于 NetworkInterface 累计字节统计，无需维护 PerformanceCounter 实例。
        }

        private void UpdateNet(SysInfoSnapshot s)
        {
            try
            {
                ulong recv = 0, sent = 0;
                long linkBits = 0;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    try
                    {
                        var st = ni.GetIPStatistics();
                        recv += (ulong)st.BytesReceived;
                        sent += (ulong)st.BytesSent;
                        linkBits += ni.Speed;   // 累加链路速率（bit/s），用于把手按利用率显示
                    }
                    catch { }
                }
                s.NetLinkBits = linkBits;

                if (_netPrevTime != DateTime.MinValue)
                {
                    double sec = (DateTime.UtcNow - _netPrevTime).TotalSeconds;
                    if (sec > 0.05)
                    {
                        double d = (recv - _netPrevDown) / sec;
                        double u = (sent - _netPrevUp) / sec;
                        if (d < 0) d = 0;
                        if (u < 0) u = 0;
                        s.NetDownBps = d;
                        s.NetUpBps = u;
                    }
                }
                _netPrevTime = DateTime.UtcNow;
                _netPrevDown = recv;
                _netPrevUp = sent;

                _peakDown = Math.Max(s.NetDownBps, _peakDown * 0.93);
                _peakUp = Math.Max(s.NetUpBps, _peakUp * 0.93);
                s.NetDownPeak = _peakDown;
                s.NetUpPeak = _peakUp;
            }
            catch { }
        }

        // ---------------- 磁盘 ----------------

        private void RefreshDiskInstances()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("PhysicalDisk"))
                {
                    _diskInstances = new string[0];
                    return;
                }
                PerformanceCounterCategory cat = new PerformanceCounterCategory("PhysicalDisk");
                _diskInstances = cat.GetInstanceNames()
                    .Where(n => n != "_Total" && !n.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                List<string> stale = new List<string>();
                foreach (string k in _diskRead.Keys)
                {
                    if (Array.IndexOf(_diskInstances, k) < 0)
                    {
                        try { _diskRead[k].Dispose(); } catch { }
                        try { _diskWrite[k].Dispose(); } catch { }
                        stale.Add(k);
                    }
                }
                foreach (string k in stale) { _diskRead.Remove(k); _diskWrite.Remove(k); }

                foreach (string inst in _diskInstances)
                {
                    if (!_diskRead.ContainsKey(inst))
                    {
                        try
                        {
                            PerformanceCounter r = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst);
                            r.NextValue();
                            _diskRead[inst] = r;
                        }
                        catch { }
                        try
                        {
                            PerformanceCounter w = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst);
                            w.NextValue();
                            _diskWrite[inst] = w;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void UpdateDisk(SysInfoSnapshot s)
        {
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if ((d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable) && d.IsReady)
                    {
                        DiskInfo di = new DiskInfo();
                        di.Name = d.Name.TrimEnd('\\');
                        di.TotalBytes = (ulong)d.TotalSize;
                        di.FreeBytes = (ulong)d.AvailableFreeSpace;
                        s.Disks.Add(di);
                    }
                }
            }
            catch { }

            double rd = 0, wr = 0;
            foreach (KeyValuePair<string, PerformanceCounter> kv in _diskRead)
            {
                try { double v = kv.Value.NextValue(); if (v > 0 && v < 1e12) rd += v; } catch { }
            }
            foreach (KeyValuePair<string, PerformanceCounter> kv in _diskWrite)
            {
                try { double v = kv.Value.NextValue(); if (v > 0 && v < 1e12) wr += v; } catch { }
            }
            s.DiskReadBps = rd;
            s.DiskWriteBps = wr;
            s.DiskReadActive = rd > 8192.0;
            s.DiskWriteActive = wr > 8192.0;
        }

        // ---------------- GPU ----------------

        private void UpdateGpu(SysInfoSnapshot s)
        {
            // 显存当前使用：WDDM 原生计数器（Dedicated Usage / Shared Usage）
            if (_gpuAdapterMemAvailable) QueryGpuAdapterUsage(s);

            // 容量（总量）：后台线程获取后统一填充
            s.GpuDedTotalMB = _gpuDedTotalMB;
            s.GpuSharedTotalMB = _gpuSharedTotalMB;

            // 利用率与温度：NVIDIA 用 nvidia-smi，否则用 GPU Engine 计数器近似
            if (_nvidiaAvailable)
            {
                if (TryNvidiaSmi(s)) return;
            }

            if (_gpuEngineAvailable) s.GpuUsage = GetGpuEngineUsage();
        }

        private static void SyncCounterDict(Dictionary<string, PerformanceCounter> dict,
            string[] instances, string counter)
        {
            List<string> stale = new List<string>();
            foreach (string k in dict.Keys)
            {
                if (Array.IndexOf(instances, k) < 0)
                {
                    try { dict[k].Dispose(); } catch { }
                    stale.Add(k);
                }
            }
            foreach (string k in stale) dict.Remove(k);

            foreach (string inst in instances)
            {
                if (!dict.ContainsKey(inst))
                {
                    try
                    {
                        PerformanceCounter pc = new PerformanceCounter("GPU Adapter Memory", counter, inst);
                        pc.NextValue();
                        dict[inst] = pc;
                    }
                    catch { }
                }
            }
        }

        private void QueryGpuAdapterUsage(SysInfoSnapshot s)
        {
            try
            {
                string[] instances = new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames();
                if (instances.Length == 0) return;

                SyncCounterDict(_gpuDedUsed, instances, "Dedicated Usage");
                SyncCounterDict(_gpuShUsed, instances, "Shared Usage");

                ulong du = 0, su = 0;
                foreach (KeyValuePair<string, PerformanceCounter> kv in _gpuDedUsed)
                { try { du += (ulong)Math.Max(0, kv.Value.NextValue()); } catch { } }
                foreach (KeyValuePair<string, PerformanceCounter> kv in _gpuShUsed)
                { try { su += (ulong)Math.Max(0, kv.Value.NextValue()); } catch { } }

                s.GpuDedUsedMB = du / (1024UL * 1024UL);
                s.GpuSharedUsedMB = su / (1024UL * 1024UL);
            }
            catch { }
        }

        // ---------------- 显存容量（一次性获取） ----------------

        private void QueryGpuCapacityOnce()
        {
            try
            {
                // 1) 独显驱动注册表（NVIDIA/AMD 写 qwMemorySize）
                try
                {
                    using (RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    {
                        if (baseKey != null)
                        {
                            for (int i = 0; i < 8; i++)
                            {
                                using (RegistryKey sub = baseKey.OpenSubKey(i.ToString("D4")))
                                {
                                    if (sub == null) continue;
                                    object v = sub.GetValue("HardwareInformation.qwMemorySize");
                                    if (v != null)
                                    {
                                        ulong sz = Convert.ToUInt64(v);
                                        if (sz > 1048576 && sz < (1UL << 41))
                                            _gpuDedTotalMB = Math.Max(_gpuDedTotalMB, sz / 1048576UL);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // 2) WMI AdapterRAM（核显固定配额等，立即生效）
                if (_gpuDedTotalMB == 0)
                {
                    try
                    {
                        using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                            "SELECT AdapterRAM FROM Win32_VideoController"))
                        {
                            foreach (ManagementObject o in searcher.Get())
                            {
                                try
                                {
                                    object v = o["AdapterRAM"];
                                    if (v != null)
                                    {
                                        ulong ram = Convert.ToUInt64(v);
                                        if (ram > 1048576 && ram < (1UL << 40))
                                            _gpuDedTotalMB = Math.Max(_gpuDedTotalMB, ram / 1048576UL);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                // 3) 共享总量兜底：物理内存的一半（核显典型共享上限，估算值，立即生效）
                if (_gpuSharedTotalMB == 0)
                {
                    NativeMethods.MEMORYSTATUSEX m = new NativeMethods.MEMORYSTATUSEX();
                    m.dwLength = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX));
                    if (NativeMethods.GlobalMemoryStatusEx(ref m))
                        _gpuSharedTotalMB = (m.ullTotalPhys / (1024UL * 1024UL)) / 2UL;
                }

                // 4) dxdiag 精确解析（专用/共享总量，权威值，覆盖上面结果）
                try
                {
                    string dxPath = Path.Combine(Path.GetTempPath(), "sysinfodash_dxdiag.txt");
                    try { File.Delete(dxPath); } catch { }

                    Process p = new Process();
                    p.StartInfo.FileName = "dxdiag.exe";
                    p.StartInfo.Arguments = "/t \"" + dxPath + "\"";
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.UseShellExecute = false;
                    p.Start();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } }

                    if (File.Exists(dxPath))
                    {
                        string text = File.ReadAllText(dxPath);
                        ulong ded = ParseMemoryLine(text,
                            new string[] { "Dedicated Memory", "专用内存" });
                        ulong shd = ParseMemoryLine(text,
                            new string[] { "Shared Memory", "共享内存" });
                        if (ded > 0) _gpuDedTotalMB = ded;
                        if (shd > 0) _gpuSharedTotalMB = shd;
                        try { File.Delete(dxPath); } catch { }
                    }
                }
                catch { }
            }
            catch { }
        }

        private static ulong ParseMemoryLine(string text, string[] keys)
        {
            try
            {
                ulong best = 0;
                foreach (string key in keys)
                {
                    Regex rx = new Regex(key + @"\s*[:：]?\s*([\d.]+)\s*(MB|GB)",
                        RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    foreach (Match m in rx.Matches(text))
                    {
                        double v = 0;
                        double.TryParse(m.Groups[1].Value, out v);
                        ulong mb = (ulong)v;
                        if (m.Groups[2].Value.ToUpperInvariant() == "GB") mb = (ulong)(v * 1024.0);
                        if (mb > best) best = mb;
                    }
                }
                return best;
            }
            catch { return 0; }
        }

        private bool TryNvidiaSmi(SysInfoSnapshot s)
        {
            try
            {
                Process p = new Process();
                p.StartInfo.FileName = "nvidia-smi.exe";
                p.StartInfo.Arguments =
                    "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits";
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.Start();

                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } }

                string[] lines = output.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return false;

                float usage = 0, memUsed = 0, memTotal = 0, temp = 0;
                bool ok = false;
                foreach (string line in lines)
                {
                    string[] parts = line.Split(',');
                    if (parts.Length < 4) continue;
                    float u, mu, mt, t;
                    if (!float.TryParse(parts[0].Trim(), out u)) continue;
                    if (!float.TryParse(parts[1].Trim(), out mu)) continue;
                    if (!float.TryParse(parts[2].Trim(), out mt)) continue;
                    if (!float.TryParse(parts[3].Trim(), out t)) continue;
                    if (u > usage) usage = u;
                    if (t > temp) temp = t;
                    memUsed += mu;
                    memTotal += mt;
                    ok = true;
                }
                if (!ok) return false;

                s.GpuUsage = usage;
                s.GpuDedUsedMB = (ulong)memUsed;
                s.GpuDedTotalMB = (ulong)memTotal;
                s.GpuTemp = temp;
                return true;
            }
            catch { return false; }
        }

        private static string ExtractLuid(string instance)
        {
            int i = instance.IndexOf("luid_", StringComparison.Ordinal);
            if (i < 0) return "";
            int j = instance.IndexOf('_', i + 5);
            return j < 0 ? instance.Substring(i) : instance.Substring(i, j - i);
        }

        private float? GetGpuEngineUsage()
        {
            try
            {
                string[] instances = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();

                List<string> stale = new List<string>();
                foreach (string k in _gpuEng.Keys)
                {
                    if (Array.IndexOf(instances, k) < 0)
                    {
                        try { _gpuEng[k].Dispose(); } catch { }
                        stale.Add(k);
                    }
                }
                foreach (string k in stale) _gpuEng.Remove(k);

                foreach (string inst in instances)
                {
                    if (!_gpuEng.ContainsKey(inst))
                    {
                        try
                        {
                            PerformanceCounter pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                            pc.NextValue();
                            _gpuEng[inst] = pc;
                        }
                        catch { }
                    }
                }

                // 只统计不带 pid_ 前缀的实例（按 luid 分组的引擎汇总），
                // 每组取引擎利用率最大值，多 GPU 再取最大值，作为 GPU 整体利用率近似。
                var groups = instances
                    .Where(n => n.IndexOf("pid_", StringComparison.Ordinal) != 0
                             && n.IndexOf("luid_", StringComparison.Ordinal) >= 0)
                    .GroupBy(n => ExtractLuid(n));

                double best = 0;
                foreach (var g in groups)
                {
                    double groupMax = 0;
                    foreach (string inst in g)
                    {
                        PerformanceCounter pc;
                        if (_gpuEng.TryGetValue(inst, out pc))
                        {
                            try { double v = pc.NextValue(); if (v > groupMax) groupMax = v; } catch { }
                        }
                    }
                    if (groupMax > best) best = groupMax;
                }

                if (best < 0) best = 0;
                if (best > 100) best = 100;
                return (float)best;
            }
            catch { return null; }
        }

        // ---------------- CPU 温度 ----------------

        private static float? QueryTempWmi()
        {
            try
            {
                // 方式1：Win10+ 热区性能计数器（单位 0.1°C）
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
                {
                    float best = 0f;
                    bool any = false;
                    foreach (ManagementObject o in searcher.Get())
                    {
                        try
                        {
                            object v = o["Temperature"];
                            if (v != null)
                            {
                                float t = Convert.ToSingle(v) / 10f;
                                if (t > 150f) t -= 273.15f;   // 部分主板返回开尔文×10，校正为摄氏度
                                if (t > best) best = t;
                                any = true;
                            }
                        }
                        catch { }
                    }
                    if (any && best > 0f) return best;
                }
            }
            catch { }

            try
            {
                // 方式2：ACPI 热区（单位 0.1K）
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    float best = 0f;
                    bool any = false;
                    foreach (ManagementObject o in searcher.Get())
                    {
                        try
                        {
                            object v = o["CurrentTemperature"];
                            if (v != null)
                            {
                                float t = Convert.ToSingle(v) / 10f - 273.15f;
                                if (t > best) best = t;
                                any = true;
                            }
                        }
                        catch { }
                    }
                    if (any && best > 0f) return best;
                }
            }
            catch { }

            return null;
        }

        private void UpdateTemp(SysInfoSnapshot s)
        {
            s.CpuTemp = QueryTempWmi();
        }

        // ---------------- 资源释放 ----------------

        public void Dispose()
        {
            _stop = true;
            try { if (_worker != null) _worker.Join(2000); } catch { }

            SafeDispose(_cpuPerf);
            SafeDispose(_cpuFreqPct);
            foreach (KeyValuePair<string, PerformanceCounter> kv in _diskRead) SafeDispose(kv.Value);
            foreach (KeyValuePair<string, PerformanceCounter> kv in _diskWrite) SafeDispose(kv.Value);
            foreach (KeyValuePair<string, PerformanceCounter> kv in _gpuEng) SafeDispose(kv.Value);
            foreach (KeyValuePair<string, PerformanceCounter> kv in _gpuDedUsed) SafeDispose(kv.Value);
            foreach (KeyValuePair<string, PerformanceCounter> kv in _gpuShUsed) SafeDispose(kv.Value);
        }

        private static void SafeDispose(IDisposable obj)
        {
            try { if (obj != null) obj.Dispose(); } catch { }
        }
    }
}
