using DeviceBatteryTray.Hid;

namespace DeviceBatteryTray.Devices;

public enum DeviceKind { Keyboard, Mouse, Gamepad, Headset, Speaker, Other }

public enum ReadState
{
    Ok,          // 读到电量
    Offline,     // 接收器在，但设备休眠 / 关机
    NotFound,    // 接收器没插
    Error,       // 通讯失败
}

public sealed record BatteryReading(ReadState State, int Percent = 0, bool Charging = false, string? Detail = null)
{
    public static BatteryReading NotFound => new(ReadState.NotFound);
    public static BatteryReading Offline => new(ReadState.Offline);
    public static BatteryReading Fail(string why) => new(ReadState.Error, Detail: why);
}

/// <summary>
/// 同一型号下的一台设备：Key 区分同型号的多个接收器（只有一种接收器 / 没插时为空）。
/// Name / Kind 不为空时代替型号的 DisplayName / Kind（通用蓝牙这种一个 provider 读多种设备的情况）。
/// </summary>
public sealed record DeviceReading(string Key, BatteryReading Reading)
{
    public string? Name { get; init; }
    public DeviceKind? Kind { get; init; }
}

public abstract class BatteryProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract DeviceKind Kind { get; }

    /// <summary>读一台设备（只支持单个接收器的型号实现这个即可）。</summary>
    public virtual BatteryReading Read() => ReadAll().FirstOrDefault()?.Reading ?? BatteryReading.NotFound;

    /// <summary>
    /// 读这个型号的所有设备（插了几个同型号接收器就返回几项）。一个都没插时返回一项 Key 为空的 NotFound，
    /// 让界面知道支持这个型号。默认实现只读一台。
    /// </summary>
    public virtual IReadOnlyList<DeviceReading> ReadAll() => new[] { new DeviceReading("", Read()) };

    /// <summary>
    /// 对 VID/PID 下 UsagePage 匹配、可写的每个接口（每个对应一个接收器）执行 read；
    /// 一个都没有时返回 NotFound（接收器在但接口打不开时返回失败）。
    /// </summary>
    protected static IReadOnlyList<DeviceReading> ReadEachReceiver(ushort vid, ushort[] pids, ushort usagePage, Func<HidConnection, BatteryReading> read)
    {
        foreach (var pid in pids) // 多个 PID 时按优先级：前面的 PID 有设备就不再看后面的
        {
            var all = HidEnumerator.Find(vid, pid);
            var targets = all.Where(i => i.UsagePage == usagePage && i.OutputLength > 0).ToList();
            if (targets.Count == 0)
            {
                if (all.Count > 0) return new[] { new DeviceReading("", BatteryReading.Fail("打不开厂商接口")) };
                continue;
            }
            var list = new List<DeviceReading>();
            foreach (var t in targets)
            {
                using var c = HidConnection.Open(t);
                list.Add(new DeviceReading(t.InstanceKey, c == null ? BatteryReading.Fail("打不开厂商接口") : read(c)));
            }
            return list;
        }
        return new[] { new DeviceReading("", BatteryReading.NotFound) };
    }

    protected static bool Present(ushort vid, ushort pid) => HidEnumerator.Find(vid, pid).Count > 0;

    private static int _seq;
    protected static ushort NextSeq() => (ushort)(Interlocked.Increment(ref _seq) % 0xFFFF + 1);
}
