namespace DeviceBatteryTray.Devices;

/// <summary>程序支持的所有型号（读取顺序即界面里的排列顺序）。</summary>
public static class Providers
{
    public static List<BatteryProvider> Create() => new()
    {
        new VgnKeyboard(),
        new ThunderobotMouse(),
        new GameSirDongle(),
        new FlydigiVader(),
        // 漫步者 HECATE G2（35BB:A209）协议未知，暂不加入，见 docs/protocols/edifier-hecate-g2.md
    };

    /// <summary>支持的型号名（关于框、空状态里展示），和演示模式无关。</summary>
    public static IReadOnlyList<string> Models { get; } = Create().Select(p => p.DisplayName).ToList();
}
