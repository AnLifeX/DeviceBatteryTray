using DeviceBatteryTray.Hid;

namespace DeviceBatteryTray.Devices;

public enum DeviceKind { Keyboard, Mouse, Gamepad, Headset }

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

public abstract class BatteryProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract DeviceKind Kind { get; }

    public abstract BatteryReading Read();

    /// <summary>找到指定 VID/PID 下 UsagePage 匹配、可写的接口并打开。</summary>
    protected static HidConnection? OpenVendorInterface(ushort vid, ushort pid, ushort usagePage)
    {
        var target = HidEnumerator.Find(vid, pid).FirstOrDefault(i => i.UsagePage == usagePage && i.OutputLength > 0);
        return target == null ? null : HidConnection.Open(target);
    }

    protected static bool Present(ushort vid, ushort pid) => HidEnumerator.Find(vid, pid).Count > 0;

    private static int _seq;
    protected static ushort NextSeq() => (ushort)(Interlocked.Increment(ref _seq) % 0xFFFF + 1);
}
