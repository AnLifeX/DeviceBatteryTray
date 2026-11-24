using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DeviceBatteryTray.Setup;

/// <summary>开始菜单（「设备电量」文件夹：程序 + 卸载）和桌面快捷方式。</summary>
static class Shortcuts
{
    static string StartMenuDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product.Name);

    static string StartMenuApp => Path.Combine(StartMenuDir, Product.Name + ".lnk");
    static string StartMenuUninstall => Path.Combine(StartMenuDir, $"卸载 {Product.Name}.lnk");

    static string Desktop =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Product.Name + ".lnk");

    public static bool DesktopExists => File.Exists(Desktop);

    public static void Create(string dir, bool desktop)
    {
        var exe = Path.Combine(dir, Product.Exe);
        Directory.CreateDirectory(StartMenuDir);
        Save(StartMenuApp, exe, "在任务栏上显示无线设备的电量");
        Save(StartMenuUninstall, Path.Combine(dir, Product.UninstallExe), $"从电脑上移除{Product.Name}", icon: exe);
        if (desktop) Save(Desktop, exe, "在任务栏上显示无线设备的电量");
        else Delete(Desktop);
        RefreshShell();
    }

    public static void Remove()
    {
        Delete(Desktop);
        Delete(StartMenuApp);
        Delete(StartMenuUninstall);
        try { if (Directory.Exists(StartMenuDir) && Directory.GetFileSystemEntries(StartMenuDir).Length == 0) Directory.Delete(StartMenuDir); } catch { }
        RefreshShell();
    }

    static void Save(string lnk, string target, string description, string? icon = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("无法创建快捷方式（WScript.Shell 不可用）");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic s = shell.CreateShortcut(lnk);
            try
            {
                s.TargetPath = target;
                s.WorkingDirectory = Path.GetDirectoryName(target);
                s.Description = description;
                s.IconLocation = (icon ?? target) + ",0";
                s.Save();
            }
            finally { Marshal.FinalReleaseComObject(s); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    static void Delete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (Exception e) { Log.W($"删除快捷方式失败 {file}: {e.Message}"); }
    }

    /// <summary>通知资源管理器刷新图标（覆盖安装后桌面 / 开始菜单可能还显示旧图标）。</summary>
    static void RefreshShell()
    {
        try { SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
