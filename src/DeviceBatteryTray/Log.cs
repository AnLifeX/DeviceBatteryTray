namespace DeviceBatteryTray;

/// <summary>程序数据目录：%LOCALAPPDATA%\DeviceBatteryTray（设置、日志都放这里，不放临时目录）。</summary>
public static class AppPaths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBatteryTray");
}

/// <summary>运行日志：%LOCALAPPDATA%\DeviceBatteryTray\DeviceBatteryTray.log（超过 1MB 时转存为 .log.1）。</summary>
public static class Log
{
    public static readonly string FilePath = Path.Combine(AppPaths.DataDir, "DeviceBatteryTray.log");
    static readonly object Gate = new();

    public static void W(string s)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1 << 20) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {s}\n");
            }
        }
        catch { }
    }
}
