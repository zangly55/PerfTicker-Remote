using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PerfTicker;

internal sealed class LocalSampler : IMonitorSampler
{
    private readonly CancellationTokenSource _stop = new();
    private Snapshot _latest = new();
    private long _received;
    private int _interval = 1000;
    private string _error = "";
    public Snapshot Latest => Volatile.Read(ref _latest);
    public int Cores => Math.Max(1, Latest.CoreMHz.Length);
    public string CpuName => Latest.CpuName;
    public string GpuDriverVersion => Latest.GpuDriver;
    public int IntervalMs { get => Volatile.Read(ref _interval); set => Volatile.Write(ref _interval, Math.Clamp(value, 500, 10000)); }
    public bool IsLive => !_stop.IsCancellationRequested && Latest.Tick > 0 && Environment.TickCount64 - Interlocked.Read(ref _received) < Math.Max(10000, IntervalMs * 3);
    public string Status => IsLive ? "本机行情在线" : Latest.Tick == 0 ? "正在读取本机" : "本机采集已中断";
    public string Error => Volatile.Read(ref _error);
    public LocalSampler() => _ = Task.Run(Run);
    private async Task Run()
    {
        using var reader = new LocalReader();
        try
        {
            reader.Read(); // Establish counter baselines; never present the boot average as a live sample.
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(IntervalMs, _stop.Token);
                try
                {
                    var s = reader.Read();
                    s.Tick = Latest.Tick + 1;
                    Volatile.Write(ref _latest, s);
                    Interlocked.Exchange(ref _received, Environment.TickCount64);
                    _error = s.SensorWarning;
                }
                catch (Exception ex) { _error = ex.Message; Diag.Write("LOCAL", ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error = ex.Message; }
    }
    public void Dispose() => _stop.Cancel();
}

internal sealed class LocalReader : IDisposable
{
    private readonly int _cores = Environment.ProcessorCount;
    private readonly Dictionary<string, (double total, double idle)> _cpu = new();
    private Dictionary<int, (long start, double cpu)> _processes = new();
    private Dictionary<string, (long rx, long tx)> _network = new();
    private Dictionary<string, (double read, double write, double busy)> _disks = new();
    private double _time;
    private string _cpuName;
    private List<GpuInfo> _macCards;
    private uint _machHost;
    private static double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : double.NaN;
    private static string FileText(string path) { try { return File.ReadAllText(path); } catch { return ""; } }
    private static void Warn(Snapshot s, string text) => s.SensorWarning += (s.SensorWarning.Length > 0 ? " · " : "") + text;

    internal static string Command(string name, int timeoutMs, params string[] args)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo(name) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        p.StartInfo.Environment["LC_ALL"] = "C";
        foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
        p.Start();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException(name + " 超时"); }
        Task.WaitAll(output, error);
        if (p.ExitCode != 0) throw new IOException(name + " 无法读取指标");
        return output.Result;
    }

