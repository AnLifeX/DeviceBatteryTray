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
    ///   --wait-pid N     （自动更新内部用）等旧进程退出后再启动
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // Velopack 必须最先运行：安装 / 更新 / 卸载时会用特殊参数启动本程序执行钩子，然后直接退出
        // Velopack 在覆盖安装 / 卸载前会强制结束正在运行的本程序，来不及还原任务栏，所以在钩子里补上
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => TaskbarWidget.RestoreTaskbarLayout())
            .OnBeforeUninstallFastCallback(_ => OnUninstall())
            .Run();

        if (args.Length == 2 && args[0] == "--dump") { Dump(args[1]); return; }

        // 开发用：--demo-ui [home|settings] [light|dark] [out.png] 用演示数据打开主页面（不读设备、不挂任务栏）
        if (args.Length >= 1 && args[0] == "--demo-ui")
        {
            ApplicationConfiguration.Initialize();
            if (args.Length > 2) { AppSettings.Current.Theme = args[2] == "dark" ? ThemeMode.Dark : ThemeMode.Light; Theme.Invalidate(); }
            Views.WpfHost.EnsureApp();
            var w = new Views.MainWindow(new DemoModel(), args.Length > 1 && args[1] == "settings" ? MainPage.Settings : MainPage.Home);
            w.Closed += (_, _) => Application.Exit();
            Views.WpfHost.Show(w);
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

        Application.Run(new TrayApp(CreateProviders(), showMain: !background, justUpdated: args.Contains("--updated")));
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

    static List<BatteryProvider> CreateProviders() => new()
        {
            new VgnKeyboard(),
            new ThunderobotMouse(),
            new GameSirDongle(),
            new FlydigiVader(),
            // 漫步者 HECATE G2（35BB:A209）协议未知，暂不加入，见 docs/protocols/edifier-hecate-g2.md
        };

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

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

        ApplicationConfiguration.Initialize();
        var demo = new DemoModel();
        float s = 1;
        var bg = Color.FromArgb(70, 70, 70);

        // 预览 1：任务栏内嵌条 + 深色/浅色面板（含新版本提示）+ 空面板
        using var strip = TaskbarWidget.Render(demo.Devices.Where(d => d.Online)
            .Select(d => new TaskbarWidget.Item(d.Kind, d.Name, d.Reading!)).ToList(), 40, Color.White);
        using var fDark = Flyout.Render(demo, Theme.Of(true), s, "dev:mouse", null, 0, new());
        using var fLight = Flyout.Render(demo, Theme.Of(false), s, "settings", null, 0, new());
        using var fEmpty = Flyout.Render(new DemoModel { Empty = true }, Theme.Of(true), s, null, null, 0, new());
        int pw = fDark.Width + fLight.Width + fEmpty.Width + 64, ph = Math.Max(fDark.Height, fLight.Height) + strip.Height * 2 + 48;
        using (var sheet = new Bitmap(pw, ph))
        {
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(bg);
                using (var tb = new SolidBrush(Color.FromArgb(32, 32, 32))) g.FillRectangle(tb, 0, 8, sheet.Width, strip.Height * 2);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(strip, 16, 8, strip.Width * 2, strip.Height * 2);
                int y = strip.Height * 2 + 24;
                g.DrawImage(fDark, 16, y);
                g.DrawImage(fLight, 32 + fDark.Width, y);
                g.DrawImage(fEmpty, 48 + fDark.Width + fLight.Width, y);
            }
            sheet.Save(Path.ChangeExtension(file, ".png"));
        }

    }

    /// <summary>--dump 预览用的演示数据。</summary>
    sealed class DemoModel : IAppModel
    {
        public bool Empty { get; init; }
        public IReadOnlyList<DeviceStatus> Devices => Empty
            ? new List<DeviceStatus> { new("vgn", DeviceKind.Keyboard, "VGN V98Pro V2 键盘", BatteryReading.Offline) }
            : new List<DeviceStatus>
            {
                new("vgn", DeviceKind.Keyboard, "VGN V98Pro V2 键盘", new(ReadState.Ok, 73)),
                new("mouse", DeviceKind.Mouse, "雷神 ML903 mini 鼠标", new(ReadState.Ok, 18)),
                new("gamesir", DeviceKind.Gamepad, "盖世小鸡手柄", new(ReadState.Ok, 35, true)),
                new("flydigi", DeviceKind.Gamepad, "飞智 黑武士 3 Pro", new(ReadState.Ok, 100, Detail: "估算")),
                new("edifier", DeviceKind.Headset, "漫步者 HECATE G2", BatteryReading.Offline),
                new("other", DeviceKind.Mouse, "未插入的接收器（不显示）", BatteryReading.NotFound),
                new("flydigi-admin", DeviceKind.Gamepad, "飞智 黑武士 3 Pro（普通权限时）", new(ReadState.Error, Detail: FlydigiVader.NeedAdminDetail)),
            };
        public DateTime? UpdatedAt => new DateTime(2026, 11, 24, 14, 32, 5);
        public bool Refreshing => false;
        public UpdateState Update => Empty ? UpdateState.Idle
            : new(UpdateStatus.Available, new AppUpdate(new Version(0, 2, 0), null!));
        public bool AutoStart { get => true; set { } }
        public bool Elevated => true;
        public event Action? Changed { add { } remove { } }
        public void Refresh() { }
        public void CheckUpdate() { }
        public void InstallUpdate() { }
        public void ShowMain(MainPage page) { }
        public void RestartElevated() { }
        public void Exit() { }
    }
}
