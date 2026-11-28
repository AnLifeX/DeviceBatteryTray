using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace DeviceBatteryTray;

/// <summary>一个可用的新版本：版本号和安装包下载地址。</summary>
public sealed record AppUpdate(Version Version, string SetupUrl, long Size);

public enum UpdateStatus { Idle, Checking, UpToDate, Available, Downloading, Failed }

public sealed record UpdateState(UpdateStatus Status, AppUpdate? Info = null, int Progress = 0, string? Error = null)
{
    public static readonly UpdateState Idle = new(UpdateStatus.Idle);
}

/// <summary>
/// 自动更新：查 GitHub 上最新的已发布 Release（草稿和预发布不算），下载其中的安装包，
/// 以 --update 模式运行它，然后本程序退出；安装包（src/Setup）等本程序退出后替换文件并重新启动。
/// 安装版和便携版都能更新（程序目录里有安装包写入的文件清单）；开发时直接运行编译输出不能更新。
/// </summary>
public static class Updater
{
    public const string Repo = "AnLifeX/DeviceBatteryTray";
    public static string RepoUrl => $"https://github.com/{Repo}";

    const string SetupAsset = "DeviceBatteryTray-Setup.exe";
    /// <summary>安装包写入的程序文件清单，见 src/Setup/Product.cs。</summary>
    const string Manifest = "DeviceBatteryTray.files";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeviceBatteryTray", CurrentText));
        return http;
    }

    /// <summary>当前版本号，如 "0.2.0"。</summary>
    public static string CurrentText { get; } =
        (typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    static string AppDir => AppContext.BaseDirectory.TrimEnd('\\');

    /// <summary>由安装包安装（安装版或便携版）时才能自动更新。</summary>
    public static bool CanSelfUpdate => File.Exists(Path.Combine(AppDir, Manifest));

    static Version ToVersion(string s)
    {
        s = s.TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    }

    /// <summary>有新版本返回信息，已是最新返回 null；失败抛异常（消息可直接展示）。</summary>
    public static async Task<AppUpdate?> CheckAsync()
    {
        if (!CanSelfUpdate) throw new Exception("当前版本不支持自动更新");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req); }
        catch (HttpRequestException e) { Log.W("检查更新网络错误: " + e.Message); throw new Exception("无法检查更新，请检查网络后重试", e); }
        catch (TaskCanceledException e) { Log.W("检查更新超时"); throw new Exception("检查更新超时，请稍后重试", e); }
        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // 还没有发布过任何版本
            if (!resp.IsSuccessStatusCode) { Log.W($"检查更新返回 HTTP {(int)resp.StatusCode}"); throw new Exception("检查更新失败，请稍后重试"); }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var latest = ToVersion(doc.RootElement.GetProperty("tag_name").GetString() ?? "");
            if (latest <= ToVersion(CurrentText)) return null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (a.GetProperty("name").GetString() == SetupAsset)
                    return new AppUpdate(latest, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64());
            Log.W($"v{latest} 的发布里没有安装包");
            throw new Exception("新版本暂不可用，请稍后重试");
        }
    }

    /// <summary>下载安装包到临时目录，返回文件路径；progress 报告 0~100。</summary>
    public static async Task<string> DownloadAsync(AppUpdate u, IProgress<int> progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeviceBatteryTray");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"DeviceBatteryTray-Setup-{u.Version}.exe");
        using var resp = await Http.GetAsync(u.SetupUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? u.Size, done = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(file))
        {
            var buf = new byte[81920];
            int n, last = -1;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                int pct = total > 0 ? (int)(done * 100 / total) : 0;
                if (pct != last) progress.Report(last = pct);
            }
        }
        if (u.Size > 0 && new FileInfo(file).Length != u.Size) throw new IOException("安装包下载不完整，请稍后重试");
        return file;
    }

    /// <summary>
    /// 以更新模式运行下载好的安装包：等本进程退出后替换文件，并重新启动（主页面开着的话新版本也打开主页面）。
    /// 调用方随后要正常退出（还原任务栏）。
    /// </summary>
    public static void ApplyAndRestart(string setupFile, bool showMain)
    {
        // 启动前锁住文件（只允许读），防止下载完到运行之间被别的程序替换
        using var guard = new FileStream(setupFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var args = $"--update --dir \"{AppDir}\" --wait-pid {Environment.ProcessId}";
        if (!showMain) args += " " + AutoStart.BackgroundArg;
        Process.Start(new ProcessStartInfo(setupFile, args) { UseShellExecute = false });
    }
}
