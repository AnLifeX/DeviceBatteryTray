namespace DeviceBatteryTray.Devices;

/// <summary>雷神 ML903 mini（ITON 方案，星闪接收器 / 有线）。协议见 docs/protocols/thunderobot-ml903mini.md</summary>
public sealed class ThunderobotMouse : BatteryProvider
{
    const ushort Vid = 0x369B, UsagePage = 0xFFA0;
    const ushort ReceiverPid = 0x0114, WiredPid = 0x00F3; // 星闪接收器优先，其次有线直连
    const byte OutId = 0xA0, InId = 0xA1, CmdStatus = 0x50, TagBattery = 0x25;

    // 经接收器读取时回包没有充电标志（放在充电底座上也一样），只能按电量走势推断；按接收器分别记录
    static readonly Dictionary<string, ChargeTrend> Trends = new();

    public override string Id => "thunderobot-369b";
    public override string DisplayName => "雷神 ML903 mini 鼠标";
    public override DeviceKind Kind => DeviceKind.Mouse;

    // 星闪接收器优先：插着接收器时读所有接收器，否则读有线直连的鼠标
    public override IReadOnlyList<DeviceReading> ReadAll()
    {
        var rx = ReadEachReceiver(Vid, new[] { ReceiverPid }, UsagePage, ReadOne);
        if (rx.Count == 1 && rx[0].Reading.State == ReadState.NotFound)
            return ReadEachReceiver(Vid, new[] { WiredPid }, UsagePage, ReadOne);

        // 接收器和数据线同时插着：接收器回包没有充电标志，改用有线读到的电量和充电状态。
        // 只有一个接收器时才能确定是同一只鼠标；仍归在接收器名下，界面里不会多出一台设备
        if (rx.Count == 1 && Present(Vid, WiredPid))
        {
            var wired = ReadEachReceiver(Vid, new[] { WiredPid }, UsagePage, ReadOne);
            if (wired.Count == 1 && wired[0].Reading.State == ReadState.Ok)
                return new[] { rx[0] with { Reading = wired[0].Reading } };
        }
        return rx;
    }

    static BatteryReading ReadOne(Hid.HidConnection c)
    {
        var rx = c.Request(new byte[] { OutId, CmdStatus }, b => b[0] == InId && b[1] == CmdStatus);
        if (rx == null) return BatteryReading.Fail("无应答");
        if (rx[2] == 0) return BatteryReading.Offline; // 鼠标休眠：整包为 0

        // 在 TLV 区里找电量 tag 0x25，下一字节：低 7 位百分比，最高位充电（只有有线直连时会置位）
        for (int i = 3; i < rx.Length - 1; i++)
            if (rx[i] == TagBattery)
            {
                int percent = Math.Min(rx[i + 1] & 0x7F, 100);
                bool charging = (rx[i + 1] & 0x80) != 0;
                if (c.Interface.Pid == ReceiverPid) charging |= Trend(c.Interface.InstanceKey).Update(DateTime.Now, percent);
                return new BatteryReading(ReadState.Ok, percent, charging);
            }
        return BatteryReading.Fail("应答里没有电量字段");
    }

    static ChargeTrend Trend(string key)
    {
        lock (Trends)
        {
            if (!Trends.TryGetValue(key, out var t)) Trends[key] = t = new ChargeTrend();
            return t;
        }
    }
}
