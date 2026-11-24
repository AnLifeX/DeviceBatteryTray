namespace DeviceBatteryTray;

/// <summary>简单运行日志：%TEMP%\DeviceBatteryTray.log（超过 1MB 自动清空）。</summary>
public static class Log
{
    public static readonly string FilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeviceBatteryTray.log");
    static readonly object Gate = new();

    public static void W(string s)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1 << 20) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {s}\n");
            }
        }
        catch { }
    }
}
