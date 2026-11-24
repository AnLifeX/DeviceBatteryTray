using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace DeviceBatteryTray.Setup;

/// <summary>
/// 程序的安装信息：名称、目录、卸载登记、开机自启。
/// 文件名、事件名、自启项等必须和主程序（src/DeviceBatteryTray）保持一致。
/// </summary>
static class Product
{
    public const string Name = "设备电量";
    public const string Id = "DeviceBatteryTray";
    public const string Exe = "DeviceBatteryTray.exe";
    public const string UninstallExe = "Uninstall.exe";
    /// <summary>程序文件清单（相对路径，每行一个），更新 / 卸载时只删清单里的文件；主程序靠它判断能否自动更新。</summary>
    public const string Manifest = "DeviceBatteryTray.files";
    public const string Publisher = "AnLifeX";
    public const string RepoUrl = "https://github.com/AnLifeX/DeviceBatteryTray";
    public const string ExitEventName = @"Local\DeviceBatteryTray.Exit";
    public const string TaskName = "DeviceBatteryTray";
    public const string BackgroundArg = "--background";

    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Id;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>安装包里程序的版本（与安装器版本一致），如 "0.1.0"。</summary>
    public static string Version { get; } =
        (typeof(Product).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Id);

    /// <summary>设置和日志（主程序的 AppPaths.DataDir）。</summary>
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Id);

    public static string SelfPath => Process.GetCurrentProcess().MainModule!.FileName;

    // ---------- 已安装的版本 ----------

    public sealed record Installation(string Dir, string Version);

    /// <summary>本安装器装过的版本（卸载登记里的位置和版本）；没装过返回 null。</summary>
    public static Installation? FindInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (k?.GetValue("InstallLocation") is not string dir || k.GetValue("UninstallString") is not string un) return null;
        if (!un.Contains(UninstallExe)) return null; // 旧的 Velopack 测试版，见 OldVelopackInstalled
        return new Installation(dir, k.GetValue("DisplayVersion") as string ?? "");
    }

    /// <summary>早期用 Velopack 打包的测试版（同一个卸载登记名）。需要先卸载它。</summary>
    public static bool OldVelopackInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return k?.GetValue("UninstallString") is string un && un.IndexOf("Update.exe", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>在「设置 → 应用」里登记（当前用户）。</summary>
    public static void Register(string dir, long sizeBytes)
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var exe = Path.Combine(dir, Exe);
        k.SetValue("DisplayName", Name);
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", Publisher);
        k.SetValue("DisplayIcon", $"{exe},0");
        k.SetValue("InstallLocation", dir);
        k.SetValue("UninstallString", $"\"{Path.Combine(dir, UninstallExe)}\"");
        k.SetValue("URLInfoAbout", RepoUrl);
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("EstimatedSize", (int)Math.Max(1, sizeBytes / 1024), RegistryValueKind.DWord);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public static void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

    // ---------- 开机自启（与主程序 AutoStart.cs 相同的两种方式） ----------

    /// <summary>
    /// 是否已开启开机自启：普通权限时是 HKCU\Run，管理员身份运行时主程序建的是「最高权限」计划任务。
    /// </summary>
    public static bool AutoStartEnabled => RunValue() != null || TaskExists();

    static string? RunValue()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Id) as string;
    }

    public static bool TaskExists() => Schtasks($"/query /tn {TaskName}") == 0;

    /// <summary>打开：写 HKCU\Run（不需要管理员）。已有计划任务就保留它。关闭：两种都删。</summary>
    public static void SetAutoStart(string dir, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
        {
            if (!TaskExists()) k.SetValue(Id, $"\"{Path.Combine(dir, Exe)}\" {BackgroundArg}");
            return;
        }
        k.DeleteValue(Id, throwOnMissingValue: false);
        RemoveTask();
    }

    /// <summary>
    /// 删除计划任务。任务是主程序以管理员身份建的，普通权限可能删不掉，这时以管理员身份再删一次（会弹 UAC）。
    /// </summary>
    public static void RemoveTask()
    {
        if (!TaskExists()) return;
        if (Schtasks($"/delete /tn {TaskName} /f") == 0) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/delete /tn {TaskName} /f")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(15000);
        }
        catch (Exception e) { Log.W("以管理员身份删除计划任务失败: " + e.Message); }
    }

    static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    /// <summary>目录里已有的程序文件清单（相对路径）。</summary>
    public static string[] ReadManifest(string dir)
    {
        var f = Path.Combine(dir, Manifest);
        return File.Exists(f)
            ? File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0 && !Path.IsPathRooted(l) && !l.Contains("..")).ToArray()
            : Array.Empty<string>();
    }
}
