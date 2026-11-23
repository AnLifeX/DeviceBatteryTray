namespace DeviceBatteryTray.Devices;

/// <summary>VGN / VXE / ATK 2.4G 键盘接收器（Evision 方案）。协议见 docs/protocols/vgn-v98pro-v2.md</summary>
public sealed class VgnKeyboard : BatteryProvider
{
    const ushort Vid = 0x320F, Pid = 0x5088, UsagePage = 0xFF1C;
    const byte ReportId = 0x04, CmdPowerInfo = 0x1A, CmdOffline = 0xFF;

    public override string Id => "vgn-320f-5088";
    public override string DisplayName => "VGN V98Pro V2 键盘";
    public override DeviceKind Kind => DeviceKind.Keyboard;

    public override BatteryReading Read()
    {
        using var c = OpenVendorInterface(Vid, Pid, UsagePage);
        if (c == null) return Present(Vid, Pid) ? BatteryReading.Fail("打不开厂商接口") : BatteryReading.NotFound;

        // 键盘休眠时接收器有时完全不回，重试一次仍无应答就按休眠处理
        byte[]? rx = null;
        for (int attempt = 0; attempt < 2 && rx == null; attempt++)
        {
            ushort seq = NextSeq();
            rx = c.Request(new byte[] { ReportId, (byte)seq, (byte)(seq >> 8), CmdPowerInfo },
                b => b[0] == ReportId && b[1] == (byte)seq && b[2] == (byte)(seq >> 8));
        }
        if (rx == null) return BatteryReading.Offline;
        if (rx[3] == CmdOffline) return BatteryReading.Offline;
        if (rx[3] != CmdPowerInfo) return BatteryReading.Fail($"未知应答 0x{rx[3]:X2}");

        // 带 Report ID 的缓冲：数据区从 [1] 开始，电量在数据区偏移 7 → [8]，充电标志 [9]
        return new BatteryReading(ReadState.Ok, Math.Min((int)rx[8], 100), rx[9] != 0);
    }
}
