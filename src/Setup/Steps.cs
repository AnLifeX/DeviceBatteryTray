using System;
using System.IO;
using System.Threading.Tasks;

namespace DeviceBatteryTray.Setup;

/// <summary>进度：一句说明 + 进度（0~1，null 表示不确定）。</summary>
sealed record Status(string Text, double? Fraction = null);

sealed class InstallOptions
{
    public string Dir = Product.DefaultDir;
    public bool DesktopShortcut = true;
    public bool AutoStart = true;
}

/// <summary>安装 / 卸载 / 更新的具体步骤（界面只负责显示进度）。</summary>
static class Steps
{
    public static async Task InstallAsync(InstallOptions o, IProgress<Status> p)
    {
        Log.W($"安装 v{Product.Version} 到 {o.Dir}，桌面快捷方式={o.DesktopShortcut}，开机自启={o.AutoStart}");
        await EnsureRuntimeAsync(p);

        p.Report(new Status($"正在关闭{Product.Name}…"));
        bool forced = await AppProcess.CloseAsync();

        await CopyFilesAsync(o.Dir, p);

        p.Report(new Status("正在创建快捷方式…"));
        File.Copy(Product.SelfPath, Path.Combine(o.Dir, Product.UninstallExe), overwrite: true);
        Product.Register(o.Dir, Payload.Size);
        Shortcuts.Create(o.Dir, o.DesktopShortcut);
        Product.SetAutoStart(o.Dir, o.AutoStart);
        if (forced) AppProcess.RunAndWait(o.Dir, "--restore-taskbar");
        Log.W("安装完成");
    }

    public static async Task UninstallAsync(string dir, bool keepData, IProgress<Status> p)
    {
        Log.W($"卸载 {dir}，保留设置={keepData}");
        p.Report(new Status($"正在关闭{Product.Name}…"));
        bool forced = await AppProcess.CloseAsync();
        if (forced && File.Exists(Path.Combine(dir, Product.Exe))) AppProcess.RunAndWait(dir, "--restore-taskbar");

        p.Report(new Status("正在移除开机自启…"));
        await Task.Run(() => Product.SetAutoStart(dir, on: false));

        p.Report(new Status("正在删除文件…"));
        await Task.Run(() =>
        {
            Shortcuts.Remove();
            Payload.Remove(dir);
            Product.Unregister();
            if (!keepData)
                try { if (Directory.Exists(Product.DataDir)) Directory.Delete(Product.DataDir, recursive: true); }
                catch (Exception e) { Log.W("删除设置失败: " + e.Message); }
        });
        Log.W("卸载完成");
    }

    /// <summary>主程序发现新版本后调用：等它退出，替换文件，更新卸载登记。</summary>
    public static async Task UpdateAsync(string dir, int waitPid, IProgress<Status> p)
    {
        Log.W($"更新 {dir} 到 v{Product.Version}");
        p.Report(new Status($"正在等待{Product.Name}退出…"));
        if (waitPid > 0) await AppProcess.WaitForExitAsync(waitPid, TimeSpan.FromSeconds(60));
        bool forced = await AppProcess.CloseAsync();

        await EnsureRuntimeAsync(p);
        await CopyFilesAsync(dir, p);

        // 安装版：更新卸载程序和登记的版本号；便携版没有登记，不用管
        if (Product.FindInstalled() is { } inst && string.Equals(Path.GetFullPath(inst.Dir).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(Product.SelfPath, Path.Combine(dir, Product.UninstallExe), overwrite: true);
            Product.Register(dir, Payload.Size);
        }
        if (forced) AppProcess.RunAndWait(dir, "--restore-taskbar");
        Log.W("更新完成");
    }

    static async Task EnsureRuntimeAsync(IProgress<Status> p)
    {
        if (DotNetRuntime.IsInstalled()) return;
        p.Report(new Status("正在下载 .NET 8 桌面运行时…", 0));
        var file = await DotNetRuntime.DownloadAsync(new Progress<double>(f => p.Report(new Status($"正在下载 .NET 8 桌面运行时… {f:P0}", f))));
        p.Report(new Status("正在安装 .NET 8 桌面运行时…"));
        await DotNetRuntime.InstallAsync(file);
    }

    static Task CopyFilesAsync(string dir, IProgress<Status> p)
    {
        p.Report(new Status("正在复制文件…", 0));
        return Task.Run(() => Payload.Install(dir, f => p.Report(new Status("正在复制文件…", f))));
    }
}
