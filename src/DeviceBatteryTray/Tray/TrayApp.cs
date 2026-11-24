using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DeviceBatteryTray.Devices;
using DeviceBatteryTray.Ui;
using Microsoft.Win32;

namespace DeviceBatteryTray.Tray;

/// <summary>
/// 主逻辑：定时轮询各设备，把「在线」的设备显示在任务栏内嵌条上；点击内嵌条弹出面板；
/// 双击 exe（或另一个实例发来「显示」信号）时打开主页面；定时检查更新。
/// 只有内嵌条嵌不进任务栏（如竖向任务栏）时，才显示一个程序托盘图标作为入口。
/// </summary>
public sealed class TrayApp : ApplicationContext, IAppModel
{
    static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromSeconds(20);

    private readonly List<BatteryProvider> _providers;
    private readonly Dictionary<string, BatteryReading> _last = new();
    private readonly HashSet<string> _lowNotified = new();
    private readonly NotifyIcon _app;
    private readonly TaskbarWidget _widget = new();
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _timer, _updateTimer;
    private readonly EventWaitHandle _exitEvent, _showEvent;
    private Flyout? _flyout;
    private DateTime _flyoutClosedAt;
    private MainWindow? _main;
    private int _polling;
    private bool? _autoStart;
    private int _updateHours = AppSettings.Current.UpdateCheckHours;
    private Action? _balloonClick;

    public event Action? Changed;
    public DateTime? UpdatedAt { get; private set; }
    public bool Refreshing { get; private set; }
    public UpdateState Update { get; private set; } = UpdateState.Idle;
    public bool Elevated { get; } = FlydigiVader.IsElevated();

    public IReadOnlyList<DeviceStatus> Devices =>
        _providers.Select(p => new DeviceStatus(p.Id, p.Kind, p.DisplayName, _last.GetValueOrDefault(p.Id))).ToList();

    public TrayApp(List<BatteryProvider> providers, bool showMain, bool justUpdated)
    {
        _providers = providers;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _app = new NotifyIcon { Text = "设备电量", Icon = AppIcon.Sized(SystemInformation.SmallIconSize.Width), Visible = false };
        _app.MouseUp += (_, _) => ToggleFlyout();
        _app.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _app.BalloonTipClosed += (_, _) => _balloonClick = null;
        _widget.Clicked += _ => ToggleFlyout();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; // 深浅色 / 强调色切换
        SystemEvents.SessionEnding += (_, _) => _widget.Detach();      // 注销/关机前还原任务栏
        Application.ApplicationExit += (_, _) => _widget.Detach();
        AppSettings.Changed += OnSettingsChanged;

        // 跨进程信号：--exit 让本实例退出；再次双击 exe 让本实例打开主页面。
        // 管理员实例创建的事件要允许普通权限进程打开，所以给 Everyone 设置访问权限。
        _exitEvent = CreateSharedEvent(Program.ExitEventName);
        _showEvent = CreateSharedEvent(Program.ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => _ui.Post(_ => ExitThread(), null), null, -1, true);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _ui.Post(_ => ShowMain(MainPage.Home), null), null, -1, false);

        DeviceBatteryTray.AutoStart.Migrate();

        var interval = TimeSpan.FromSeconds(AppSettings.Current.RefreshSeconds);
        _timer = new System.Threading.Timer(_ => PollAsync(), null, TimeSpan.Zero, interval);
        _updateTimer = new System.Threading.Timer(_ => _ui.Post(_ => CheckUpdate(manual: false), null), null,
            FirstUpdateCheck, TimeSpan.FromHours(_updateHours));

