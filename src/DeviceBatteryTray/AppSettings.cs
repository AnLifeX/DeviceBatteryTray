using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceBatteryTray.Ui;

namespace DeviceBatteryTray;

public enum UpdateMode { Auto, Notify, Off }

/// <summary>用户设置：%APPDATA%\DeviceBatteryTray\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>电量刷新间隔（秒）。</summary>
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>低电量提醒阈值（%），0 = 不提醒。</summary>
    public int LowBatteryPercent { get; set; } = 20;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UpdateMode Update { get; set; } = UpdateMode.Auto;

    public static readonly int[] RefreshChoices = { 10, 30, 60, 120, 300 };
    public static readonly int[] LowBatteryChoices = { 0, 10, 15, 20, 30 };

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeviceBatteryTray");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>设置变化（在调用 <see cref="Change"/> 的线程上触发，一般是 UI 线程）。</summary>
    public static event Action? Changed;

    public static void Change(Action<AppSettings> edit)
    {
        edit(Current);
        Current.Normalize();
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception e) { Log.W("保存设置失败: " + e.Message); }
        Ui.Theme.Invalidate();
        Changed?.Invoke();
    }

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) is { } s)
            {
                s.Normalize();
                return s;
            }
        }
        catch (Exception e) { Log.W("读取设置失败: " + e.Message); }
        return new AppSettings();
    }

    void Normalize()
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 5, 3600);
        LowBatteryPercent = Math.Clamp(LowBatteryPercent, 0, 100);
    }

    public static string RefreshText(int sec) => sec < 60 ? $"{sec} 秒" : $"{sec / 60} 分钟";
    public static string LowBatteryText(int p) => p == 0 ? "不提醒" : $"低于 {p}%";
    public static string ThemeText(ThemeMode m) => m switch { ThemeMode.Light => "浅色", ThemeMode.Dark => "深色", _ => "跟随系统" };
    public static string UpdateText(UpdateMode m) => m switch { UpdateMode.Notify => "仅提醒", UpdateMode.Off => "关闭", _ => "自动下载并安装" };
}
