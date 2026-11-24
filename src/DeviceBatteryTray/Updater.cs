using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeviceBatteryTray;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256, string PageUrl);

public enum UpdateStatus { Idle, Checking, UpToDate, Available, Downloading, Failed }

public sealed record UpdateState(UpdateStatus Status, UpdateInfo? Info = null, int Progress = 0, string? Error = null)
{
    public static readonly UpdateState Idle = new(UpdateStatus.Idle);
}

/// <summary>
/// 从 GitHub Releases 检查 / 下载新版本（便携单文件，没有安装步骤）（只取 Release 里名为 DeviceBatteryTray.exe 的附件）。
/// 替换方式：当前 exe 改名为 .old → 新文件改名为 exe → 启动新进程（等旧进程退出）→ 旧进程退出；
/// 新进程启动时删掉 .old。仓库需为公开，否则 API 返回 404。
/// </summary>
public static class Updater
{
    public const string Repo = "AnLifeX/DeviceBatteryTray";
    public const string AssetName = "DeviceBatteryTray.exe";
    public static string RepoUrl => $"https://github.com/{Repo}";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"DeviceBatteryTray/{CurrentText}");
        return h;
    }

    /// <summary>当前版本号（去掉 +commit 后缀），如 "0.1.0" 或 "0.1.0-ci.12"。</summary>
    public static string CurrentText =>
        (typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static Version Current => Parse(CurrentText) ?? new Version(0, 0, 0);

    public static Version? Parse(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("找不到程序路径");

    /// <summary>只有单文件发布版才能自我替换（开发时 bin 目录里是 apphost + dll）。</summary>
#pragma warning disable IL3000
    public static bool CanSelfUpdate => string.IsNullOrEmpty(typeof(Updater).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>有新版本返回信息，已是最新返回 null；失败抛异常（消息可直接展示）。</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // 环境变量 DEVICEBATTERYTRAY_UPDATE_API 可覆盖地址（测试更新流程用，指向一个同格式的 JSON）
        string api = Environment.GetEnvironmentVariable("DEVICEBATTERYTRAY_UPDATE_API") is { Length: > 0 } o ? o
            : $"https://api.github.com/repos/{Repo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await Http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) throw new Exception("没有找到发布版本（仓库未公开或还没有发布）");
        if (res.StatusCode == HttpStatusCode.Forbidden) throw new Exception("GitHub 请求过于频繁，稍后再试");
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = Parse(tag) ?? throw new Exception($"无法识别的版本号：{tag}");
        if (version <= Current) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:") ? dg[7..] : null;
            return new UpdateInfo(version, tag, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha,
                root.GetProperty("html_url").GetString() ?? RepoUrl);
        }
        throw new Exception($"{tag} 里没有 {AssetName}");
    }

    /// <summary>下载并替换 exe；成功后调用 <see cref="Restart"/>。</summary>
    public static async Task InstallAsync(UpdateInfo u, IProgress<int> progress, CancellationToken ct = default)
    {
        if (!CanSelfUpdate) throw new Exception("开发版本不支持自动更新，请使用发布版");
        string exe = ExePath, tmp = exe + ".new", old = exe + ".old";

        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                using var res = await Http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                var buf = new byte[81920];
                long done = 0;
                int n, last = -1;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    int p = u.Size > 0 ? (int)(done * 100 / u.Size) : 0;
                    if (p != last) progress.Report(last = p);
                }
                if (u.Size > 0 && done != u.Size) throw new Exception($"下载不完整（{done}/{u.Size} 字节）");
            }

            if (u.Sha256 != null)
            {
                await using var fs = File.OpenRead(tmp);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hash.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase)) throw new Exception("文件校验失败（SHA-256 不一致）");
            }

            // 运行中的 exe 不能覆盖，但可以改名
            File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(tmp);
            throw new Exception("没有写入程序目录的权限，请以管理员身份运行或手动下载");
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>启动新版本（等本进程退出后再接管），调用方随后退出。主页面开着的话新版本也打开主页面。</summary>
    public static void Restart(bool showMain)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        if (!showMain) psi.ArgumentList.Add("--background");
        foreach (var a in new[] { "--updated", "--wait-pid", Environment.ProcessId.ToString() }) psi.ArgumentList.Add(a);
        if (Environment.GetCommandLineArgs().Contains("--no-elevate")) psi.ArgumentList.Add("--no-elevate");
        // 当前是管理员进程时，子进程继承管理员权限，不会再弹 UAC
        Process.Start(psi);
    }

    /// <summary>启动时清理上次更新留下的文件。</summary>
    public static void CleanupOld()
    {
        if (Environment.ProcessPath is not { } exe) return;
        TryDelete(exe + ".old");
        TryDelete(exe + ".new");
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
