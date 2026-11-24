using System;
using System.IO;

namespace DeviceBatteryTray.Setup;

/// <summary>安装日志：%TEMP%\DeviceBatteryTray-Setup.log（排查安装 / 更新问题用）。</summary>
static class Log
{
    public static readonly string FilePath = Path.Combine(Path.GetTempPath(), "DeviceBatteryTray-Setup.log");

    public static void W(string s)
    {
        try { File.AppendAllText(FilePath, $"{DateTime.Now:MM-dd HH:mm:ss.fff} {s}\r\n"); } catch { }
    }
}
