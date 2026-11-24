using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Ui;

/// <summary>
/// 一个设备的当前状态；Reading 为 null 表示还没读过。Name 是显示名（用户改过就是用户的名字），
/// Model 是型号名；Visible 为 false 表示用户在主页面关掉了显示。
/// </summary>
public sealed record DeviceStatus(string Id, DeviceKind Kind, string Name, BatteryReading? Reading, string Model = "", bool Visible = true)
{
    public bool Online => Reading?.State == ReadState.Ok;

    /// <summary>任务栏和弹出面板只显示在线且没被隐藏的设备。</summary>
    public bool Shown => Online && Visible;
}

public enum MainPage { Home, Settings, About }

/// <summary>面板和主页面看到的程序状态与操作（TrayApp 实现；--dump 预览用演示数据实现）。</summary>
public interface IAppModel
{
    IReadOnlyList<DeviceStatus> Devices { get; }
    DateTime? UpdatedAt { get; }
    bool Refreshing { get; }
    UpdateState Update { get; }
    bool AutoStart { get; set; }
    bool Elevated { get; }

    /// <summary>演示模式：用模拟设备代替真实读取（不保存，重启后关闭）。</summary>
    bool Demo { get; set; }

    /// <summary>状态变化，在 UI 线程触发。</summary>
    event Action? Changed;

    void Refresh();
    void CheckUpdate();
    void InstallUpdate();
    void ShowMain(MainPage page);

    /// <summary>以管理员身份重启（读取飞智黑武士用）。</summary>
    void RestartElevated();
    void Exit();
}