        if (showMain) _ui.Post(_ => ShowMain(MainPage.Home), null);
        if (justUpdated) _ui.Post(_ => Notify("已更新", $"设备电量已更新到 v{Updater.CurrentText}", ToolTipIcon.Info), null);
    }

    static EventWaitHandle CreateSharedEvent(string name)
    {
        var sec = new EventWaitHandleSecurity();
        sec.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, sec);
    }

    private void RaiseChanged() => Changed?.Invoke();

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => _ui.Post(_ =>
    {
        Theme.Invalidate();
        RefreshWidget();
        RaiseChanged();
    }, null);

    private void OnSettingsChanged()
    {
        var interval = TimeSpan.FromSeconds(AppSettings.Current.RefreshSeconds);
        _timer.Change(interval, interval);
        if (_updateHours != AppSettings.Current.UpdateCheckHours)
        {
            _updateHours = AppSettings.Current.UpdateCheckHours;
            var period = TimeSpan.FromHours(_updateHours);
            _updateTimer.Change(period, period);
        }
        _lowNotified.Clear();
        RefreshWidget();
        RaiseChanged();
    }

    // ---------- 轮询 ----------

    public void Refresh() => PollAsync();

    private void PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        _ui.Post(_ => { Refreshing = true; RaiseChanged(); }, null);
        Task.Run(() =>
        {
            try
            {
                var results = new Dictionary<string, BatteryReading>();
                foreach (var p in _providers)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try { results[p.Id] = p.Read(); }
                    catch (Exception e) { results[p.Id] = BatteryReading.Fail(e.Message); Log.W($"{p.Id} 异常: {e}"); }
                    Log.W($"{p.Id}: {results[p.Id]} ({sw.ElapsedMilliseconds}ms)");
                }
                _ui.Post(_ =>
                {
                    foreach (var kv in results) _last[kv.Key] = kv.Value;
                    UpdatedAt = DateTime.Now;
                    Refreshing = false;
                    RefreshWidget();
                    RaiseChanged();
                }, null);
            }
            finally { Interlocked.Exchange(ref _polling, 0); }
        });
    }

    /// <summary>只有读到电量的设备才显示。</summary>
    private List<DeviceStatus> Online() => Devices.Where(d => d.Online).ToList();

    private void RefreshWidget()
    {
        var online = Online();
        bool widgetShown = _widget.Update(online.Select(d => new TaskbarWidget.Item(d.Kind, d.Name, d.Reading!)).ToList());

        // 内嵌条显示不了（竖向任务栏 / 没有在线设备）时，用程序托盘图标兜底，保证随时能打开面板
        _app.Visible = !widgetShown;
        _app.Text = Truncate(online.Count == 0 ? "设备电量：没有在线设备"
            : string.Join("\n", online.Select(d => $"{d.Name} {d.Reading!.Percent}%")));

        // 低电量提醒：每次跌破阈值提醒一次，充电或回升后重置
        int threshold = AppSettings.Current.LowBatteryPercent;
        foreach (var d in online)
        {
            var r = d.Reading!;
            if (threshold > 0 && !r.Charging && r.Percent <= threshold)
            {
                if (_lowNotified.Add(d.Id)) Notify("电量低", $"{d.Name} 只剩 {r.Percent}%", ToolTipIcon.Warning);
            }
            else _lowNotified.Remove(d.Id);
        }
    }

    // ---------- 面板 / 主页面 ----------

    private void ToggleFlyout()
    {
        // 点内嵌条时面板会先因失去焦点关闭，紧接着又收到点击：这种情况视为「关闭」，不再重开
        if (_flyout != null || (DateTime.Now - _flyoutClosedAt).TotalMilliseconds < 300) return;

        _flyout = new Flyout(this);
        _flyout.FormClosed += (_, _) => { _flyout?.Dispose(); _flyout = null; _flyoutClosedAt = DateTime.Now; };
        _flyout.ShowNear(Cursor.Position);
    }

    public void ShowMain(MainPage page)
    {
        if (_main == null || _main.IsDisposed)
        {
            _main = new MainWindow(this, page);
            _main.FormClosed += (_, _) => _main = null;
            _main.Show();
        }
        else
        {
            _main.Page = page;
            if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
            _main.Show();
        }
        _main.Activate();
        SetForegroundWindow(_main.Handle);
    }

    public void Exit() => ExitThread();

    /// <summary>
    /// 以管理员身份重启：起一个普通权限的新进程，等本进程退出后按正常启动流程提权
    /// （有最高权限计划任务就用它，不弹 UAC；否则弹 UAC，取消的话以普通权限继续运行）。
    /// </summary>
    public void RestartElevated()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath) { UseShellExecute = false };
        psi.ArgumentList.Add("--wait-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        System.Diagnostics.Process.Start(psi);
        Log.W("以管理员身份重启");
        ExitThread();
    }

    public bool AutoStart
    {
        get => _autoStart ??= DeviceBatteryTray.AutoStart.Enabled;
        set
        {
            DeviceBatteryTray.AutoStart.Enabled = value;
            _autoStart = DeviceBatteryTray.AutoStart.Enabled;
            RaiseChanged();
        }
    }

    // ---------- 更新 ----------

    private void SetUpdate(UpdateState s) { Update = s; RaiseChanged(); }

    public void CheckUpdate() => CheckUpdate(manual: true);

    private async void CheckUpdate(bool manual)
    {
        if (!manual && (!AppSettings.Current.AutoUpdate || !Updater.CanSelfUpdate)) return; // 开发版本不自动检查
        if (Update.Status is UpdateStatus.Checking or UpdateStatus.Downloading) return;
        SetUpdate(new UpdateState(UpdateStatus.Checking));
        try
        {
            var info = await Updater.CheckAsync();
            Log.W($"检查更新：当前 {Updater.CurrentText}，最新 {(info == null ? "无更新" : "v" + info.Version)}");
            if (info == null) { SetUpdate(new UpdateState(UpdateStatus.UpToDate)); return; }
            SetUpdate(new UpdateState(UpdateStatus.Available, info));
            if (manual) return;

            InstallUpdate(); // 自动更新：下载后替换并重启
        }
        catch (Exception e)
        {
            Log.W("检查更新失败: " + e.Message);
            SetUpdate(new UpdateState(UpdateStatus.Failed, Error: e.Message));
        }
    }

    public async void InstallUpdate()
    {
        if (Update is not { Status: UpdateStatus.Available, Info: { } info }) return;
        SetUpdate(new UpdateState(UpdateStatus.Downloading, info));
        try
        {
            await Updater.DownloadAsync(info, new Progress<int>(p => SetUpdate(new UpdateState(UpdateStatus.Downloading, info, p))));
            Log.W($"已下载 v{info.Version}，退出后由更新程序替换并重启");
            Updater.ApplyAndRestart(info, showMain: _main is { Visible: true });
            ExitThread();
        }
        catch (Exception e)
        {
            Log.W("更新失败: " + e);
            SetUpdate(new UpdateState(UpdateStatus.Failed, info, Error: e.Message));
            Notify("更新失败", e.Message, ToolTipIcon.Error, () => ShowMain(MainPage.Settings));
        }
    }

    // ---------- 通知 ----------

    /// <summary>气泡提醒要挂在一个可见的托盘图标上；程序图标平时隐藏，提醒时临时显示。</summary>
    private void Notify(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        bool wasHidden = !_app.Visible;
        _app.Visible = true;
        _app.ShowBalloonTip(5000, title, text, icon);
        if (!wasHidden) return;
        void Hide(object? s, EventArgs e)
        {
            _app.BalloonTipClosed -= Hide; _app.BalloonTipClicked -= Hide;
            RefreshWidget();
        }
        _app.BalloonTipClosed += Hide; _app.BalloonTipClicked += Hide;
    }

    static string Truncate(string s) => s.Length <= 127 ? s : s[..127]; // NotifyIcon.Text 上限

    protected override void ExitThreadCore()
    {
        _timer.Dispose();
        _updateTimer.Dispose();
        AppSettings.Changed -= OnSettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _flyout?.Close();
        _main?.Close();
        _widget.Detach();
        _widget.Dispose();
        _app.Visible = false; _app.Dispose();
        _showEvent.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
}
