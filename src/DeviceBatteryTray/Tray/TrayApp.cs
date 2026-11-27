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
    static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(1);

    private readonly List<BatteryProvider> _providers;
    /// <summary>最近一次读到的所有设备（同型号多个接收器各一项）；还没读过时每个型号一项、读数为 null。</summary>
    private List<RawDevice> _raw;
    private readonly HashSet<string> _lowNotified = new();
    private readonly NotifyIcon _app;
    private readonly TaskbarWidget _widget = new();
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _timer, _updateTimer;
    private readonly EventWaitHandle _exitEvent, _showEvent;
    private Views.FlyoutWindow? _flyout;
    private bool _demo;
    private DateTime _flyoutClosedAt;
    private Views.MainWindow? _main;
    private int _polling;
    private bool? _autoStart;
    private Action? _balloonClick;
    private bool _notifying; // 有提醒正在显示：托盘图标要保持可见

    public event Action? Changed;
    public DateTime? UpdatedAt { get; private set; }
    public bool Refreshing { get; private set; }
    /// <summary>界面看到的更新状态：演示模式下是模拟的，真实的检查照常在后台进行、互不影响。</summary>
    public UpdateState Update => _demo ? _demoUpdate : _update;
    private UpdateState _update = UpdateState.Idle;
    private UpdateState _demoUpdate = DemoUpdateAvailable;
    public bool Elevated { get; } = FlydigiVader.IsElevated();

    public IReadOnlyList<DeviceStatus> Devices => DeviceList.Build(_demo ? DemoData.Raw : _raw);

    /// <summary>
    /// 演示模式：用模拟设备代替真实读取，低电量提醒照常弹（方便看样子），并模拟「发现新版本 → 下载」；
    /// 只在内存里，重启后关闭。
    /// </summary>
    public bool Demo
    {
        get => _demo;
        set
        {
            if (_demo == value || (value && !DemoData.Available)) return; // Release 版没有演示模式
            _demo = value;
            Log.W("演示模式：" + (value ? "开" : "关"));
            _demoUpdate = DemoUpdateAvailable;
            _lowNotified.Clear();
            RefreshWidget();
            RaiseChanged();
            PollAsync();
        }
    }

    public TrayApp(List<BatteryProvider> providers, bool showMain, bool demo = false)
    {
        _demo = demo;
        _providers = providers;
        _raw = providers.Select(p => new RawDevice(p.Id, "", p.Kind, p.DisplayName, null)).ToList();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _app = new NotifyIcon { Text = "设备电量", Icon = AppIcon.Sized(SystemInformation.SmallIconSize.Width), Visible = false };
        _app.MouseUp += (_, _) => ToggleFlyout();
        _app.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; OnBalloonDone(); a?.Invoke(); };
        _app.BalloonTipClosed += (_, _) => { _balloonClick = null; OnBalloonDone(); };
        _widget.Clicked += _ => ToggleFlyout();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; // 任务栏深浅色变化：电量条跟随任务栏颜色
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
            FirstUpdateCheck, UpdateCheckInterval);

        if (showMain) _ui.Post(_ => ShowMain(MainPage.Home), null);
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
        RefreshWidget();
        RaiseChanged();
    }, null);

    private void OnSettingsChanged()
    {
        var interval = TimeSpan.FromSeconds(AppSettings.Current.RefreshSeconds);
        _timer.Change(interval, interval);
        // 不清空已提醒记录：否则切主题、改名等任何设置变化都会把低电量再提醒一遍。
        // 改了阈值也不用清：RefreshWidget 会把高于新阈值的移出记录，新跌破的照常提醒。
        RefreshWidget();
        RaiseChanged();
    }

    // ---------- 轮询 ----------

    public void Refresh() => PollAsync();

    private void PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        _ui.Post(_ => { Refreshing = true; RaiseChanged(); }, null);
        if (_demo)
        {
            // 演示模式：不碰真实设备，模拟一次读取（稍等一下，让刷新动画可见）
            Task.Delay(400).ContinueWith(_ => _ui.Post(_ =>
            {
                DemoData.Tick();
                UpdatedAt = DateTime.Now;
                Refreshing = false;
                Interlocked.Exchange(ref _polling, 0);
                RefreshWidget();
                RaiseChanged();
            }, null));
            return;
        }
        Task.Run(() =>
        {
            try
            {
                var results = new List<RawDevice>();
                foreach (var p in _providers)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    IReadOnlyList<DeviceReading> readings;
                    try { readings = p.ReadAll(); }
                    catch (Exception e) { readings = new[] { new DeviceReading("", BatteryReading.Fail(e.Message)) }; Log.W($"{p.Id} 异常: {e}"); }
                    foreach (var r in readings)
                    {
                        results.Add(new RawDevice(p.Id, r.Key, p.Kind, p.DisplayName, r.Reading));
                        Log.W($"{DeviceList.IdOf(p.Id, r.Key)}: {r.Reading} ({sw.ElapsedMilliseconds}ms)");
                    }
                }
                _ui.Post(_ =>
                {
                    _raw = results;
                    DeviceList.MigrateMovedDevices(results);
                    UpdatedAt = DateTime.Now;
                    Refreshing = false;
                    RefreshWidget();
                    RaiseChanged();
                }, null);
            }
            finally { Interlocked.Exchange(ref _polling, 0); }
        });
    }

    /// <summary>任务栏和面板只显示读到电量、且没被用户隐藏的设备。</summary>
    private List<DeviceStatus> Online() => Devices.Where(d => d.Shown).ToList();

    private void RefreshWidget()
    {
        var online = Online();
        bool widgetShown = _widget.Update(online.Select(d => new TaskbarWidget.Item(d.Kind, d.Name, d.Reading!)).ToList());

        // 内嵌条显示不了（竖向任务栏 / 没有在线设备）时，用程序托盘图标兜底，保证随时能打开面板
        _app.Visible = !widgetShown || _notifying;
        _app.Text = Truncate(online.Count == 0 ? "设备电量：没有在线设备"
            : string.Join("\n", online.Select(d => $"{d.Name} {d.Reading!.Percent}%")));

        // 低电量提醒：每次跌破阈值提醒一次，充电或回升后重置
        int threshold = AppSettings.Current.LowBatteryPercent;
        foreach (var d in online)
        {
            var r = d.Reading!;
            if (threshold > 0 && !r.Charging && r.Percent <= threshold)
            {
                if (_lowNotified.Add(d.Id)) Notify("电量低", $"{d.Name} 只剩 {r.Percent}%", ToolTipIcon.Warning, () => ShowMain(MainPage.Home));
            }
            else _lowNotified.Remove(d.Id);
        }
    }

    // ---------- 面板 / 主页面 ----------

    private void ToggleFlyout()
    {
        // 点内嵌条时面板会先因失去焦点关闭，紧接着又收到点击：这种情况视为「关闭」，不再重开
        if (_flyout != null || (DateTime.Now - _flyoutClosedAt).TotalMilliseconds < 300) return;

        Views.WpfHost.EnsureApp();
        _flyout = new Views.FlyoutWindow(this);
        _flyout.Closed += (_, _) => { _flyout = null; _flyoutClosedAt = DateTime.Now; TrimMemorySoon(); };
        _flyout.ShowNear(Cursor.Position);
    }

    public void ShowMain(MainPage page)
    {
        if (_main == null)
        {
            Views.WpfHost.EnsureApp();
            _main = new Views.MainWindow(this, page);
            _main.Closed += (_, _) => { _main = null; TrimMemorySoon(); };
            Views.WpfHost.Show(_main);
        }
        else
        {
            _main.Page = page;
            if (_main.WindowState == System.Windows.WindowState.Minimized) _main.WindowState = System.Windows.WindowState.Normal;
            _main.Show();
        }
        _main.Activate();
        SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(_main).Handle);
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

    private void SetUpdate(UpdateState s) { _update = s; RaiseChanged(); }

    /// <summary>界面上的「检查更新」：演示模式下走模拟流程。</summary>
    public void CheckUpdate()
    {
        if (_demo) DemoCheckUpdate();
        else CheckUpdate(manual: true);
    }

    /// <summary>界面上的「立即更新」：演示模式下走模拟流程。</summary>
    public void InstallUpdate()
    {
        if (_demo) DemoInstallUpdate();
        else InstallRealUpdate();
    }

    private async void CheckUpdate(bool manual)
    {
        if (!manual && (!AppSettings.Current.AutoUpdate || !Updater.CanSelfUpdate)) return; // 开发版本不自动检查
        if (_update.Status is UpdateStatus.Checking or UpdateStatus.Downloading) return;
        SetUpdate(new UpdateState(UpdateStatus.Checking));
        try
        {
            var info = await Updater.CheckAsync();
            Log.W($"检查更新：当前 {Updater.CurrentText}，最新 {(info == null ? "无更新" : "v" + info.Version)}");
            if (info == null) { SetUpdate(new UpdateState(UpdateStatus.UpToDate)); return; }
            SetUpdate(new UpdateState(UpdateStatus.Available, info));
            if (manual) return;

            InstallRealUpdate(); // 自动更新：下载后替换并重启
        }
        catch (Exception e)
        {
            Log.W("检查更新失败: " + e.Message);
            SetUpdate(new UpdateState(UpdateStatus.Failed, Error: e.Message));
        }
    }

    private async void InstallRealUpdate()
    {
        // 下载失败后可以直接重试（横幅上的「重试」）
        if (_update is not { Status: UpdateStatus.Available or UpdateStatus.Failed, Info: { } info }) return;
        SetUpdate(new UpdateState(UpdateStatus.Downloading, info));
        try
        {
            var setup = await Updater.DownloadAsync(info, new Progress<int>(p => SetUpdate(new UpdateState(UpdateStatus.Downloading, info, p))));
            Log.W($"已下载 v{info.Version}，退出后由安装包替换文件并重启");
            Updater.ApplyAndRestart(setup, showMain: _main is { IsVisible: true });
            ExitThread();
        }
        catch (Exception e)
        {
            // 不弹通知：失败原因和「重试」显示在主页面的更新横幅里，后台自动更新下次检查时也会再试
            Log.W("更新失败: " + e);
            SetUpdate(new UpdateState(UpdateStatus.Failed, info, Error: e.Message));
        }
    }

    // ---------- 演示模式的更新（不联网、不替换程序） ----------

    /// <summary>演示用的「新版本」：当前版本的下一个小版本。</summary>
    static UpdateState DemoUpdateAvailable
    {
        get
        {
            var v = Version.TryParse(Updater.CurrentText, out var cur) ? cur : new Version(0, 1, 0);
            // 演示流程不会真的下载，下载地址留空
            return new UpdateState(UpdateStatus.Available, new AppUpdate(new Version(v.Major, v.Minor + 1, 0), "", 0));
        }
    }

    private void SetDemoUpdate(UpdateState s) { _demoUpdate = s; RaiseChanged(); }

    private async void DemoCheckUpdate()
    {
        if (_demoUpdate.Status is UpdateStatus.Checking or UpdateStatus.Downloading) return;
        SetDemoUpdate(new UpdateState(UpdateStatus.Checking));
        await Task.Delay(800);
        if (_demo) SetDemoUpdate(DemoUpdateAvailable);
    }

    /// <summary>
    /// 模拟下载：第一次下到一半「失败」，展示失败样式和「重试」；重试会走完进度（约 3 秒），
    /// 然后回到「发现新版本」（真实流程这时会退出，由更新程序显示进度画面、替换文件并重启）。
    /// </summary>
    private async void DemoInstallUpdate()
    {
        if (_demoUpdate is not { Status: UpdateStatus.Available or UpdateStatus.Failed, Info: { } info }) return;
        bool fail = _demoUpdate.Status == UpdateStatus.Available;
        for (int p = 0; p <= 100; p += 4)
        {
            if (!_demo) return; // 中途退出了演示模式
            if (fail && p >= 60) { SetDemoUpdate(new UpdateState(UpdateStatus.Failed, info, Error: "网络连接中断（演示）")); return; }
            SetDemoUpdate(new UpdateState(UpdateStatus.Downloading, info, p));
            await Task.Delay(120);
        }
        if (_demo) SetDemoUpdate(DemoUpdateAvailable);
    }

    // ---------- 通知 ----------

    /// <summary>
    /// 气泡提醒要挂在一个可见的托盘图标上；程序图标平时隐藏，提醒时临时显示，直到提醒关闭。
    /// 期间 RefreshWidget 不能把图标藏掉：图标一删，系统会把它的提醒立刻撤掉（之前提醒一闪就没的原因）。
    /// </summary>
    private void Notify(string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _notifying = true;
        _app.Visible = true;
        _app.ShowBalloonTip(5000, title, text, icon);
    }

    private void OnBalloonDone()
    {
        if (!_notifying) return;
        _notifying = false;
        RefreshWidget(); // 内嵌条正常显示的话，托盘图标恢复隐藏
    }

    static string Truncate(string s) => s.Length <= 127 ? s : s[..127]; // NotifyIcon.Text 上限

    protected override void ExitThreadCore()
    {
        _timer.Dispose();
        _updateTimer.Dispose();
        AppSettings.Changed -= OnSettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        // 关窗口要用到 WPF；万一加载失败（比如程序文件被删了），也不能卡住退出，否则任务栏还原不了
        try { CloseWindows(); } catch (Exception e) { Log.W("退出时关闭窗口失败: " + e.Message); }
        AppSettings.Flush(); // 关窗口时数字框失去焦点也会提交一次，放在关窗口之后
        _trimTimer?.Dispose();
        _widget.Detach();
        _widget.Dispose();
        _app.Visible = false; _app.Dispose();
        _showEvent.Dispose();
        base.ExitThreadCore();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void CloseWindows()
    {
        _flyout?.Close();
        _main?.Close();
    }

    // ---------- 内存 ----------

    private System.Windows.Forms.Timer? _trimTimer;

    /// <summary>
    /// 主页面和弹出面板都关闭 10 秒后：完整回收一次，并把暂时不用的内存还给系统。
    /// WPF 加载后会常驻（打开过窗口后任务管理器里能到 100 多 MB），平时后台只需要十几 MB；
    /// 还回去的页面再次打开窗口时按需换回。
    /// </summary>
    private void TrimMemorySoon()
    {
        if (_trimTimer == null) { _trimTimer = new System.Windows.Forms.Timer { Interval = 10_000 }; _trimTimer.Tick += OnTrim; }
        _trimTimer.Stop();
        _trimTimer.Start(); // 连续开关窗口时重新计时
    }

    private void OnTrim(object? s, EventArgs e)
    {
        _trimTimer!.Stop();
        if (_main != null || _flyout != null) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }

    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
}
