using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Renci.SshNet;

namespace PerfTicker;

// JSON null denotes an unavailable sensor, never a fabricated zero.
internal sealed class SensorNumberConverter : JsonConverter<double>
{
    public override bool HandleNull => true;
    public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}

internal sealed class RemoteSampler : IMonitorSampler
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true, Converters = { new SensorNumberConverter() }
    };
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Func<RemoteConfig> _loadConfig;
    private Snapshot _latest = new();
    private string _status = "正在连接";
    private string _error = "";
    private int _intervalMs = 1000;
    private long _tick, _receivedAt;
    private volatile bool _connected;
    public Snapshot Latest => Volatile.Read(ref _latest);
    public int Cores => Math.Max(1, Latest.CoreMHz.Length);
    public string CpuName => Latest.CpuName;
    public string GpuDriverVersion => Latest.GpuDriver;
    public int IntervalMs
    {
        get => Volatile.Read(ref _intervalMs);
        set => Volatile.Write(ref _intervalMs, Math.Clamp(value, 500, 10000));
    }
    public bool IsLive => !_stop.IsCancellationRequested && _connected && Environment.TickCount64 - Interlocked.Read(ref _receivedAt)
        < Math.Max(5000, IntervalMs * 3);
    public string Status => _connected && !IsLive ? "数据已过期" : Volatile.Read(ref _status);
    public string Error => Volatile.Read(ref _error);

    public RemoteSampler(int interval = 1000, Func<RemoteConfig> loadConfig = null)
    {
        _loadConfig = loadConfig ?? RemoteConfig.Load;
        IntervalMs = interval;
        _worker = Task.Run(RunAsync);
    }

    internal static Snapshot Parse(string line)
    {
        var s = JsonSerializer.Deserialize<Snapshot>(line, JsonOptions)
            ?? throw new InvalidDataException("采集器返回空数据。");
        if (s.Protocol is not (1 or 2) || !s.IsRemote || s.CoreMHz == null || s.CoreMHz.Length == 0
            || s.CoreMHz.Length > 4096 || s.Gpu == null || s.Procs == null || s.Gpus == null
            || s.Gpus.Count > 1024 || s.Gpus.Any(g => g == null || g.Index < 0 || g.Index > 10000)
            || s.Procs.Any(p => p == null || p.GpuMemory == null)
            || !double.IsFinite(s.MemTotalGB) || s.MemTotalGB <= 0
            || !double.IsFinite(s.CpuPct) || s.CpuPct < 0 || s.CpuPct > 100)
            throw new InvalidDataException("采集器数据格式或必需指标无效。");
        s.NormalizeGpus();
        if (s.Gpus.Select(g => g.Id).Distinct().Count() != s.Gpus.Count
            || s.Gpus.Select(g => g.Index).Distinct().Count() != s.Gpus.Count)
            throw new InvalidDataException("GPU 编号或设备标识重复。");
        return s;
    }

    internal static string CollectorCommand(int interval, int gpu = -1)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PerfTicker.collector.py");
        using var reader = new StreamReader(stream ?? throw new FileNotFoundException("采集脚本缺失"));
        string code = Convert.ToBase64String(Encoding.UTF8.GetBytes(reader.ReadToEnd()));
        return "python3 -u -c \"import base64;exec(base64.b64decode('" + code + "'))\" "
            + (interval / 1000.0).ToString(CultureInfo.InvariantCulture) + " "
            + gpu.ToString(CultureInfo.InvariantCulture);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _connected = false;
                _status = "正在连接";
                var config = _loadConfig();
                config.Validate();
                using var connection = new SshConnection(config);
                var client = connection.Client;
                using var stopConnection = _stop.Token.Register(() => { try { client.Dispose(); } catch { } });
                connection.Connect();
                int interval = IntervalMs;
                using var command = client.CreateCommand(CollectorCommand(interval));
                command.CommandTimeout = Timeout.InfiniteTimeSpan;
                var execution = command.BeginExecute();
                using var output = new StreamReader(command.OutputStream, Encoding.UTF8);
                try
                {
                    while (!_stop.IsCancellationRequested && interval == IntervalMs)
                    {
                        var line = await output.ReadLineAsync(_stop.Token).AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(Math.Max(15, interval / 1000.0 * 4)), _stop.Token);
                        if (line == null)
                            throw new IOException("采集器已退出：" + command.Error.Trim());
                        if (line.Length > 8 * 1024 * 1024) throw new InvalidDataException("采集数据过大。");
                        var snapshot = Parse(line);
                        snapshot.Tick = Interlocked.Increment(ref _tick);
                        snapshot.WallSeconds = Environment.TickCount64 / 1000.0;
                        Volatile.Write(ref _latest, snapshot);
                        Interlocked.Exchange(ref _receivedAt, Environment.TickCount64);
                        _error = snapshot.SensorWarning ?? "";
                        _status = "远程行情在线";
                        _connected = true;
                    }
                }
                finally
                {
                    _connected = false;
                    // Closing the SSH channel also closes stdout, terminating this read-only collector.
                    try { command.CancelAsync(); } catch { }
                    try { client.Disconnect(); } catch { }
                }
            }
            catch (Exception ex) when (!_stop.IsCancellationRequested)
            {
                _connected = false;
                _status = "断线 · 自动重连";
                _error = ex.Message;
                Diag.Write("REMOTE", ex.Message);
                try { await Task.Delay(3000, _stop.Token); } catch (OperationCanceledException) { }
            }
            catch when (_stop.IsCancellationRequested) { break; }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // Never block the UI waiting for a dead network connection.
    }
}
