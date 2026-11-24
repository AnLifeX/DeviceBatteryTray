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
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum BatteryLevel { Good, Caution, Critical, Charging, None }

/// <summary>设备页的一张卡片。</summary>
public sealed class DeviceItem : Observable
{
    private string _status = "", _percentText = "", _stateText = "", _note = "";
    private double _percent;
    private bool _online, _needsAdmin;
    private BatteryLevel _level;

    private string _name = "", _model = "", _editName = "";
    private bool _visible = true, _editing;

    public DeviceItem(string id, DeviceKind kind)
    {
        Id = id;
        Glyph = Tray.Glyphs.Device(kind);
        RenameCommand = new RelayCommand(() => { EditName = Name; Editing = true; });
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => Editing = false);
    }

    public string Id { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Model { get => _model; private set => Set(ref _model, value); }

    /// <summary>主页面的「显示」开关：关掉后不在任务栏和弹出面板显示，也不弹低电量提醒。</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            Set(ref _visible, value);
            AppSettings.Change(s =>
            {
                s.HiddenDevices.Remove(Id);
                if (!value) s.HiddenDevices.Add(Id);
            });
        }
    }

    // ----- 重命名（就地编辑） -----
    public bool Editing { get => _editing; set => Set(ref _editing, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public ICommand RenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }

    /// <summary>保存名称；清空或改回型号名则恢复默认（同型号多台时自动编号）。</summary>
    void CommitRename()
    {
        if (!Editing) return;
        Editing = false;
        string n = EditName.Trim();
        AppSettings.Change(s =>
        {
            if (n.Length == 0 || n == Model) s.DeviceNames.Remove(Id);
            else s.DeviceNames[Id] = n;
        });
    }

    public void Update(DeviceStatus d)
    {
        Name = d.Name;
        Model = d.Model;
        if (_visible != d.Visible) { _visible = d.Visible; Raise(nameof(Visible)); }
        Update(d.Reading!);
        // 用户改过名字的设备，在状态前面带上型号，方便知道是什么设备（自动编号的「型号 2」不用重复）
        if (d.Model.Length > 0 && !d.Name.StartsWith(d.Model)) Status = d.Model + " · " + Status;
    }
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

    void Update(BatteryReading r)
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
        ShowAboutCommand = new RelayCommand(() => AboutOpen = true);
        CloseAboutCommand = new RelayCommand(() => AboutOpen = false);
        // 浅色 ↔ 深色（从「跟随系统」第一次点击时，切到和当前相反的颜色）
        ToggleThemeCommand = new RelayCommand(() => AppSettings.Change(s => s.Theme = Ui.Theme.Current.Dark ? ThemeMode.Light : ThemeMode.Dark));
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
    public ICommand ShowAboutCommand { get; }
    public ICommand CloseAboutCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    private bool _aboutOpen;

    /// <summary>关于框（主页面上的模态框）是否打开。</summary>
    public bool AboutOpen { get => _aboutOpen; set => Set(ref _aboutOpen, value); }


    /// <summary>页面切换请求（由主窗口处理动画）。</summary>
    public event Action<MainPage>? NavigateRequested;

    public bool Spinning { get => _spinning; private set => Set(ref _spinning, value); }

    /// <summary>模型状态变化时调用（UI 线程）。</summary>
    public void Sync()
    {
        // 主页面：接收器在的设备；弹出面板：在线的设备
        var all = _m.Devices;
        SyncList(Devices, all.Where(d => d.Reading is { State: not ReadState.NotFound }).ToList());
        SyncList(OnlineDevices, all.Where(d => d.Shown).ToList());
        SupportedText = "支持：" + string.Join("、", Providers.Models);
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
            if (item == null) list.Insert(Math.Min(i, list.Count), item = new DeviceItem(d.Id, d.Kind));
            else if (list.IndexOf(item) != i) list.Move(list.IndexOf(item), i);
            item.Update(d);
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

    /// <summary>新版本横幅：发现新版本 / 下载中 / 下载失败（可重试）。只检查失败（没有新版本信息）时不显示。</summary>
    public bool UpdateBannerOpen => _m.Update.Status is UpdateStatus.Available or UpdateStatus.Downloading || UpdateDownloadFailed;
    public bool UpdateDownloadFailed => _m.Update is { Status: UpdateStatus.Failed, Info: not null };
    public string UpdateBannerTitle => UpdateDownloadFailed
        ? $"新版本 v{_m.Update.Info?.Version} 下载失败" : $"发现新版本 v{_m.Update.Info?.Version}";
    public string UpdateBannerMessage => _m.Update.Status switch
    {
        UpdateStatus.Downloading => $"正在下载… {_m.Update.Progress}%",
        UpdateStatus.Failed => _m.Update.Error ?? "",
        _ => "更新后程序会自动重启",
    };
    public bool CanApplyUpdate => _m.Update.Status == UpdateStatus.Available || UpdateDownloadFailed;
    public string ApplyUpdateText => UpdateDownloadFailed ? "重试" : "立即更新";
    public Wpf.Ui.Controls.SymbolRegular UpdateBannerSymbol => UpdateDownloadFailed
        ? Wpf.Ui.Controls.SymbolRegular.ErrorCircle24 : Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
    public bool UpdateDownloading => _m.Update.Status == UpdateStatus.Downloading;
    /// <summary>弹出面板的一行提示：下载中显示进度。</summary>
    public string FlyoutUpdateText => UpdateDownloading
        ? $"正在下载 v{_m.Update.Info?.Version}… {_m.Update.Progress}%" : UpdateBannerTitle;
    public int UpdateProgress => _m.Update.Progress;

    // ----- 设置页 -----

    public string Version => "v" + Updater.CurrentText;

    /// <summary>主题按钮：图标表示当前颜色（太阳 = 浅色，月亮 = 深色），点一下切换。</summary>
    public Wpf.Ui.Controls.SymbolRegular ThemeSymbol => Ui.Theme.Current.Dark
        ? Wpf.Ui.Controls.SymbolRegular.WeatherMoon24 : Wpf.Ui.Controls.SymbolRegular.WeatherSunny24;
    public string ThemeTip => Ui.Theme.Current.Dark ? "深色（点击切换到浅色）" : "浅色（点击切换到深色）";

    /// <summary>关于页用的大图标。</summary>
    public System.Windows.Media.ImageSource? AppIconImage => MainWindow.AppIconSource;
    public string Description => "在任务栏上查看无线键盘、鼠标、手柄的电量，不需要安装各家驱动。";

    public bool AutoStart
    {
        get => _m.AutoStart;
        set { _m.AutoStart = value; RaiseAll(); }
    }

    public string AutoStartDescription => _m.Elevated ? "登录 Windows 后在后台启动（使用计划任务，不弹 UAC）" : "登录 Windows 后在后台启动";

    /// <summary>刷新间隔（秒），可以直接输入，5 ~ 3600。</summary>
    public double? RefreshSeconds
    {
        get => AppSettings.Current.RefreshSeconds;
        set { if (value is double v && (int)Math.Round(v) != AppSettings.Current.RefreshSeconds) AppSettings.Change(s => s.RefreshSeconds = (int)Math.Round(v)); }
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
