using DeviceBatteryTray.Devices;
using Microsoft.Win32;

namespace DeviceBatteryTray.Tray;

/// <summary>
/// 主逻辑：定时轮询各设备，把「在线」的设备显示在任务栏内嵌条上，点击内嵌条弹出面板。
/// 休眠 / 未连接 / 不支持的设备一律不显示。
/// 只有内嵌条嵌不进任务栏（如竖向任务栏）时，才显示一个程序托盘图标作为入口。
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    const int LowBattery = 20;
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "DeviceBatteryTray";

    private readonly List<BatteryProvider> _providers;
    private readonly Dictionary<string, BatteryReading> _last = new();
    private readonly HashSet<string> _lowNotified = new();
    private readonly NotifyIcon _app;
    private readonly TaskbarWidget _widget = new();
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _timer;
    private Flyout? _flyout;
    private DateTime _flyoutClosedAt;
    private int _polling;

    public TrayApp(List<BatteryProvider> providers)
    {
        _providers = providers;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _app = new NotifyIcon { Text = "设备电量", Icon = IconRenderer.RenderGlyph(Glyphs.Battery10.ToString()), Visible = false };
        _app.MouseUp += (_, _) => ToggleFlyout();
        _widget.Clicked += _ => ToggleFlyout();
        SystemEvents.UserPreferenceChanged += (_, _) => Refresh(); // 深浅色切换
        SystemEvents.SessionEnding += (_, _) => _widget.Detach();  // 注销/关机前还原任务栏
        Application.ApplicationExit += (_, _) => _widget.Detach();

        // DeviceBatteryTray.exe --exit 会触发这个事件，让本实例正常退出。
        // 管理员实例创建的事件要允许普通权限进程打开，所以给 Everyone 设置访问权限。
        var sec = new System.Security.AccessControl.EventWaitHandleSecurity();
        sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.EventWaitHandleRights.Synchronize | System.Security.AccessControl.EventWaitHandleRights.Modify,
            System.Security.AccessControl.AccessControlType.Allow));
        var exitEvent = System.Threading.EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, Program.ExitEventName, out _, sec);
        ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => _ui.Post(_ => ExitThread(), null), null, -1, true);

        _timer = new System.Threading.Timer(_ => PollAsync(), null, TimeSpan.Zero, PollInterval);
    }

    private void PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
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
                _ui.Post(_ => { foreach (var kv in results) _last[kv.Key] = kv.Value; Refresh(); }, null);
            }
            finally { Interlocked.Exchange(ref _polling, 0); }
        });
    }

    /// <summary>只有读到电量的设备才显示。</summary>
    private List<(BatteryProvider P, BatteryReading R)> Online() =>
        _providers.Where(p => _last.TryGetValue(p.Id, out var r) && r.State == ReadState.Ok)
                  .Select(p => (p, _last[p.Id])).ToList();

    private void Refresh()
    {
        var online = Online();
        bool widgetShown = _widget.Update(online.Select(v => new TaskbarWidget.Item(v.P.Kind, v.P.DisplayName, v.R)).ToList());

        // 内嵌条显示不了（竖向任务栏 / 没有在线设备）时，用程序托盘图标兜底，保证随时能打开面板
        _app.Visible = !widgetShown;
        _app.Text = Truncate(online.Count == 0 ? "设备电量：没有在线设备"
            : string.Join("\n", online.Select(v => $"{v.P.DisplayName} {v.R.Percent}%")));
        Log.W($"刷新：在线 {online.Count}，内嵌条显示={widgetShown}，管理员={FlydigiVader.IsElevated()}");

        // 低电量提醒：每次跌破阈值提醒一次，充电或回升后重置
        foreach (var (p, r) in online)
        {
            if (!r.Charging && r.Percent <= LowBattery)
            {
                if (_lowNotified.Add(p.Id)) Notify("电量低", $"{p.DisplayName} 只剩 {r.Percent}%");
            }
            else _lowNotified.Remove(p.Id);
        }
    }

    private void ToggleFlyout()
    {
        // 点内嵌条时面板会先因失去焦点关闭，紧接着又收到点击：这种情况视为「关闭」，不再重开
        if (_flyout != null || (DateTime.Now - _flyoutClosedAt).TotalMilliseconds < 300) return;

        var rows = Online().Select(v => new Flyout.Row(v.P.Kind, v.P.DisplayName, v.R)).ToList();
        _flyout = new Flyout(rows, AutoStart,
            refresh: PollAsync,
            setAutoStart: v => AutoStart = v,
            exit: ExitThread);
        _flyout.FormClosed += (_, _) => { _flyout?.Dispose(); _flyout = null; _flyoutClosedAt = DateTime.Now; };
        _flyout.ShowNear(Cursor.Position);
    }

    /// <summary>气泡提醒要挂在一个可见的托盘图标上；程序图标平时隐藏，提醒时临时显示。</summary>
    private void Notify(string title, string text)
    {
        bool wasHidden = !_app.Visible;
        _app.Visible = true;
        _app.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning);
        if (!wasHidden) return;
        void Hide(object? s, EventArgs e)
        {
            _app.BalloonTipClosed -= Hide; _app.BalloonTipClicked -= Hide;
            Refresh();
        }
        _app.BalloonTipClosed += Hide; _app.BalloonTipClicked += Hide;
    }

    static string Truncate(string s) => s.Length <= 127 ? s : s[..127]; // NotifyIcon.Text 上限

    /// <summary>
    /// 开机自启。管理员身份运行时用「计划任务 + 最高权限」，开机不弹 UAC；
    /// 普通权限时退回到 HKCU\Run（此时黑武士读不到）。
    /// </summary>
    static bool AutoStart
    {
        get
        {
            if (Schtasks("/query /tn " + RunName) == 0) return true;
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string v && v.Contains(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            k.DeleteValue(RunName, throwOnMissingValue: false);
            Schtasks($"/delete /tn {RunName} /f");
            if (!value) return;

            if (FlydigiVader.IsElevated()) CreateLogonTask();
            else k.SetValue(RunName, $"\"{Application.ExecutablePath}\"");
        }
    }

    /// <summary>
    /// 用 XML 建计划任务：当前用户登录时以最高权限启动。
    /// 必须关掉默认的「运行超过 72 小时自动结束」和「仅接通电源时运行」，托盘程序要常驻。
    /// </summary>
    static void CreateLogonTask()
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>任务栏显示无线外设电量</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{System.Security.SecurityElement.Escape(Application.ExecutablePath)}"</Command></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "DeviceBatteryTray.task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        Program.RunSchtasks($"/create /tn {Program.TaskName} /xml \"{file}\" /f", out int code);
        Log.W($"创建计划任务：退出码 {code}");
        File.Delete(file);
    }

    static int Schtasks(string args)
    {
        try { Program.RunSchtasks(args, out int code); return code; }
        catch { return -1; }
    }

    protected override void ExitThreadCore()
    {
        _timer.Dispose();
        _flyout?.Close();
        _widget.Detach();
        _widget.Dispose();
        _app.Visible = false; _app.Dispose();
        base.ExitThreadCore();
    }
}
