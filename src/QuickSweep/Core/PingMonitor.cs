using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QuickSweep.Core;

public readonly record struct PingSample(DateTime T, double? RouterMs, double? NetMs);

public sealed class PingStats
{
    public int Count, Lost, Spikes;
    public double Avg, Max;
    /// <summary>When the slowest reply came in (null when every packet was lost).</summary>
    public DateTime? MaxAt;
    /// <summary>The newest reading: milliseconds, or null when that packet was lost.</summary>
    public double? LastMs;
    public double LossPct => Count == 0 ? 0 : 100.0 * Lost / Count;
}

/// <summary>One small ICMP echo to the default gateway and to 1.1.1.1 every few seconds.</summary>
public sealed class PingMonitor
{
    public const double SpikeMs = 100;
    // One sample per 3 s, each stamped with its time. Bounded: at the cap the oldest 500 go, so about 5 hours stay (the Network page needs 10 minutes, a session summary the whole game).
    public const int HistoryCap = 6000;
    const int TrimBy = 500;
    readonly List<PingSample> _hist = new();
    readonly object _l = new();
    public string RouterAddress { get; private set; } = "";
    public bool Enabled { get; set; } = true;
    public PingSample? Last { get; private set; }
    public event Action<PingSample>? Sampled;
    CancellationTokenSource? _cts;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }
    public void Stop() => _cts?.Cancel();

    static string FindGateway()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork && !gw.Address.Equals(IPAddress.Any))
                        return gw.Address.ToString();
            }
        }
        catch { }
        return "";
    }

    static async Task<double?> One(string addr, Ping p)
    {
        if (addr == "") return null;
        try
        {
            var r = await p.SendPingAsync(addr, 1000);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { return null; }
    }

    async Task Loop(CancellationToken ct)
    {
        using var p1 = new Ping();
        using var p2 = new Ping();
        int n = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Enabled)
                {
                    if (n++ % 20 == 0) RouterAddress = FindGateway();
                    var a = One(RouterAddress, p1);
                    var b = One("1.1.1.1", p2);
                    await Task.WhenAll(a, b);
                    var s = new PingSample(DateTime.Now, a.Result, b.Result);
                    Record(s);
                    Sampled?.Invoke(s);
                }
                await Task.Delay(3000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log.Write("ping loop: " + e.Message); try { await Task.Delay(5000, ct); } catch { break; } }
        }
    }

    /// <summary>Appends one stamped reading; at the cap the oldest ones are dropped, so memory never grows.</summary>
    internal void Record(PingSample s)
    {
        lock (_l)
        {
            _hist.Add(s);
            if (_hist.Count > HistoryCap) _hist.RemoveRange(0, TrimBy);
        }
        Last = s;
    }

    /// <summary>Demo mode: fills the history with sample readings instead of pinging.</summary>
    internal void Seed(IEnumerable<PingSample> samples)
    {
        lock (_l) { _hist.Clear(); _hist.AddRange(samples); }
        Last = _hist.LastOrDefault();
    }

    public List<PingSample> History(DateTime since)
    {
        lock (_l) return _hist.Where(s => s.T >= since).ToList();
    }

    public static (PingStats router, PingStats net) Summarize(IEnumerable<PingSample> samples)
    {
        var r = new PingStats(); var n = new PingStats();
        double rs = 0, ns = 0; int rc = 0, nc = 0;
        foreach (var s in samples)
        {
            r.Count++; n.Count++;
            r.LastMs = s.RouterMs; n.LastMs = s.NetMs;
            if (s.RouterMs is double a) { rs += a; rc++; if (a > r.Max || r.MaxAt == null) { r.Max = a; r.MaxAt = s.T; } if (a >= SpikeMs) r.Spikes++; } else { r.Lost++; r.Spikes++; }
            if (s.NetMs is double b) { ns += b; nc++; if (b > n.Max || n.MaxAt == null) { n.Max = b; n.MaxAt = s.T; } if (b >= SpikeMs) n.Spikes++; } else { n.Lost++; n.Spikes++; }
        }
        r.Avg = rc > 0 ? rs / rc : 0; n.Avg = nc > 0 ? ns / nc : 0;
        return (r, n);
    }
}
