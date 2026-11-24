using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeviceBatteryTray.Setup;

/// <summary>关闭 / 启动主程序。</summary>
static class AppProcess
{
    static Process[] Running() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Product.Exe));

    /// <summary>等指定进程退出（更新时主程序启动安装器后自己退出）。</summary>
    public static Task WaitForExitAsync(int pid, TimeSpan timeout) => Task.Run(() =>
    {
        try { using var p = Process.GetProcessById(pid); p.WaitForExit((int)timeout.TotalMilliseconds); }
        catch (ArgumentException) { } // 已经退出
    });

    /// <summary>
    /// 让正在运行的主程序正常退出（它会自己还原任务栏）。等不到就强制结束，返回 true，
    /// 这时调用方要用 --restore-taskbar 还原任务栏。结束不了（例如以管理员身份运行）时抛出异常。
    /// </summary>
    public static async Task<bool> CloseAsync()
    {
        if (Running().Length == 0) return false;
        Log.W("通知主程序退出");
        // 主程序创建的事件允许所有用户打开（管理员身份运行时也能通知到）
        if (EventWaitHandle.TryOpenExisting(Product.ExitEventName, out var ev)) using (ev) ev.Set();
        if (await WaitAllExitedAsync(TimeSpan.FromSeconds(15))) return false;

        Log.W("主程序没有退出，强制结束");
        foreach (var p in Running())
        {
            try { p.Kill(); } catch (Exception e) { Log.W("结束失败: " + e.Message); }
            finally { p.Dispose(); }
        }
        if (await WaitAllExitedAsync(TimeSpan.FromSeconds(5))) return true;
        throw new Exception($"无法关闭{Product.Name}，请手动退出后重试。");
    }

    static async Task<bool> WaitAllExitedAsync(TimeSpan timeout)
    {
        var until = DateTime.Now + timeout;
        while (DateTime.Now < until)
        {
            var ps = Running();
            bool any = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            if (!any) return true;
            await Task.Delay(200);
        }
        return false;
    }

    public static void Start(string dir, string args = "")
    {
        Log.W($"启动主程序 {args}");
        Process.Start(new ProcessStartInfo(Path.Combine(dir, Product.Exe), args) { UseShellExecute = false, WorkingDirectory = dir });
    }

    /// <summary>运行主程序的一次性命令（如 --restore-taskbar）并等待结束。</summary>
    public static void RunAndWait(string dir, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, Product.Exe), args) { UseShellExecute = false, WorkingDirectory = dir });
            p?.WaitForExit(15000);
        }
        catch (Exception e) { Log.W($"运行 {args} 失败: {e.Message}"); }
    }
}
