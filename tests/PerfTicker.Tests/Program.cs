using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PerfTicker;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
async Task Until(Func<bool> condition, int seconds = 15)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
    if (!condition()) throw new Exception("Timed out waiting for test condition");
}

string samplePath = args[0];
var samples = JsonDocument.Parse(File.ReadAllText(samplePath)).RootElement;
var sample = RemoteSampler.Parse(samples[0].GetRawText());
Check(sample.IsRemote && sample.CoreMHz.Length > 0 && sample.Gpu.MemTotalMB > 0, "Linux sample schema and remote hardware");
Check(double.IsNaN(sample.Procs[0].GpuPct), "unavailable process GPU utilization is NaN");
try { RemoteSampler.Parse("{\"Protocol\":1}"); throw new Exception("Parser accepted invalid sample"); }
catch (InvalidDataException) { Check(true, "reject incomplete snapshot"); }
var metric = new Metric();
metric.Push(double.NaN, 1, 1);
Check(metric.Count == 0 && metric.ShownText == "—", "missing sensor produces no fabricated candle");
metric.Push(10, 1000, 1);
metric.Push(20, 1500, 1);
metric.Push(double.NaN, 2000, 1);
metric.RebuildCandles(2000);
Check(metric.Count == 2 && metric.Candles.Count == 1 && metric.Candles[0].O == 10 && metric.Candles[0].C == 20 && !metric.Available, "OHLC ignores missing values and retains history");

if (args.Contains("--live"))
{
    var actual = RemoteConfig.Load();
    using var relay = new FaultRelay(actual.Host, actual.Port);
    var testConfig = new RemoteConfig
    {
        Host = "127.0.0.1", Port = relay.Port, User = actual.User,
        PasswordProtected = actual.PasswordProtected, Fingerprint = actual.Fingerprint
    };
    using var sampler = new RemoteSampler(500, () => testConfig);
    await Until(() => sampler.IsLive && sampler.Latest.Tick >= 2);
    Check(sampler.Latest.HostName == sample.HostName, "SSH authenticated and receives real telemetry");
    long before = sampler.Latest.Tick;
    relay.Stall = true;
    await Until(() => !sampler.IsLive, 10);
    Check(sampler.Status == "数据已过期", "silent link stall marks displayed data stale");
    relay.Stall = false;
    await Until(() => sampler.IsLive && sampler.Latest.Tick > before);
    Check(true, "stream resumes after temporary stall");
    relay.Enabled = false;
    relay.DropConnections();
    await Until(() => !sampler.IsLive);
    before = sampler.Latest.Tick;
    await Task.Delay(1500);
    Check(!sampler.IsLive && sampler.Latest.Tick == before, "disconnected sampler does not invent samples or fall back to this PC");
    relay.Enabled = true;
    await Until(() => sampler.IsLive && sampler.Latest.Tick > before, 20);
    Check(true, "automatic SSH reconnection restores remote stream");
    sampler.IntervalMs = 2000;
    before = sampler.Latest.Tick;
    await Until(() => sampler.IsLive && sampler.Latest.Tick >= before + 3, 20);
    Check(sampler.IntervalMs == 2000, "sampling interval change restarts the collector");
    sampler.Dispose();
    Check(!sampler.IsLive, "disposed sampler immediately stops claiming live status");
    var wrong = new RemoteConfig
    {
        Host = actual.Host, Port = actual.Port, User = actual.User,
        PasswordProtected = actual.PasswordProtected, Fingerprint = "SHA256:" + new string('A', 43)
    };
    using var rejected = new RemoteSampler(1000, () => wrong);
    await Until(() => rejected.Error.Contains("指纹"));
    Check(!rejected.IsLive && rejected.Latest.Tick == 0, "mismatched SSH host key rejected");
}
Console.WriteLine($"{passed} checks passed.");

sealed class FaultRelay : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> sockets = new();
    public volatile bool Enabled = true;
    public volatile bool Stall;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FaultRelay(string host, int port)
    {
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var inbound = await listener.AcceptTcpClientAsync(stop.Token);
                    if (!Enabled) { inbound.Dispose(); continue; }
                    var outbound = new TcpClient(AddressFamily.InterNetworkV6);
                    sockets.Add(inbound); sockets.Add(outbound);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await outbound.ConnectAsync(host, port, stop.Token);
                            await Task.WhenAny(Pump(inbound.GetStream(), outbound.GetStream(), false),
                                               Pump(outbound.GetStream(), inbound.GetStream(), true));
                        }
                        catch { }
                        finally { inbound.Dispose(); outbound.Dispose(); }
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }
    private async Task Pump(NetworkStream from, NetworkStream to, bool simulateStall)
    {
        var buffer = new byte[65536];
        while (!stop.IsCancellationRequested)
        {
            while (simulateStall && Stall) await Task.Delay(50, stop.Token);
            int n = await from.ReadAsync(buffer, stop.Token);
            if (n == 0) return;
            while (simulateStall && Stall) await Task.Delay(50, stop.Token);
            await to.WriteAsync(buffer.AsMemory(0, n), stop.Token);
        }
    }
    public void DropConnections()
    {
        while (sockets.TryTake(out var socket)) socket.Dispose();
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); DropConnections(); }
}
