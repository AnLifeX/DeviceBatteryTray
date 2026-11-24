namespace DeviceBatteryTray.Devices;

/// <summary>雷神 ML903 mini（ITON 方案，星闪接收器 / 有线）。协议见 docs/protocols/thunderobot-ml903mini.md</summary>
public sealed class ThunderobotMouse : BatteryProvider
{
    const ushort Vid = 0x369B, UsagePage = 0xFFA0;
    static readonly ushort[] Pids = { 0x0114, 0x00F3 }; // 星闪接收器优先，其次有线直连
    const byte OutId = 0xA0, InId = 0xA1, CmdStatus = 0x50, TagBattery = 0x25;

    public override string Id => "thunderobot-369b";
    public override string DisplayName => "雷神 ML903 mini 鼠标";
    public override DeviceKind Kind => DeviceKind.Mouse;

    // 星闪接收器优先：插着接收器时读所有接收器，否则读有线直连的鼠标
    public override IReadOnlyList<DeviceReading> ReadAll() => ReadEachReceiver(Vid, Pids, UsagePage, ReadOne);

    static BatteryReading ReadOne(Hid.HidConnection c)
    {
        var rx = c.Request(new byte[] { OutId, CmdStatus }, b => b[0] == InId && b[1] == CmdStatus);
        if (rx == null) return BatteryReading.Fail("无应答");
        if (rx[2] == 0) return BatteryReading.Offline; // 鼠标休眠：整包为 0

        // 在 TLV 区里找电量 tag 0x25，下一字节：低 7 位百分比，最高位充电
        for (int i = 3; i < rx.Length - 1; i++)
            if (rx[i] == TagBattery)
                return new BatteryReading(ReadState.Ok, Math.Min(rx[i + 1] & 0x7F, 100), (rx[i + 1] & 0x80) != 0);
        return BatteryReading.Fail("应答里没有电量字段");
    }
}
