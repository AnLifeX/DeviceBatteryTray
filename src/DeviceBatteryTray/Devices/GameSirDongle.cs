namespace DeviceBatteryTray.Devices;

/// <summary>盖世小鸡 2.4G 接收器。协议见 docs/protocols/gamesir-dongle.md</summary>
public sealed class GameSirDongle : BatteryProvider
{
    const ushort Vid = 0x3537, Pid = 0x103E, UsagePage = 0xFFA0;

    public override string Id => "gamesir-3537-103e";
    public override string DisplayName => "盖世小鸡手柄";
    public override DeviceKind Kind => DeviceKind.Gamepad;
    public override ConnectionMode Connection => ConnectionMode.Receiver24G;

    public override IReadOnlyList<DeviceReading> ReadAll() => ReadEachReceiver(Vid, new[] { Pid }, UsagePage, ReadOne);

    static BatteryReading ReadOne(Hid.HidConnection c)
    {
        // 接收器在这个接口上持续推送全 0 的 06 包，只认 06 0E 0A 01 开头的设备信息应答
        var rx = c.Request(new byte[] { 0x07, 0x03, 0x0A, 0x01 },
            b => b[0] == 0x06 && b[1] == 0x0E && b[2] == 0x0A && b[3] == 0x01, timeoutMs: 1500);
        if (rx == null) return BatteryReading.Offline;

        // 06 0E 0A 01 | id | fw[2] | mac[6] | battery
        int level = rx[13];
        if (rx[4] == 0 && level == 0) return BatteryReading.Offline;
        return new BatteryReading(ReadState.Ok, Math.Min(level, 100));
    }
}
