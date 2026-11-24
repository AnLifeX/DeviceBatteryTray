using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace DeviceBatteryTray;

/// <summary>一个可用的新版本。</summary>
public sealed record AppUpdate(Version Version, Velopack.UpdateInfo Info);

public enum UpdateStatus { Idle, Checking, UpToDate, Available, Downloading, Failed }

public sealed record UpdateState(UpdateStatus Status, AppUpdate? Info = null, int Progress = 0, string? Error = null)
{
    public static readonly UpdateState Idle = new(UpdateStatus.Idle);
}

/// <summary>
/// 自动更新（Velopack）。安装版和便携版（Portable.zip）都能更新：从 GitHub 已发布的 Release 下载
/// （草稿不算），优先下载增量包，由 Velopack 的 Update.exe 在本程序退出后替换并重启。
/// 开发时直接运行 bin 里的 exe 不算安装，不能更新。
/// </summary>
public static class Updater
{
    public const string Repo = "AnLifeX/DeviceBatteryTray";
    public static string RepoUrl => $"https://github.com/{Repo}";

    private static UpdateManager? _mgr;

    /// <summary>
    /// 更新源。环境变量 DEVICEBATTERYTRAY_UPDATE_DIR 指向一个 vpk pack 输出目录时改用本地目录
    /// （测试更新流程用），否则用 GitHub Releases。
    /// </summary>
    static UpdateManager Mgr => _mgr ??= new UpdateManager(
        Environment.GetEnvironmentVariable("DEVICEBATTERYTRAY_UPDATE_DIR") is { Length: > 0 } dir
            ? new SimpleFileSource(new DirectoryInfo(dir))
            : new GithubSource(RepoUrl, null, prerelease: false));

    /// <summary>当前版本号，如 "0.2.0"。</summary>
    public static string CurrentText
    {
        get
        {
            try { if (Mgr.CurrentVersion is { } v) return v.ToString(); } catch { }
            return (typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
        }
    }

    /// <summary>是否由 Velopack 安装（安装版或便携版）；只有这时才能自动更新。</summary>
    public static bool CanSelfUpdate
    {
        get { try { return Mgr.IsInstalled; } catch { return false; } }
    }

    static Version ToVersion(string s)
    {
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    }

    /// <summary>有新版本返回信息，已是最新返回 null；失败抛异常（消息可直接展示）。</summary>
    public static async Task<AppUpdate?> CheckAsync()
    {
        if (!CanSelfUpdate) throw new Exception("当前是开发版本，不支持自动更新（请使用安装版或便携版）");
        var info = await Mgr.CheckForUpdatesAsync();
        return info == null ? null : new AppUpdate(ToVersion(info.TargetFullRelease.Version.ToString()), info);
    }

    /// <summary>下载新版本（优先增量包）；完成后调用 <see cref="ApplyAndRestart"/>。</summary>
    public static Task DownloadAsync(AppUpdate u, IProgress<int> progress) =>
        Mgr.DownloadUpdatesAsync(u.Info, p => progress.Report(p));

    /// <summary>
    /// 启动 Velopack 更新程序：等本进程退出后替换文件并重启（主页面开着的话新版本也打开主页面）。
    /// 调用方随后要正常退出（还原任务栏），更新程序最多等 60 秒。
    /// </summary>
    public static void ApplyAndRestart(AppUpdate u, bool showMain)
    {
        var args = new List<string>();
        if (!showMain) args.Insert(0, AutoStart.BackgroundArg);
        // silent: false：替换文件时显示和安装时同样的启动画面 + 进度条
        Mgr.WaitExitThenApplyUpdates(u.Info.TargetFullRelease, silent: false, restart: true, args.ToArray());
    }
}
