using DeviceBatteryTray.Devices;
using DeviceBatteryTray.Tray;

namespace DeviceBatteryTray;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--dump")
        {
            // 调试：读一遍所有设备，写到文件后退出（WinExe 没有控制台）
            var trace = new List<string>();
            DeviceBatteryTray.Hid.HidConnection.Debug = s => { lock (trace) trace.Add("    " + s); };
            var lines = CreateProviders().Select(p =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BatteryReading r;
                try { r = p.Read(); } catch (Exception e) { r = BatteryReading.Fail(e.Message); }
                string t; lock (trace) { t = string.Join("\n", trace); trace.Clear(); }
                return $"{p.DisplayName}\t{r.State}\t{r.Percent}%\tcharging={r.Charging}\t{r.Detail}\t{sw.ElapsedMilliseconds}ms\n{t}";
            }).ToList();
            File.WriteAllLines(args[1], lines);

            // 同时输出内嵌条和弹出面板的预览图
            var widgetItems = new List<TaskbarWidget.Item>
            {
                new(DeviceKind.Keyboard, "VGN V98Pro V2 键盘", new(ReadState.Ok, 73)),
                new(DeviceKind.Mouse, "雷神 ML903 mini 鼠标", new(ReadState.Ok, 94)),
                new(DeviceKind.Gamepad, "盖世小鸡手柄", new(ReadState.Ok, 35, true)),
                new(DeviceKind.Gamepad, "飞智 黑武士 3 Pro", new(ReadState.Ok, 100, Detail: "估算")),
            };
            using var strip = TaskbarWidget.Render(widgetItems, 40, System.Drawing.Color.White);
            ApplicationConfiguration.Initialize();
            using var fly = new Flyout(widgetItems.Select(i => new Flyout.Row(i.Kind, i.Name, i.Reading)).ToList(), true, () => { }, _ => { }, () => { });
            using var flyBmp = new System.Drawing.Bitmap(fly.Width, fly.Height);
            fly.DrawToBitmap(flyBmp, new System.Drawing.Rectangle(0, 0, fly.Width, fly.Height));
            using var sheet = new System.Drawing.Bitmap(Math.Max(strip.Width * 2, fly.Width) + 32, strip.Height * 2 + fly.Height + 40);
            using (var g = System.Drawing.Graphics.FromImage(sheet))
            {
                g.Clear(System.Drawing.Color.FromArgb(60, 60, 60));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                using (var tb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(32, 32, 32)))
                    g.FillRectangle(tb, 0, 8, sheet.Width, strip.Height * 2); // 模拟深色任务栏
                g.DrawImage(strip, 16, 8, strip.Width * 2, strip.Height * 2);
                g.DrawImage(flyBmp, 16, strip.Height * 2 + 24);
            }
            sheet.Save(Path.ChangeExtension(args[1], ".png"));
            return;
        }

        if (args.Contains("--exit"))
        {
            // 通知正在运行的实例正常退出（会还原任务栏）
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var ev)) using (ev) ev.Set();
            return;
        }

        // 飞智黑武士要经内核过滤驱动通讯，需要管理员权限：非管理员启动时请求一次提权。
        // 用户在 UAC 里点了「否」就照常以普通权限运行，黑武士显示「需要以管理员身份运行」。
        // 已开启开机自启时，存在一个「最高权限」计划任务，通过它启动自己就不会弹 UAC。
        if (!FlydigiVader.IsElevated() && !args.Contains("--no-elevate"))
        {
            if (Mutex.TryOpenExisting(@"Local\DeviceBatteryTray", out var running)) { running.Dispose(); return; } // 已在运行
            if (TryRunScheduledTask() || TryRelaunchElevated()) return;
        }

        using var single = new Mutex(true, @"Local\DeviceBatteryTray", out bool first);
        if (!first) return; // 只允许一个实例

        ApplicationConfiguration.Initialize();
        Log.W($"启动，管理员={FlydigiVader.IsElevated()}");
        Application.ThreadException += (_, e) => Log.W("UI 线程异常: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.W("未处理异常: " + e.ExceptionObject);

        Application.Run(new TrayApp(CreateProviders()));
    }

    public const string ExitEventName = @"Local\DeviceBatteryTray.Exit";

    public const string TaskName = "DeviceBatteryTray";

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

    static bool TryRelaunchElevated()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
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
}
