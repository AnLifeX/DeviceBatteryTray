using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DeviceBatteryTray.Views;
using Wpf.Ui.Controls;

namespace DeviceBatteryTray.Setup;

/// <summary>
/// 自动更新：主程序下载好新的安装包后用 --update 启动它，然后自己退出。
/// 这里等它退出、替换文件，再把它重新启动起来。出错时可以重试；关闭的话启动原来的版本。
/// </summary>
public partial class UpdateWindow : FluentWindow
{
    readonly string _dir;
    readonly int _waitPid;
    readonly bool _background;
    bool _busy;

    /// <summary>预览用：不执行更新。</summary>
    internal bool NoRun { get; set; }

    static UpdateWindow() => Corners.KeepBorderlessWhenTransparent(typeof(UpdateWindow));

    public UpdateWindow(string dir, int waitPid, bool background)
    {
        _dir = dir;
        _waitPid = waitPid;
        _background = background;
        InitializeComponent();
        Corners.RoundWindow(this, 8);
        var logo = WizardWindow.LoadLogo();
        Logo.Source = logo;
        TitleIcon.Source = logo;
        Icon = logo;
        Title = $"正在更新{Product.Name}";
        TitleBar.Title = Title;
        Heading.Text = Title;
        var old = OldVersion();
        VersionText.Text = old != null ? $"v{old} → v{Product.Version}" : $"v{Product.Version}";

        RetryButton.Click += async (_, _) => await RunAsync();
        CloseButton.Click += (_, _) => { StartApp(); Close(); };
        Closing += (_, e) => e.Cancel = _busy;
        Loaded += async (_, _) => { if (!NoRun) await RunAsync(); };
    }

    string? OldVersion()
    {
        try { return FileVersionInfo.GetVersionInfo(Path.Combine(_dir, Product.Exe)).ProductVersion?.Split('+')[0]; }
        catch { return null; }
    }

    async Task RunAsync()
    {
        _busy = true;
        ErrorInfo.IsOpen = false;
        ErrorButtons.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        Bar.Visibility = Visibility.Visible;
        var progress = new Progress<Status>(s =>
        {
            StatusText.Text = s.Text;
            Bar.IsIndeterminate = s.Fraction == null;
            if (s.Fraction is double f) Bar.Value = f * 100;
        });
        try
        {
            await Steps.UpdateAsync(_dir, _waitPid, progress);
            StatusText.Text = $"正在启动{Product.Name}…";
            _busy = false;
            StartApp();
            await Task.Delay(600);
            Close();
        }
        catch (Exception e)
        {
            Log.W("更新失败: " + e);
            ShowError(e.Message);
        }
    }

    void ShowError(string message)
    {
        _busy = false;
        Bar.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ErrorInfo.Message = message + $"\n日志：{Log.FilePath}";
        ErrorInfo.IsOpen = true;
        ErrorButtons.Visibility = Visibility.Visible;
        TitleBar.ShowClose = true;
    }

#if DEBUG
    /// <summary>开发用：显示进度 / 出错时的样子（不执行任何操作），见 Preview.cs。</summary>
    internal void ShowPreview(string page)
    {
        VersionText.Text = $"v0.1.0 → v{Product.Version}";
        StatusText.Text = "正在复制文件…";
        Bar.Value = 62;
        if (page == "update-error") ShowError("替换程序文件失败：文件正被另一个程序使用。");
    }
#endif

    /// <summary>启动主程序（更新失败时启动的是原来的版本：文件替换失败会回滚）。</summary>
    void StartApp()
    {
        try { AppProcess.Start(_dir, _background ? Product.BackgroundArg : ""); }
        catch (Exception e) { Log.W("启动主程序失败: " + e.Message); }
    }
}
