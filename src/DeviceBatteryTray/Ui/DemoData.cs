using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Ui;

/// <summary>
/// 演示模式的模拟设备（12 台，覆盖各种情况）：同型号多台（自动编号）、正常 / 偏低 / 低电量、充电中、
/// 休眠、读取失败、需要管理员权限。每次刷新数值会变化，方便检查设备很多时的界面。
/// </summary>
public static class DemoData
{
    /// <summary>
    /// 演示模式只给本地开发测试用：只有 Debug 编译才有入口（设置里的开关、--demo、--demo-ui），
    /// 发布的 Release 版本里没有。
    /// </summary>
#if DEBUG
    public const bool Available = true;
#else
    public const bool Available = false;
#endif

    static int _keyboard = 73, _gamepad = 35, _mouse2 = 9;

    const string Vgn = "VGN V98Pro V2 键盘", Thunder = "雷神 ML903 mini 鼠标", GameSir = "盖世小鸡手柄", Flydigi = "飞智 黑武士 3 Pro";

    public static IReadOnlyList<RawDevice> Raw => new List<RawDevice>
    {
        new("vgn-320f-5088", "demo-a", DeviceKind.Keyboard, Vgn, new(ReadState.Ok, _keyboard)),
        new("vgn-320f-5088", "demo-b", DeviceKind.Keyboard, Vgn, new(ReadState.Ok, 52)),
        new("vgn-320f-5088", "demo-c", DeviceKind.Keyboard, Vgn, new(ReadState.Ok, 33)),
        new("thunderobot-369b", "demo-a", DeviceKind.Mouse, Thunder, new(ReadState.Ok, 18)),
        new("thunderobot-369b", "demo-b", DeviceKind.Mouse, Thunder, new(ReadState.Ok, 88)),
        new("thunderobot-369b", "demo-c", DeviceKind.Mouse, Thunder, new(ReadState.Ok, _mouse2, Charging: true)),
        new("gamesir-3537-103e", "demo-a", DeviceKind.Gamepad, GameSir, new(ReadState.Ok, _gamepad, Charging: true)),
        new("gamesir-3537-103e", "demo-b", DeviceKind.Gamepad, GameSir, new(ReadState.Ok, 64)),
        new("gamesir-3537-103e", "demo-c", DeviceKind.Gamepad, GameSir, BatteryReading.Offline),
        new("flydigi-vader3", "", DeviceKind.Gamepad, Flydigi, new(ReadState.Ok, 100, Detail: "估算")),
        new("demo-headset", "", DeviceKind.Headset, "无线耳机（演示）", BatteryReading.Offline),
        new("demo-error", "", DeviceKind.Mouse, "读取失败的鼠标（演示）", BatteryReading.Fail("无应答")),
    };

    /// <summary>模拟一次刷新：键盘掉 1%，两个充电中的设备往上充（充满后重来）。</summary>
    public static void Tick()
    {
        _keyboard = _keyboard <= 41 ? 73 : _keyboard - 1;
        _gamepad = _gamepad >= 100 ? 35 : Math.Min(100, _gamepad + 3);
        _mouse2 = _mouse2 >= 100 ? 9 : Math.Min(100, _mouse2 + 5);
    }
}
