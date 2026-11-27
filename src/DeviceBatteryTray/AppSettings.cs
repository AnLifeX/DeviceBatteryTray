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

    /// <summary>深浅色。没有这一项（第一次运行）或是旧版本的 "System" 时，按 Windows 当前的深浅色确定并保存。</summary>
    [JsonConverter(typeof(ThemeConverter))]
    public ThemeMode? Theme { get; set; }

    /// <summary>自动更新：启动后和之后每小时检查一次，发现新版本就下载安装包、替换程序并重启。</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>用户在主页面关掉「显示」的设备 ID（不在任务栏 / 弹出面板显示，也不提醒）。默认全部显示。</summary>
    public List<string> HiddenDevices { get; set; } = new();

    /// <summary>用户给设备起的名字（设备 ID → 名称），用来区分同型号的多台设备。</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = new();

    /// <summary>出现过的设备 ID（判断「换了 USB 口的老设备」还是「本来就在的另一台」用）。</summary>
    public List<string> SeenDevices { get; set; } = new();

    public static readonly int[] LowBatteryChoices = { 0, 10, 15, 20, 30 };
    static readonly string FilePath = Path.Combine(AppPaths.DataDir, "settings.json");
    // 有一个开发版本把设置放在了 %LOCALAPPDATA%\DeviceBatteryTray（后来曾是测试版的安装目录），读不到新位置时从这里迁移
    static readonly string OldFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceBatteryTray", "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
#if DEBUG
    /// <summary>--demo-ui（开发用）只在内存里改设置，不写文件，不影响正在使用的设置。</summary>
    static readonly bool ReadOnly = Environment.GetCommandLineArgs().Contains("--demo-ui");
#else
    static readonly bool ReadOnly = false;
#endif

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>设置变化（在调用 <see cref="Change"/> 的线程上触发，一般是 UI 线程）。</summary>
    public static event Action? Changed;

    /// <summary>设置已写入文件（在线程池线程上触发；ok = false 表示写入失败）。</summary>
    public static event Action<bool>? Saved;

    /// <summary>连续修改（如连点数字框的箭头）合并成一次写文件。</summary>
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    static readonly object SaveGate = new();
    static System.Threading.Timer? _saveTimer;
    static bool _dirty;

    /// <summary>修改设置：内存里立即生效并触发 <see cref="Changed"/>，文件防抖写入（退出前用 <see cref="Flush"/> 补写）。</summary>
    public static void Change(Action<AppSettings> edit)
    {
        edit(Current);
        Current.Normalize();
        lock (SaveGate)
        {
            _dirty = true;
            _saveTimer ??= new System.Threading.Timer(_ => Flush());
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke();
    }

    /// <summary>把还没写入的修改立即写入文件。</summary>
    public static void Flush()
    {
        string json;
        lock (SaveGate)
        {
            if (!_dirty) return;
            _dirty = false;
            _saveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            json = JsonSerializer.Serialize(Current, Json);
        }
        bool ok = ReadOnly || WriteText(json);
        Saved?.Invoke(ok);
    }

    static bool WriteText(string json)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            // 先写临时文件再替换：写到一半退出 / 断电不会留下损坏的设置文件（损坏会在下次启动时被默认值覆盖）
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) { Log.W("保存设置失败: " + e.Message); return false; }
    }

    static AppSettings Load()
    {
        foreach (var path in new[] { FilePath, OldFilePath })
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) is { } s)
                {
                    bool initTheme = s.Theme == null;
                    s.Normalize();
                    if (path == OldFilePath || initTheme) WriteFile(s);
                    if (path == OldFilePath) DeleteOld();
                    Log.W($"读取设置（{path}）：刷新间隔 {s.RefreshSeconds} 秒");
                    return s;
                }
            }
            catch (Exception e) { Log.W($"读取设置失败（{path}）: " + e.Message); }
        }
        var created = new AppSettings();
        created.Normalize();
        Log.W("没有可用的设置文件，使用默认设置");
        WriteFile(created);
        return created;
    }

    static void WriteFile(AppSettings s)
    {
        if (ReadOnly) return;
        WriteText(JsonSerializer.Serialize(s, Json));
    }

    static void DeleteOld()
    {
        if (ReadOnly) return;
        try
        {
            File.Delete(OldFilePath); // 只删文件：那个目录可能是安装目录
        }
        catch { }
    }

    void Normalize()
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 5, 3600);
        LowBatteryPercent = Math.Clamp(LowBatteryPercent, 0, 100);
        Theme ??= Ui.Theme.SystemDark() ? ThemeMode.Dark : ThemeMode.Light;
        HiddenDevices ??= new();
        DeviceNames ??= new();
        SeenDevices ??= new();
    }

    public static string RefreshText(int sec) => sec < 60 ? $"{sec} 秒" : $"{sec / 60} 分钟";
    public static string LowBatteryText(int p) => p == 0 ? "不提醒" : $"低于 {p}%";

    /// <summary>Light / Dark；其他值（旧版本的 "System"）读成 null，由 Normalize 按系统深浅色确定。</summary>
    sealed class ThemeConverter : JsonConverter<ThemeMode?>
    {
        public override ThemeMode? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Enum.TryParse<ThemeMode>(reader.GetString(), out var v) && Enum.IsDefined(v) ? v : null;

        public override void Write(Utf8JsonWriter writer, ThemeMode? value, JsonSerializerOptions options)
        {
            if (value is { } v) writer.WriteStringValue(v.ToString()); else writer.WriteNullValue();
        }
    }
}