    public Snapshot Read()
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        double dt = _time > 0 ? now - _time : 1;
        _time = now;
        var s = new Snapshot { Protocol = 2, HostName = Environment.MachineName,
            OsName = RuntimeInformation.OSDescription, Arch = RuntimeInformation.OSArchitecture.ToString(),
            WallSeconds = Environment.TickCount64 / 1000.0,
            CoreMHz = Enumerable.Repeat(double.NaN, _cores).ToArray(),
            CorePct = Enumerable.Repeat(double.NaN, _cores).ToArray(),
            CpuAvgMHz = double.NaN, CpuMaxMHz = double.NaN, CpuPeakMHz = double.NaN,
            DiskReadMBps = double.NaN, DiskWriteMBps = double.NaN, DiskBusyPct = double.NaN,
            CtxSwitchPerSec = double.NaN, Handles = -1, PageUsedGB = double.NaN, PageTotalGB = double.NaN };
        if (OperatingSystem.IsLinux()) ReadLinux(s, dt);
        else if (OperatingSystem.IsMacOS()) ReadMac(s);
        else if (OperatingSystem.IsWindows()) ReadWindows(s);
        else throw new PlatformNotSupportedException("本机采集支持 macOS、Linux 和 Windows。");
        s.CpuName = _cpuName ?? "处理器";
        try { ReadNetwork(s, dt); } catch { Warn(s, "网络吞吐不可用"); s.NetRxMBps = s.NetTxMBps = double.NaN; }
        ReadProcesses(s, dt);
        if (!OperatingSystem.IsMacOS()) ReadNvidia(s);
        s.NormalizeGpus();
        return s;
    }

    private double CpuDelta(string key, double total, double idle)
    {
        var previous = _cpu.GetValueOrDefault(key, (total, idle));
        _cpu[key] = (total, idle);
        double delta = total - previous.total;
        return delta > 0 ? Math.Clamp(100 * (1 - (idle - previous.idle) / delta), 0, 100) : 0;
    }

    internal static (double Total, double Idle) LinuxCpuCounters(string line)
    {
        var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(Number).ToArray();
        return (fields.Sum(), fields[3] + (fields.Length > 4 ? fields[4] : 0));
    }
    private void ReadLinux(Snapshot s, double dt)
    {
        var lines = File.ReadAllLines("/proc/stat");
        foreach (var line in lines.Where(l => l.StartsWith("cpu")))
        {
            string key = line.Split(' ')[0];
            var (total, idle) = LinuxCpuCounters(line);
            double value = CpuDelta(key, total, idle);
            if (key == "cpu") s.CpuPct = value;
            else if (int.TryParse(key[3..], out int i) && i < _cores) s.CorePct[i] = value;
        }
        var cpuInfo = FileText("/proc/cpuinfo");
        _cpuName ??= Regex.Match(cpuInfo, @"(?:model name|Hardware)\s*:\s*([^\n]+)").Groups[1].Value.Trim();
        var frequencies = Regex.Matches(cpuInfo, @"cpu MHz\s*:\s*([\d.]+)").Select(m => Number(m.Groups[1].Value)).ToArray();
        for (int i = 0; i < _cores; i++)
        {
            var value = Number(FileText($"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_cur_freq").Trim()) / 1000;
            s.CoreMHz[i] = double.IsFinite(value) ? value : i < frequencies.Length ? frequencies[i] : double.NaN;
        }
        var finite = s.CoreMHz.Where(double.IsFinite).ToArray();
        if (finite.Length > 0) { s.CpuAvgMHz = finite.Average(); s.CpuMaxMHz = finite.Max(); }
        s.CpuPeakMHz = Number(FileText("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq").Trim()) / 1000;
        var memory = File.ReadAllLines("/proc/meminfo").Select(l => l.Split(':')).ToDictionary(a => a[0], a => Number(a[1].Trim().Split(' ')[0]));
        s.MemTotalGB = memory["MemTotal"] / 1048576;
        s.MemUsedGB = (memory["MemTotal"] - memory.GetValueOrDefault("MemAvailable", memory.GetValueOrDefault("MemFree"))) / 1048576;
        s.MemPct = s.MemUsedGB / s.MemTotalGB * 100;
        s.PageTotalGB = memory.GetValueOrDefault("SwapTotal") / 1048576;
        s.PageUsedGB = (memory.GetValueOrDefault("SwapTotal") - memory.GetValueOrDefault("SwapFree")) / 1048576;
        s.UptimeSeconds = Number(FileText("/proc/uptime").Split(' ')[0]);
        var disks = new Dictionary<string, (double read, double write, double busy)>();
        double rd = 0, wr = 0, busy = 0;
        foreach (var path in Directory.EnumerateDirectories("/sys/block"))
        {
            var name = Path.GetFileName(path);
            if (new[] { "loop", "ram", "zram", "dm-", "md" }.Any(name.StartsWith)) continue;
            var fields = FileText(path + "/stat").Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
            if (fields.Length < 10) continue;
            var value = (read: fields[2] * 512, write: fields[6] * 512, busy: fields[9]);
            disks[name] = value;
            if (!_disks.TryGetValue(name, out var previous)) continue;
            rd += Math.Max(0, value.read - previous.read); wr += Math.Max(0, value.write - previous.write);
            busy = Math.Max(busy, Math.Clamp((value.busy - previous.busy) / dt / 10, 0, 100));
        }
        _disks = disks;
        s.FixedDisks = disks.Count;
        if (disks.Count > 0) { s.DiskReadMBps = rd / dt / 1048576; s.DiskWriteMBps = wr / dt / 1048576; s.DiskBusyPct = busy; }
    }

    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern uint mach_host_self();
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "task_self_trap")] private static extern uint mach_task_self();
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int mach_port_deallocate(uint task, uint name);
    private void ReadMac(Snapshot s)
    {
        if (_machHost == 0) _machHost = mach_host_self();
        var ticks = new uint[4]; uint count = 4;
        if (host_statistics(_machHost, 3, ticks, ref count) != 0) throw new IOException("macOS CPU 计数器不可用");
        s.CpuPct = CpuDelta("cpu", ticks.Sum(v => (double)v), ticks[2]);
        _cpuName ??= Command("/usr/sbin/sysctl", 3000, "-n", "machdep.cpu.brand_string").Trim();
        s.MemTotalGB = Number(Command("/usr/sbin/sysctl", 3000, "-n", "hw.memsize").Trim()) / 1073741824;
        s.MemUsedGB = MacMemoryUsed(Command("/usr/bin/vm_stat", 3000), s.MemTotalGB * 1073741824) / 1073741824;
        s.MemPct = s.MemUsedGB / s.MemTotalGB * 100;
        s.UptimeSeconds = Environment.TickCount64 / 1000.0;
        if (_macCards == null)
        {
            try { _macCards = ParseMacGpus(Command("/usr/sbin/system_profiler", 15000, "-json", "SPDisplaysDataType")); }
            catch { _macCards = new(); }
        }
        s.Gpus = _macCards;
        Warn(s, "macOS 本机 GPU 仅显示设备信息；负载、显存、温度、功耗及 CPU 频率、磁盘吞吐暂不可用");
    }

    internal static double MacMemoryUsed(string vmStat, double totalBytes)
    {
        double size = Number(Regex.Match(vmStat, @"page size of (\d+) bytes").Groups[1].Value);
        double Pages(string key) => Number(Regex.Match(vmStat, Regex.Escape(key) + @":\s*(\d+)").Groups[1].Value);
        // Reclaimable inactive and speculative pages are excluded from this estimate.
        double free = Pages("Pages free") + Pages("Pages inactive") + Pages("Pages speculative");
        if (!double.IsFinite(size) || !double.IsFinite(free)) throw new InvalidDataException("无法解析 vm_stat");
        return Math.Clamp(totalBytes - free * size, 0, totalBytes);
    }
    internal static List<GpuInfo> ParseMacGpus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<GpuInfo>();
        if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var items)) return result;
        foreach (var item in items.EnumerateArray())
        {
            string name = item.TryGetProperty("sppci_model", out var model) ? model.GetString() : item.TryGetProperty("_name", out var n) ? n.GetString() : "Mac GPU";
            result.Add(new GpuInfo { Index = result.Count, Id = "mac:" + result.Count + ":" + name, Name = name, Available = true });
        }
        return result;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong Total, Available, PageTotal, PageAvailable, VirtualTotal, VirtualAvailable, Extended; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    private void ReadWindows(Snapshot s)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new IOException("CPU 计数器不可用");
        s.CpuPct = CpuDelta("cpu", kernel + user, idle);
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new IOException("内存计数器不可用");
        s.MemTotalGB = m.Total / 1073741824.0; s.MemUsedGB = (m.Total - m.Available) / 1073741824.0;
        s.MemPct = s.MemUsedGB / s.MemTotalGB * 100;
        s.UptimeSeconds = Environment.TickCount64 / 1000.0;
        _cpuName ??= Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
        Warn(s, "Windows 完整本机传感器请使用 PerfTickerRemote.exe；此版本优先适配 macOS / Linux");
    }

    private void ReadNetwork(Snapshot s, double dt)
    {
        var next = new Dictionary<string, (long rx, long tx)>();
        double rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (OperatingSystem.IsLinux() && !Directory.Exists("/sys/class/net/" + nic.Name + "/device")) continue;
            if (OperatingSystem.IsMacOS() && !nic.Name.StartsWith("en")) continue;
            var stats = nic.GetIPStatistics();
            next[nic.Id] = (stats.BytesReceived, stats.BytesSent);
            if (!_network.TryGetValue(nic.Id, out var previous)) continue;
            rx += Math.Max(0, stats.BytesReceived - previous.rx); tx += Math.Max(0, stats.BytesSent - previous.tx);
        }
        _network = next;
        s.NetRxMBps = rx / dt / 1048576; s.NetTxMBps = tx / dt / 1048576;
    }

    private void ReadProcesses(Snapshot s, double dt)
    {
        var next = new Dictionary<int, (long start, double cpu)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    int pid = p.Id; long start = p.StartTime.ToUniversalTime().Ticks; double cpu = p.TotalProcessorTime.TotalSeconds;
                    double percent = _processes.TryGetValue(pid, out var prev) && prev.start == start ? Math.Max(0, cpu - prev.cpu) / dt * 100 : 0;
                    next[pid] = (start, cpu);
                    var sample = new ProcSample { Pid = pid, Name = p.ProcessName, CpuPct = percent, CpuNorm = percent / _cores,
                        MemMB = p.WorkingSet64 / 1048576.0, Threads = p.Threads.Count, Handles = -1, GpuPct = double.NaN };
                    s.Procs.Add(sample);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        _processes = next;
        s.ProcCount = s.Procs.Count; s.Threads = s.Procs.Sum(p => (long)p.Threads);
    }

    private void ReadNvidia(Snapshot s)
    {
        try
        {
            var csv = Command("nvidia-smi", 4000, "--query-gpu=index,uuid,name,driver_version,utilization.gpu,utilization.memory,clocks.sm,clocks.mem,clocks.max.sm,memory.used,memory.total,temperature.gpu,power.draw,power.limit,fan.speed", "--format=csv,noheader,nounits");
            s.Gpus = ParseNvidia(csv, out var driver, out var skipped); s.GpuDriver = driver;
            if (skipped) Warn(s, "部分 GPU 数据无效，已跳过");
            if (s.Gpus.Count == 0) Warn(s, "未发现 NVIDIA GPU");
            try
            {
                var procs = Command("nvidia-smi", 4000, "--query-compute-apps=gpu_uuid,pid,used_gpu_memory", "--format=csv,noheader,nounits");
                AttachGpuMemory(procs, s.Procs);
            }
            catch { Warn(s, "GPU 进程显存不可用"); }
        }
        catch { Warn(s, "NVIDIA 指标不可用：请安装驱动并确认 nvidia-smi 可运行；AMD/Intel 本机指标暂未接入"); }
    }
    internal static List<GpuInfo> ParseNvidia(string csv, out string driver, out bool skipped)
    {
        var result = new List<GpuInfo>(); var drivers = new HashSet<string>(); skipped = false;
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // NVIDIA model names contain no commas; numeric fields may contain N/A / Not Supported.
            var a = line.Trim().Split(',').Select(v => v.Trim().Trim('"')).ToArray();
            if (a.Length != 15 || !int.TryParse(a[0], out int index) || index is < 0 or > 10000 || !a[1].StartsWith("GPU-") || result.Any(g => g.Index == index || g.Id == a[1])) { skipped = true; continue; }
            result.Add(new GpuInfo { Index = index, Id = a[1], Name = a[2], Available = true,
                UtilPct = Number(a[4]), MemUtilPct = Number(a[5]), ClockMHz = Number(a[6]), MemClockMHz = Number(a[7]), MaxClockMHz = Number(a[8]),
                MemUsedMB = Number(a[9]), MemTotalMB = Number(a[10]), TempC = Number(a[11]), PowerW = Number(a[12]), PowerLimitW = Number(a[13]), FanPct = Number(a[14]) });
            drivers.Add(a[3]);
        }
        driver = string.Join(", ", drivers);
        return result.OrderBy(g => g.Index).ToList();
    }
    internal static void AttachGpuMemory(string csv, List<ProcSample> processes)
    {
        var byPid = processes.ToDictionary(p => p.Pid);
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var a = line.Split(',').Select(v => v.Trim()).ToArray();
            if (a.Length != 3 || !int.TryParse(a[1], out int pid) || !byPid.TryGetValue(pid, out var p)) continue;
            double memory = Number(a[2]);
            p.GpuMemory[a[0]] = p.GpuMemory.GetValueOrDefault(a[0]) + memory;
        }
        foreach (var p in processes.Where(p => p.GpuMemory.Count > 0)) p.GpuMemMB = p.GpuMemory.Values.Sum();
    }
    public void Dispose()
    {
        if (_machHost != 0) { mach_port_deallocate(mach_task_self(), _machHost); _machHost = 0; }
    }
}
