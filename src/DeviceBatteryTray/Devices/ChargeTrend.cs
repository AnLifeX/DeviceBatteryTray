namespace DeviceBatteryTray.Devices;

/// <summary>
/// 设备不上报充电标志时，按电量走势推断是否在充电（如雷神鼠标放在充电底座上、经星闪接收器读取）。
/// 实测：充电约 3~4 分钟涨 1%（≈ +0.25%/分钟），放电约 7~10 分钟掉 1%（≈ -0.12%/分钟），
/// 但单次读数会在 ±1~2% 之间来回跳，所以不看单个点，而是对一段时间内的读数做线性回归看斜率：
/// - 最近 <see cref="StartWindow"/> 斜率 ≥ <see cref="StartSlope"/> → 判定充电中；
/// - 充电中：最近 <see cref="StartWindow"/> 斜率 ≤ <see cref="DropSlope"/>（在掉电），
///   或未充满时最近 <see cref="StallWindow"/> 斜率 ≤ <see cref="StallSlope"/>（不再上涨）→ 判定未充电；
/// - 两次读数间隔超过 <see cref="Gap"/>（电脑休眠 / 设备长时间离线）→ 清空历史重新判断。
/// 充满后电量停在 100%，会一直显示充电中，直到拿下来电量开始下降。
/// 代价：放上底座后要过 10 分钟左右才会显示充电。
/// </summary>
public sealed class ChargeTrend
{
    public static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan StallWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);
    public const double StartSlope = 0.15, DropSlope = -0.05, StallSlope = 0.02; // %/分钟
    const int FullPercent = 99;

    private readonly List<(DateTime T, int P)> _samples = new();
    private bool _charging;
    private DateTime _since;

    public bool Charging => _charging;

    public bool Update(DateTime now, int percent)
    {
        if (_samples.Count > 0 && (now - _samples[^1].T > Gap || now < _samples[^1].T)) Reset();
        _samples.RemoveAll(s => now - s.T > StallWindow);
        _samples.Add((now, percent));

        var recent = Slope(now, StartWindow);
        if (!_charging)
        {
            if (recent >= StartSlope) { _charging = true; _since = now; }
        }
        else if (recent <= DropSlope)
        {
            _charging = false;
        }
        // 停涨判断要等窗口里全是充电后的读数，否则会被刚上底座前的放电段拉低
        else if (now - _since >= StallWindow && RecentMax(now) < FullPercent
                 && Slope(now, StallWindow) is double s && s <= StallSlope)
        {
            _charging = false;
        }
        return _charging;
    }

    public void Reset()
    {
        _samples.Clear();
        _charging = false;
    }

    /// <summary>窗口内读数的最小二乘斜率（%/分钟）；读数跨度不足 <see cref="MinSpan"/> 时返回 null。</summary>
    private double? Slope(DateTime now, TimeSpan window)
    {
        var pts = _samples.Where(s => now - s.T <= window).ToList();
        if (pts.Count < 4 || pts[^1].T - pts[0].T < MinSpan) return null;
        var t0 = pts[0].T;
        double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, p) in pts)
        {
            double x = (t - t0).TotalMinutes;
            sx += x; sy += p; sxx += x * x; sxy += x * p;
        }
        double d = n * sxx - sx * sx;
        return d <= 0 ? null : (n * sxy - sx * sy) / d;
    }

    private int RecentMax(DateTime now) =>
        _samples.Where(s => now - s.T <= TimeSpan.FromMinutes(5)).Max(s => s.P);
}
