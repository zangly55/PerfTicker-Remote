namespace PerfTicker;

internal sealed class ProcSample
{
    public int Pid;
    public string Name = "";
    public double CpuPct;      // 单核百分比（100 = 占满一颗核心）
    public double GpuPct;      // GPU 引擎占用百分比
    public double GpuMemMB = double.NaN; // NVIDIA compute process memory, unavailable => NaN
    public Dictionary<string, double> GpuMemory = new();
    public double MemoryOnGpu(string id) => string.IsNullOrEmpty(id) ? GpuMemMB
        : GpuMemory.TryGetValue(id, out var value) ? value : double.NaN;
    public double MemMB;       // 工作集
    public int Threads;
    public int Handles;
    public double CpuNorm;     // 占整机百分比
    public double CpuDelta;    // 相对上一采样的 CPU% 变化（「涨跌」）
}

internal sealed class GpuInfo
{
    public int Index;
    public string Id = "";
    public bool Available;
    public string Name = "图形处理器";
    public double UtilPct = double.NaN;
    public double MemUtilPct = double.NaN;
    public double ClockMHz = double.NaN;
    public double MemClockMHz = double.NaN;
    public double MaxClockMHz = double.NaN;
    public double MemUsedMB = double.NaN;
    public double MemTotalMB = double.NaN;
    public double TempC = double.NaN;
    public double PowerW = double.NaN;
    public double PowerLimitW = double.NaN;
    public double FanPct = double.NaN;
    public bool Nvml;
}

internal sealed class Snapshot
{
    public int Protocol = 0;
    public bool IsRemote = false;
    public string HostName = "";
    public string OsName = "";
    public string OsBuild = "";
    public string Arch = "";
    public string GpuDriver = "";
    public string SensorWarning = "";
    public int PhysicalCores = 0;
    public int FixedDisks = 1;
    public double NetMaxGbps = 0;
    public long Tick;          // 单调递增的采样序号
    public double WallSeconds;

    public double CpuPct;                 // 整机
    public double[] CorePct = Array.Empty<double>();
    public double[] CoreMHz = Array.Empty<double>();
    public double CpuAvgMHz;
    public double CpuMaxMHz;
    public double CpuPeakMHz;             // 硬件最大频率（标称）
    public string CpuName = "处理器";

    public double MemUsedGB, MemTotalGB, MemPct;
    public double PageUsedGB, PageTotalGB;

    public GpuInfo Gpu = new();
    public List<GpuInfo> Gpus = new();

    public GpuInfo FindGpu(string id) => Gpus.FirstOrDefault(g => g.Id == id);

    public void NormalizeGpus()
    {
        // Protocol 1 snapshots remain readable by diagnostics and older fixtures.
        if (Protocol == 1 && Gpus.Count == 0 && Gpu.Available) Gpus.Add(Gpu);
        foreach (var gpu in Gpus)
            if (string.IsNullOrWhiteSpace(gpu.Id)) gpu.Id = "index:" + gpu.Index;
        if (Protocol == 1 && Gpus.Count == 1)
            foreach (var p in Procs)
                if (double.IsFinite(p.GpuMemMB)) p.GpuMemory.TryAdd(Gpus[0].Id, p.GpuMemMB);
        Gpu = AggregateGpu(Gpus);
    }

    internal static GpuInfo AggregateGpu(IReadOnlyList<GpuInfo> cards)
    {
        if (cards.Count == 1) return cards[0];
        double Combine(Func<GpuInfo, double> read, string mode)
        {
            var values = cards.Select(read).ToArray();
            // Partial readings must not look like a complete machine total.
            if (values.Length == 0 || values.Any(v => !double.IsFinite(v))) return double.NaN;
            return mode == "sum" ? values.Sum() : mode == "max" ? values.Max() : values.Average();
        }
        return new GpuInfo {
            Available = cards.Any(g => g.Available), Name = cards.Count == 0 ? "GPU 数据不可用" : $"{cards.Count} 张 GPU 汇总",
            UtilPct = Combine(g => g.UtilPct, "avg"), MemUtilPct = Combine(g => g.MemUtilPct, "avg"),
            ClockMHz = Combine(g => g.ClockMHz, "avg"), MemClockMHz = Combine(g => g.MemClockMHz, "avg"),
            MaxClockMHz = Combine(g => g.MaxClockMHz, "avg"), MemUsedMB = Combine(g => g.MemUsedMB, "sum"),
            MemTotalMB = Combine(g => g.MemTotalMB, "sum"), PowerW = Combine(g => g.PowerW, "sum"),
            PowerLimitW = Combine(g => g.PowerLimitW, "sum"), TempC = Combine(g => g.TempC, "max"),
            FanPct = Combine(g => g.FanPct, "max"), Nvml = cards.Count > 0 && cards.All(g => g.Nvml)
        };
    }

    public double DiskReadMBps, DiskWriteMBps, DiskBusyPct;
    public double NetRxMBps, NetTxMBps;

    public double CtxSwitchPerSec;
    public long Threads, Handles, ProcCount;
    public double UptimeSeconds;

    public List<ProcSample> Procs = new();
}
