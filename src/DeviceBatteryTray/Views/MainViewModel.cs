using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DeviceBatteryTray.Devices;

namespace DeviceBatteryTray.Views;

using DeviceBatteryTray.Ui;

public sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => _run();
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public enum BatteryLevel { Good, Caution, Critical, Charging, None }

/// <summary>设备页的一张卡片。</summary>
public sealed class DeviceItem : Observable
{
    private string _status = "", _percentText = "", _stateText = "", _note = "";
    private double _percent;
    private bool _online, _needsAdmin;
    private BatteryLevel _level;

    public DeviceItem(string id, DeviceKind kind, string name)
    {
        Id = id;
        Name = name;
        Glyph = Tray.Glyphs.Device(kind);
    }

    public string Id { get; }
    public string Name { get; }
    public string Glyph { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public bool Online { get => _online; private set => Set(ref _online, value); }
    public bool Charging { get => _charging; private set => Set(ref _charging, value); }
    private bool _charging;
    /// <summary>百分比下面的小字：充电中 / 估算。</summary>
    public string Note { get => _note; private set => Set(ref _note, value); }
    public bool NeedsAdmin { get => _needsAdmin; private set => Set(ref _needsAdmin, value); }
    public BatteryLevel Level { get => _level; private set => Set(ref _level, value); }

    public void Update(BatteryReading r)
    {
        Online = r.State == ReadState.Ok;
        NeedsAdmin = r.Detail == FlydigiVader.NeedAdminDetail;
        Percent = Online ? r.Percent : 0;
        PercentText = Online ? $"{r.Percent}%" : "";
        Charging = Online && r.Charging;
        Note = !Online ? "" : r.Charging ? "充电中" : r.Detail ?? "";
        StateText = Online || NeedsAdmin ? "" : r.State == ReadState.Offline ? "休眠" : "读取失败";
        Level = !Online ? BatteryLevel.None : r.Charging ? BatteryLevel.Charging
            : r.Percent <= 20 ? BatteryLevel.Critical : r.Percent <= 40 ? BatteryLevel.Caution : BatteryLevel.Good;
        Status = r.State switch
        {
            ReadState.Ok => string.Join(" · ", new[] { "已连接", r.Charging ? "充电中" : null, r.Detail }.Where(s => s != null)),
            ReadState.Offline => "接收器已连接，设备休眠或已关机",
            _ => NeedsAdmin ? "接收器已连接，需要管理员权限才能读取电量" : r.Detail ?? "读取失败",
        };
    }
}

public sealed record Choice(string Text, object Value);

/// <summary>主页面的数据和操作；状态来自 <see cref="IAppModel"/>，设置直接读写 <see cref="AppSettings"/>。</summary>
public sealed class MainViewModel : Observable
{
    static readonly TimeSpan MinSpin = TimeSpan.FromMilliseconds(700);

    private readonly IAppModel _m;
    private DateTime _spinUntil;
    private bool _spinning;

