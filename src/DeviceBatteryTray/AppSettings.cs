using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceBatteryTray.Ui;

namespace DeviceBatteryTray;

/// <summary>用户设置：%APPDATA%\DeviceBatteryTray\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>电量刷新间隔（秒）。</summary>
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>低电量提醒阈值（%），0 = 不提醒。</summary>
    public int LowBatteryPercent { get; set; } = 20;

    /// <summary>读取飞智黑武士（需要管理员权限，只在检测到接收器时才请求）。</summary>
    public bool ReadFlydigi { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>自动更新：按间隔检查，发现新版本就下载新 exe、替换自己并重启。</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>检查更新的间隔（小时）。</summary>
    public int UpdateCheckHours { get; set; } = 6;

    /// <summary>用户在主页面关掉「显示」的设备 ID（不在任务栏 / 弹出面板显示，也不提醒）。默认全部显示。</summary>
    public List<string> HiddenDevices { get; set; } = new();

    /// <summary>用户给设备起的名字（设备 ID → 名称），用来区分同型号的多台设备。</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = new();

    /// <summary>出现过的设备 ID（判断「换了 USB 口的老设备」还是「本来就在的另一台」用）。</summary>
    public List<string> SeenDevices { get; set; } = new();

    public static readonly int[] RefreshChoices = { 10, 30, 60, 120, 300 };
    public static readonly int[] LowBatteryChoices = { 0, 10, 15, 20, 30 };
    public static readonly int[] UpdateCheckChoices = { 1, 6, 12, 24, 168 };

    static readonly string FilePath = Path.Combine(AppPaths.DataDir, "settings.json");
    // 有一个开发版本把设置放在了 %LOCALAPPDATA%\DeviceBatteryTray（后来曾是测试版的安装目录），读不到新位置时从这里迁移
    static readonly string OldFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBatteryTray", "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>设置变化（在调用 <see cref="Change"/> 的线程上触发，一般是 UI 线程）。</summary>
    public static event Action? Changed;

    public static void Change(Action<AppSettings> edit)
    {
        edit(Current);
        Current.Normalize();
        Save();
        Ui.Theme.Invalidate();
        Changed?.Invoke();
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception e) { Log.W("保存设置失败: " + e.Message); }
    }

    static AppSettings Load()
    {
        foreach (var path in new[] { FilePath, OldFilePath })
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) is { } s)
                {
                    s.Normalize();
                    if (path == OldFilePath) MigrateFrom(s);
                    return s;
                }
            }
            catch (Exception e) { Log.W($"读取设置失败（{path}）: " + e.Message); }
        }
        return new AppSettings();
    }

    static void MigrateFrom(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json));
            File.Delete(OldFilePath); // 只删文件：那个目录可能是安装目录
        }
        catch { }
    }

    void Normalize()
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 5, 3600);
        LowBatteryPercent = Math.Clamp(LowBatteryPercent, 0, 100);
        UpdateCheckHours = Math.Clamp(UpdateCheckHours, 1, 24 * 30);
        HiddenDevices ??= new();
        DeviceNames ??= new();
        SeenDevices ??= new();
    }

    public static string RefreshText(int sec) => sec < 60 ? $"{sec} 秒" : $"{sec / 60} 分钟";
    public static string LowBatteryText(int p) => p == 0 ? "不提醒" : $"低于 {p}%";
    public static string UpdateCheckText(int h) => h switch { < 24 => $"每 {h} 小时", 24 => "每天", 168 => "每周", _ => $"每 {h / 24} 天" };
}
