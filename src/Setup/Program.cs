using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace DeviceBatteryTray.Setup;

/// <summary>
/// 一个 exe，三种模式：
///   （无参数）                              安装向导
///   Uninstall.exe / --uninstall [--dir D]   卸载向导（安装时把自己复制成安装目录里的 Uninstall.exe）
///   --update --dir D --wait-pid N [--background]
///                                            主程序自动更新：等它退出、替换文件、重新启动它
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += EmbeddedLibs.Resolve; // 必须最先注册，见 EmbeddedLibs
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)] // 保证 WPF-UI 的类型在注册 AssemblyResolve 之后才加载
    static int Run(string[] args)
    {
        Log.W($"Setup v{Product.Version} {string.Join(" ", args)}");
        string? dir = Arg(args, "--dir");
        if (dir != null) dir = Path.GetFullPath(dir.Trim().TrimEnd('\\', '/')); // 统一成标准路径（斜杠方向、末尾分隔符）
        bool update = args.Contains("--update") && dir != null;
        bool uninstall = !update && (args.Contains("--uninstall")
            || string.Equals(Path.GetFileName(Product.SelfPath), Product.UninstallExe, StringComparison.OrdinalIgnoreCase));

        if (uninstall)
        {
            dir ??= Path.GetDirectoryName(Product.SelfPath)!;
            // 卸载要删掉安装目录里的 Uninstall.exe 自己：先复制到临时目录，从那里运行
            if (!args.Contains("--from-temp"))
            {
                var temp = Path.Combine(Path.GetTempPath(), $"{Product.Id}-Uninstall-{Guid.NewGuid():N}.exe");
                File.Copy(Product.SelfPath, temp);
                Process.Start(new ProcessStartInfo(temp, $"--uninstall --dir \"{dir}\" --from-temp") { UseShellExecute = false });
                return 0;
            }
        }

        // 先建好 Application 和主题资源，窗口创建时要用到
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        bool dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() == Wpf.Ui.Appearance.SystemTheme.Dark;
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
        });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Setup;component/Styles.xaml") });
        DeviceBatteryTray.Views.Accent.Apply(dark);
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.W("界面异常: " + e.Exception);
            MessageBox.Show(e.Exception.Message, $"{Product.Name} 安装程序", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        Window window;
#if DEBUG
        // 开发用：--preview <页面> <out.png> 把某个页面渲染成图片后退出（不执行任何安装操作）
        if (args.Length >= 3 && args[0] == "--preview") return Preview.Run(app, args[1], args[2]);
#endif
        if (update)
        {
            int.TryParse(Arg(args, "--wait-pid"), out int pid);
            window = new UpdateWindow(dir!, pid, background: args.Contains(Product.BackgroundArg));
        }
        else if (uninstall) window = new WizardWindow(WizardMode.Uninstall, dir);
        else window = new WizardWindow(WizardMode.Install, null);
        int code = app.Run(window);

        if (args.Contains("--from-temp")) DeleteSelfLater();
        return code;
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>卸载程序的临时副本：退出后由 cmd 稍等片刻再删除自己。</summary>
    static void DeleteSelfLater()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{Product.SelfPath}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }
    }
}
