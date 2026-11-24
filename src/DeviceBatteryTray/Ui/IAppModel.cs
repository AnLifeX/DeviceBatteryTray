using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Ui;

/// <summary>一个设备的当前状态；Reading 为 null 表示还没读过。</summary>
public sealed record DeviceStatus(string Id, DeviceKind Kind, string Name, BatteryReading? Reading)
{
    public bool Online => Reading?.State == ReadState.Ok;
}

public enum MainPage { Home, Settings }

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
