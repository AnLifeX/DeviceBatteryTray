using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DeviceBatteryTray.Setup;

/// <summary>主程序需要 .NET 8 桌面运行时（x64）。没有的话从微软官网下载安装（全机安装，需要管理员确认一次）。</summary>
static class DotNetRuntime
{
    /// <summary>微软的固定链接，总是指向最新的 8.0.x。</summary>
    const string DownloadUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

    public static bool IsInstalled()
    {
        try
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (hk != null && hk.GetValueNames().Any(n => n.StartsWith("8.", StringComparison.Ordinal))) return true;
        }
        catch { }
        var pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dir = Path.Combine(pf, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        return Directory.Exists(dir) && Directory.GetDirectories(dir, "8.*").Length > 0;
    }

    /// <summary>下载安装程序到临时目录，progress 报告 0~1。</summary>
    public static async Task<string> DownloadAsync(IProgress<double> progress)
    {
        var file = Path.Combine(Path.GetTempPath(), "DeviceBatteryTray-windowsdesktop-runtime-8-x64.exe");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
        using (var src = await resp.Content.ReadAsStreamAsync())
        using (var dst = File.Create(file))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                await dst.WriteAsync(buf, 0, n);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }
        if (total > 0 && new FileInfo(file).Length != total) throw new IOException("下载不完整。");
        return file;
    }

    /// <summary>静默安装。运行时是全机安装，会弹 UAC；用户拒绝时抛出异常。</summary>
    public static async Task InstallAsync(string installer)
    {
        Process p;
        try
        {
            p = Process.Start(new ProcessStartInfo(installer, "/install /quiet /norestart") { UseShellExecute = true, Verb = "runas" })!;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED：UAC 点了「否」
        {
            throw new Exception("未获得管理员权限，无法安装 .NET 8 桌面运行时。");
        }
        using (p)
        {
            await Task.Run(() => p.WaitForExit());
            // 3010：成功，需要重启（不影响本程序运行）；1638：已装有更新的版本
            if (p.ExitCode is not (0 or 3010 or 1638))
                throw new Exception($".NET 8 桌面运行时安装失败（错误码 {p.ExitCode}）。");
        }
        try { File.Delete(installer); } catch { }
    }
}