    public MainViewModel(IAppModel model)
    {
        _m = model;
        RefreshCommand = new RelayCommand(() => { _spinUntil = DateTime.Now + MinSpin; Spinning = true; _m.Refresh(); });
        UpdateActionCommand = new RelayCommand(() => { if (_m.Update.Status == UpdateStatus.Available) _m.InstallUpdate(); else _m.CheckUpdate(); });
        ApplyUpdateCommand = new RelayCommand(() => _m.InstallUpdate());
        ElevateCommand = new RelayCommand(() => _m.RestartElevated());
        OpenLogCommand = new RelayCommand(() => { if (File.Exists(Log.FilePath)) Process.Start("explorer.exe", $"/select,\"{Log.FilePath}\""); });
        OpenRepoCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(Updater.RepoUrl) { UseShellExecute = true }));
        ShowSettingsCommand = new RelayCommand(() => NavigateRequested?.Invoke(MainPage.Settings));
        OpenMainCommand = new RelayCommand(() => { CloseRequested?.Invoke(); _m.ShowMain(MainPage.Home); });
        OpenSettingsCommand = new RelayCommand(() => { CloseRequested?.Invoke(); _m.ShowMain(MainPage.Settings); });
        ExitCommand = new RelayCommand(() => { CloseRequested?.Invoke(); _m.Exit(); });
        ExitDemoCommand = new RelayCommand(() => _m.Demo = false);
        BackCommand = new RelayCommand(() => NavigateRequested?.Invoke(MainPage.Home));
        Sync();
    }

    public ObservableCollection<DeviceItem> Devices { get; } = new();

    /// <summary>弹出面板只显示在线（读到电量）的设备。</summary>
    public ObservableCollection<DeviceItem> OnlineDevices { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand UpdateActionCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand ElevateCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenRepoCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand OpenMainCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand ExitDemoCommand { get; }

    /// <summary>弹出面板里点了会打开主页面的操作，面板据此先关闭自己。</summary>
    public event Action? CloseRequested;
    public ICommand BackCommand { get; }

    /// <summary>页面切换请求（由主窗口处理动画）。</summary>
    public event Action<MainPage>? NavigateRequested;

    public bool Spinning { get => _spinning; private set => Set(ref _spinning, value); }

    /// <summary>模型状态变化时调用（UI 线程）。</summary>
    public void Sync()
    {
        // 主页面：接收器在的设备；弹出面板：在线的设备
        var all = _m.Devices;
        SyncList(Devices, all.Where(d => d.Reading is { State: not ReadState.NotFound }).ToList());
        SyncList(OnlineDevices, all.Where(d => d.Online).ToList());
        SupportedText = "支持：" + string.Join("、", _m.Devices.Select(d => d.Name));
        if (!_m.Refreshing && DateTime.Now >= _spinUntil) Spinning = false;
        else if (_m.Refreshing) Spinning = true;
        RaiseAll();
    }

    /// <summary>增量同步：保留已有卡片对象，避免整列表重建闪烁、重复播放进场动画。</summary>
    static void SyncList(ObservableCollection<DeviceItem> list, List<DeviceStatus> shown)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (shown.All(d => d.Id != list[i].Id)) list.RemoveAt(i);
        for (int i = 0; i < shown.Count; i++)
        {
            var d = shown[i];
            var item = list.FirstOrDefault(x => x.Id == d.Id);
            if (item == null) list.Insert(Math.Min(i, list.Count), item = new DeviceItem(d.Id, d.Kind, d.Name));
            item.Update(d.Reading!);
        }
    }

    /// <summary>旋转至少 700ms，避免刷新太快看不到反馈。</summary>
    public void Tick()
    {
        if (Spinning && !_m.Refreshing && DateTime.Now >= _spinUntil) Spinning = false;
    }

    // ----- 设备页 -----

    public bool HasDevices => Devices.Count > 0;
    public bool HasOnlineDevices => OnlineDevices.Count > 0;
    public bool NoOnlineDevices => OnlineDevices.Count == 0;
    public string FlyoutUpdatedText => _m.Refreshing ? "正在刷新…" : _m.UpdatedAt is { } at ? $"{at:HH:mm} 更新" : "正在读取…";

    public bool Demo
    {
        get => _m.Demo;
        set => _m.Demo = value;
    }
    public bool Loading => _m.UpdatedAt == null;
    public bool NoDevices => !HasDevices && !Loading;
    public string SupportedText { get; private set; } = "";
    public string UpdatedText => _m.Refreshing ? "正在刷新…" : _m.UpdatedAt is { } at ? $"{at:HH:mm:ss} 更新" : "正在读取…";

    public bool UpdateBannerOpen => _m.Update.Status is UpdateStatus.Available or UpdateStatus.Downloading;
    public string UpdateBannerTitle => $"发现新版本 v{_m.Update.Info?.Version}";
    public string UpdateBannerMessage => _m.Update.Status == UpdateStatus.Downloading
        ? $"正在下载… {_m.Update.Progress}%" : "更新后程序会自动重启";
    public bool CanApplyUpdate => _m.Update.Status == UpdateStatus.Available;

    // ----- 设置页 -----

    public string Version => "v" + Updater.CurrentText;

    public bool AutoStart
    {
        get => _m.AutoStart;
        set { _m.AutoStart = value; RaiseAll(); }
    }

    public string AutoStartDescription => _m.Elevated ? "登录 Windows 后在后台启动（使用计划任务，不弹 UAC）" : "登录 Windows 后在后台启动";

    public IReadOnlyList<Choice> RefreshChoices { get; } =
        AppSettings.RefreshChoices.Select(v => new Choice(AppSettings.RefreshText(v), v)).ToList();
    public object RefreshSeconds
    {
        get => AppSettings.Current.RefreshSeconds;
        set { if (value is int v) AppSettings.Change(s => s.RefreshSeconds = v); }
    }

    public IReadOnlyList<Choice> LowBatteryChoices { get; } =
        AppSettings.LowBatteryChoices.Select(v => new Choice(AppSettings.LowBatteryText(v), v)).ToList();
    public object LowBatteryPercent
    {
        get => AppSettings.Current.LowBatteryPercent;
        set { if (value is int v) AppSettings.Change(s => s.LowBatteryPercent = v); }
    }

    public bool ReadFlydigi
    {
        get => AppSettings.Current.ReadFlydigi;
        set { AppSettings.Change(s => s.ReadFlydigi = value); _m.Refresh(); }
    }
    public string FlydigiDescription => !AppSettings.Current.ReadFlydigi ? "已关闭，程序不会请求管理员权限"
        : _m.Elevated ? "需要管理员权限（当前已是管理员）" : "需要管理员权限，只在检测到黑武士接收器时才请求";

    public IReadOnlyList<Choice> ThemeChoices { get; } =
        Enum.GetValues<ThemeMode>().Select(v => new Choice(AppSettings.ThemeText(v), v)).ToList();
    public object Theme
    {
        get => AppSettings.Current.Theme;
        set { if (value is ThemeMode v) AppSettings.Change(s => s.Theme = v); }
    }

    public bool AutoUpdate
    {
        get => AppSettings.Current.AutoUpdate;
        set => AppSettings.Change(s => s.AutoUpdate = value);
    }
    public string AutoUpdateDescription => AppSettings.Current.AutoUpdate ? "发现新版本时自动下载、替换程序并重启" : "已关闭，可以在下面手动检查更新";

    public IReadOnlyList<Choice> UpdateCheckChoices { get; } =
        AppSettings.UpdateCheckChoices.Select(v => new Choice(AppSettings.UpdateCheckText(v), v)).ToList();
    public object UpdateCheckHours
    {
        get => AppSettings.Current.UpdateCheckHours;
        set { if (value is int v) AppSettings.Change(s => s.UpdateCheckHours = v); }
    }

    public string UpdateStatusText => _m.Update.Status switch
    {
        UpdateStatus.Checking => "正在检查更新…",
        UpdateStatus.UpToDate => "已是最新版本",
        UpdateStatus.Available => $"发现新版本 v{_m.Update.Info?.Version}",
        UpdateStatus.Downloading => $"正在下载 v{_m.Update.Info?.Version}… {_m.Update.Progress}%",
        UpdateStatus.Failed => "检查更新失败：" + _m.Update.Error,
        _ => "还没有检查过更新",
    };
    public string UpdateActionText => _m.Update.Status switch
    {
        UpdateStatus.Available => "立即更新",
        UpdateStatus.Checking or UpdateStatus.Downloading => "请稍候…",
        _ => "检查更新",
    };
    public bool UpdateActionEnabled => _m.Update.Status is not (UpdateStatus.Checking or UpdateStatus.Downloading);
    public bool UpdateActionPrimary => _m.Update.Status == UpdateStatus.Available;

    public string LogPath => Log.FilePath;
    public string RepoUrl => Updater.RepoUrl;
}
