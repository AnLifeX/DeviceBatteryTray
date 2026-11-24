using System.Drawing;
using System.Runtime.InteropServices;
using DeviceBatteryTray.Devices;
using DeviceBatteryTray.Tray;
using DeviceBatteryTray.Ui;
using Velopack;

namespace DeviceBatteryTray;

public static class Program
{
    public const string ExitEventName = @"Local\DeviceBatteryTray.Exit";
    public const string ShowEventName = @"Local\DeviceBatteryTray.Show";
    const string MutexName = @"Local\DeviceBatteryTray";
    public const string TaskName = "DeviceBatteryTray";

    /// <summary>
    /// 启动参数：
    ///   （无）            双击启动：常驻任务栏并打开主页面；已在运行则让它打开主页面
    ///   --background     开机自启用：只常驻任务栏，不打开主页面
    ///   --exit           让正在运行的实例退出
    ///   --restore-taskbar 还原任务栏（程序被强制结束后任务按钮区仍是缩窄的）
    ///   --dump out.txt   读一遍所有设备写出收发记录，并输出界面预览图
    ///   --no-elevate     不请求管理员权限
    ///   --demo           演示模式：用模拟设备显示任务栏电量条、弹出面板和主页面
    ///   --wait-pid N     （自动更新内部用）等旧进程退出后再启动
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // Velopack 必须最先运行：安装 / 更新 / 卸载时会用特殊参数启动本程序执行钩子，然后直接退出
        // Velopack 在覆盖安装 / 卸载前会强制结束正在运行的本程序，来不及还原任务栏，所以在钩子里补上
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => { TaskbarWidget.RestoreTaskbarLayout(); RefreshShellIcons(); })
            .OnAfterUpdateFastCallback(_ => RefreshShellIcons())
            .OnBeforeUninstallFastCallback(_ => OnUninstall())
            .Run();

        if (args.Length == 2 && args[0] == "--dump") { Dump(args[1]); return; }

        // 开发用：--demo-ui [home|settings|flyout] [light|dark] [out.png] 用演示数据打开主页面（不读设备、不挂任务栏）
        if (args.Length >= 1 && args[0] == "--demo-ui")
        {
            ApplicationConfiguration.Initialize();
            if (args.Length > 2) { AppSettings.Current.Theme = args[2] == "dark" ? ThemeMode.Dark : ThemeMode.Light; Theme.Invalidate(); }
            Views.WpfHost.EnsureApp();
            System.Windows.Window w;
            if (args.Length > 1 && args[1] == "flyout")
            {
                var f = new Views.FlyoutWindow(new DemoModel());
                var wa = Screen.PrimaryScreen!.WorkingArea;
                f.ShowNear(new Point(wa.Right - 300, wa.Bottom));
                w = f;
            }
            else
            {
                w = new Views.MainWindow(new DemoModel(), args.Length > 1 && args[1] == "settings" ? MainPage.Settings : MainPage.Home);
                if (args.Length > 1 && args[1] == "about") Views.MainWindow.ViewModel!.AboutOpen = true;
                Views.WpfHost.Show(w);
            }
            w.Closed += (_, _) => Application.Exit();
            if (args.Length > 3) // 第 4 个参数：渲染成图片后退出（锁屏 / 远程时也能出图）
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                t.Tick += (_, _) => { t.Stop(); Views.WpfHost.SaveSnapshot(w, args[3]); w.Close(); };
                t.Start();
            }
            Application.Run();
            return;
        }

        if (args.Contains("--restore-taskbar")) { TaskbarWidget.RestoreTaskbarLayout(); return; } // 程序异常退出后手动还原

        if (args.Contains("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var ev)) using (ev) ev.Set();
            return;
        }

        int wi = Array.IndexOf(args, "--wait-pid");
        if (wi >= 0 && wi + 1 < args.Length && int.TryParse(args[wi + 1], out int pid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(pid); old.WaitForExit(15000); }
            catch (ArgumentException) { } // 已经退出
        }

        bool background = args.Contains(AutoStart.BackgroundArg);

        // 已在运行：让它打开主页面（开机自启重复触发时什么都不做）
        if (SignalRunning(showMain: !background)) return;

        // 飞智黑武士要经内核过滤驱动通讯，需要管理员权限。只有「设置里开着 + 装了飞智空间站 + 插着接收器」
        // 时才请求提权，其他情况直接以普通权限运行、不弹 UAC。
        // 用户在 UAC 里点了「否」就照常以普通权限运行，黑武士显示「需要管理员权限」。
        // 已开启开机自启时，存在一个「最高权限」计划任务，通过它启动自己就不会弹 UAC。
        if (!FlydigiVader.IsElevated() && !args.Contains("--no-elevate") && FlydigiVader.NeedsElevation)
        {
            if (TryRunScheduledTask())
            {
                // 计划任务固定带 --background 启动；双击启动的话等它起来后通知它打开主页面
                if (!background) SignalWhenReady(TimeSpan.FromSeconds(15));
                return;
            }
            if (TryRelaunchElevated(args)) return;
        }

        using var single = new Mutex(true, MutexName, out bool first);
        if (!first) return; // 只允许一个实例

        ApplicationConfiguration.Initialize();
        Log.W($"启动 v{Updater.CurrentText}，管理员={FlydigiVader.IsElevated()}，参数={string.Join(" ", args)}");
        Application.ThreadException += (_, e) => Log.W("UI 线程异常: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.W("未处理异常: " + e.ExceptionObject);

        Application.Run(new TrayApp(CreateProviders(), showMain: !background, demo: args.Contains("--demo")));
    }

    /// <summary>卸载前（此时程序已被 Velopack 结束）：还原任务栏，删除开机自启项和设置 / 日志。</summary>
    static void OnUninstall()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var ev)) using (ev) ev.Set(); // 万一还在运行
            for (int i = 0; i < 30 && SignalRunning(showMain: false); i++) Thread.Sleep(100);
            TaskbarWidget.RestoreTaskbarLayout();
            AutoStart.Enabled = false;
            Directory.Delete(AppPaths.DataDir, recursive: true);
        }
        catch { }
    }

    /// <summary>已有实例在运行时返回 true；需要的话通知它打开主页面。</summary>
    static bool SignalRunning(bool showMain)
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
        {
            using (ev)
            {
                if (showMain)
                {
                    AllowSetForegroundWindow(-1); // 允许那个实例把主页面切到前台
                    ev.Set();
                }
            }
            return true;
        }
        // 旧版本实例没有 Show 事件，只能靠互斥体判断
        try { if (Mutex.TryOpenExisting(MutexName, out var m)) { m.Dispose(); return true; } }
        catch (UnauthorizedAccessException) { return true; } // 管理员实例创建的互斥体，存在但打不开
        return false;
    }

    static void SignalWhenReady(TimeSpan timeout)
    {
        var until = DateTime.Now + timeout;
        while (DateTime.Now < until)
        {
            if (SignalRunning(showMain: true)) return;
            Thread.Sleep(200);
        }
    }

    /// <summary>计划任务存在且指向本 exe 时，用 schtasks /run 以最高权限启动（无 UAC）。</summary>
    static bool TryRunScheduledTask()
    {
        try
        {
            var xml = RunSchtasks($"/query /tn {TaskName} /xml", out int code);
            if (code != 0) return false;
            var cmd = System.Xml.Linq.XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim('"');
            if (cmd == null || !string.Equals(Path.GetFullPath(cmd), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return false; // 任务指向别的 exe（比如换了位置），走 UAC
            RunSchtasks($"/run /tn {TaskName}", out code);
            return code == 0;
        }
        catch { return false; }
    }

    public static string RunSchtasks(string args, out int exitCode)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        exitCode = p.ExitCode;
        return output;
    }

    static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // 用户取消了 UAC
    }

    static List<BatteryProvider> CreateProviders() => Providers.Create();

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    /// <summary>
    /// 通知资源管理器刷新图标：快捷方式指向的路径（current\DeviceBatteryTray.exe）不变，
    /// 换了图标后桌面 / 开始菜单仍会显示缓存里的旧图标。
    /// </summary>
    static void RefreshShellIcons()
    {
        try { SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    // ---------- --dump ----------

    /// <summary>调试：读一遍所有设备写到文件，并用演示数据输出界面预览图（WinExe 没有控制台）。</summary>
    static void Dump(string file)
    {
        var trace = new List<string>();
        Hid.HidConnection.Debug = s => { lock (trace) trace.Add("    " + s); };
        var lines = CreateProviders().Select(p =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BatteryReading r;
            try { r = p.Read(); } catch (Exception e) { r = BatteryReading.Fail(e.Message); }
            string t; lock (trace) { t = string.Join("\n", trace); trace.Clear(); }
            return $"{p.DisplayName}\t{r.State}\t{r.Percent}%\tcharging={r.Charging}\t{r.Detail}\t{sw.ElapsedMilliseconds}ms\n{t}";
        }).ToList();
        File.WriteAllLines(file, lines);

        // 任务栏内嵌条预览（演示数据）；面板和主页面的预览用 --demo-ui
        using var strip = TaskbarWidget.Render(DeviceList.Build(DemoData.Raw).Where(d => d.Shown)
            .Select(d => new TaskbarWidget.Item(d.Kind, d.Name, d.Reading!)).ToList(), 40, Color.White);
        using var sheet = new Bitmap(strip.Width * 2 + 32, strip.Height * 2 + 16);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImage(strip, 16, 8, strip.Width * 2, strip.Height * 2);
        }
        sheet.Save(Path.ChangeExtension(file, ".png"));
    }

    /// <summary>--dump 预览用的演示数据。</summary>
    sealed class DemoModel : IAppModel
    {
        public IReadOnlyList<DeviceStatus> Devices => DeviceList.Build(DemoData.Raw)
            .Append(new DeviceStatus("demo-admin", DeviceKind.Gamepad, "飞智 黑武士 3 Pro（普通权限时）",
                new BatteryReading(ReadState.Error, Detail: FlydigiVader.NeedAdminDetail))).ToList();
        public DateTime? UpdatedAt => new DateTime(2026, 11, 24, 14, 32, 5);
        public bool Refreshing => false;
        public UpdateState Update => new(UpdateStatus.Available, new AppUpdate(new Version(0, 2, 0), null!));
        public bool AutoStart { get => true; set { } }
        public bool Elevated => true;
        public bool Demo { get => true; set { } }
        public event Action? Changed { add { } remove { } }
        public void Refresh() { }
        public void CheckUpdate() { }
        public void InstallUpdate() { }
        public void ShowMain(MainPage page) { }
        public void RestartElevated() { }
        public void Exit() { }
    }
}
