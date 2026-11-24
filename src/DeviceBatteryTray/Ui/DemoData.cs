using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Ui;

/// <summary>
/// 演示模式的模拟设备：键盘缓慢掉电、手柄充电中、鼠标低电量、耳机休眠，
/// 每次刷新数值会变化，方便在没有真实设备时查看任务栏电量条、弹出面板和主页面的效果。
/// </summary>
public static class DemoData
{
    static int _keyboard = 73, _gamepad = 35;

    public static IReadOnlyList<DeviceStatus> Devices => new List<DeviceStatus>
    {
        new("demo-keyboard", DeviceKind.Keyboard, "VGN V98Pro V2 键盘", new(ReadState.Ok, _keyboard)),
        new("demo-mouse", DeviceKind.Mouse, "雷神 ML903 mini 鼠标", new(ReadState.Ok, 18)),
        new("demo-gamesir", DeviceKind.Gamepad, "盖世小鸡手柄", new(ReadState.Ok, _gamepad, Charging: true)),
        new("demo-flydigi", DeviceKind.Gamepad, "飞智 黑武士 3 Pro", new(ReadState.Ok, 100, Detail: "估算")),
        new("demo-headset", DeviceKind.Headset, "无线耳机（演示）", BatteryReading.Offline),
    };

    /// <summary>模拟一次刷新：键盘掉 1%，手柄充 3%（充满后从 35% 重来）。</summary>
    public static void Tick()
    {
        _keyboard = _keyboard <= 41 ? 73 : _keyboard - 1;
        _gamepad = _gamepad >= 100 ? 35 : Math.Min(100, _gamepad + 3);
    }
}
