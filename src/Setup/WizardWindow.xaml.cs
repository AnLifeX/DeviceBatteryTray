using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeviceBatteryTray.Views;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DeviceBatteryTray.Setup;

public enum WizardMode { Install, Uninstall }

/// <summary>
/// 安装向导：欢迎 → 安装位置 → 附加选项 → 安装 → 完成；
/// 卸载向导：确认 → 卸载 → 完成。
/// </summary>
public partial class WizardWindow : FluentWindow
{
    readonly WizardMode _mode;
    readonly string _uninstallDir = "";
    readonly FrameworkElement[] _pages;
    readonly string[] _steps;
    readonly InstallOptions _opts = new();
    readonly Product.Installation? _existing;
    int _index;
    bool _busy;

    static WizardWindow() => Corners.KeepBorderlessWhenTransparent(typeof(WizardWindow));

    public WizardWindow(WizardMode mode, string? dir)
    {
        _mode = mode;
        InitializeComponent();
        Corners.RoundWindow(this, 8);
        var logo = LoadLogo();
        Logo.Source = logo;
        TitleIcon.Source = logo;
        Icon = logo;
        VersionText.Text = "版本 " + Product.Version;

        if (mode == WizardMode.Install)
        {
            Title = $"{Product.Name} 安装程序";
            _pages = new FrameworkElement[] { PageWelcome, PageLocation, PageOptions, PageProgress, PageDone };
            _steps = new[] { "欢迎", "安装位置", "附加选项", "安装", "完成" };
            _existing = Product.FindInstalled();
            InitInstall();
        }
        else
        {
            Title = $"{Product.Name} 卸载程序";
            _uninstallDir = dir ?? "";
            _pages = new FrameworkElement[] { PageConfirm, PageProgress, PageDone };
            _steps = new[] { "确认", "卸载", "完成" };
            var version = Product.FindInstalled()?.Version;
            ConfirmText.Text = $"将移除设备电量{(string.IsNullOrEmpty(version) ? "" : " v" + version)}及其快捷方式和开机自启项。";
            ConfirmDir.Text = "位置：" + _uninstallDir;
        }
        TitleBar.Title = Title;

        BackButton.Click += (_, _) => Go(_index - 1);
        NextButton.Click += async (_, _) => await NextAsync();
        CancelButton.Click += (_, _) => Close();
        Closing += (_, e) => e.Cancel = _busy; // 安装 / 卸载进行中不能关闭
        Go(0);
    }

    void InitInstall()
    {
        if (_existing != null)
        {
            _opts.Dir = _existing.Dir;
            _opts.DesktopShortcut = Shortcuts.DesktopExists;
            _opts.AutoStart = Product.AutoStartEnabled;
            DirBox.IsReadOnly = true;
            BrowseButton.IsEnabled = false;
            UpgradeNote.Visibility = Visibility.Visible;
        }
        DirBox.Text = _opts.Dir;
        // 路径较长时显示结尾（程序文件夹名），而不是开头
        DirBox.Loaded += (_, _) => { DirBox.CaretIndex = DirBox.Text.Length; DirBox.ScrollToHorizontalOffset(double.MaxValue); };
        DesktopBox.IsChecked = _opts.DesktopShortcut;
        AutoStartBox.IsChecked = _opts.AutoStart;
        BrowseButton.Click += (_, _) => Browse();
        RuntimeInfo.IsOpen = !DotNetRuntime.IsInstalled();
        if (Payload.Present) SizeText.Text = $"所需空间：{Payload.Size / 1048576.0:0.#} MB";

        // 欢迎页的提示：不能安装的情况会禁用「下一步」
        if (!Payload.Present)
            ShowWelcomeInfo(InfoBarSeverity.Error, "安装包不完整", "请重新下载安装包。");
        else if (Product.OldVelopackInstalled())
            ShowWelcomeInfo(InfoBarSeverity.Warning, "请先卸载旧版本",
                "请在「设置 → 应用」中卸载「设备电量」后重新运行安装程序。");
        else if (_existing != null)
        {
            var cmp = CompareVersions(_existing.Version, Product.Version);
            ShowWelcomeInfo(cmp > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
                cmp < 0 ? $"将从 v{_existing.Version} 升级到 v{Product.Version}" : cmp == 0 ? $"已安装 v{Product.Version}" : $"已安装更新的版本 v{_existing.Version}",
                cmp < 0 ? "设置将保留。" : cmp == 0 ? "将重新安装，设置将保留。" : $"将替换为 v{Product.Version}，设置将保留。");
        }
    }

    bool CanInstall => Payload.Present && !Product.OldVelopackInstalled();

    void ShowWelcomeInfo(InfoBarSeverity severity, string title, string message)
    {
        WelcomeInfo.Severity = severity;
        WelcomeInfo.Title = title;
        WelcomeInfo.Message = message;
        WelcomeInfo.IsOpen = true;
    }

    // ---------- 翻页 ----------

    void Go(int index)
    {
        _index = Math.Max(0, Math.Min(index, _pages.Length - 1));
        var page = _pages[_index];
        foreach (var p in _pages) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;

        bool beforeRun = page != PageProgress && page != PageDone;
        BackButton.Visibility = _mode == WizardMode.Install && beforeRun && _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = page == PageDone ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = page == PageOptions ? "安装" : page == PageConfirm ? "卸载" : page == PageDone ? "完成" : "下一步";
        NextButton.IsEnabled = page != PageProgress && (page != PageWelcome || CanInstall);
        UpdateSteps();
    }

    async Task NextAsync()
    {
        var page = _pages[_index];
        if (page == PageLocation && !ValidateDir()) return;
        if (page == PageOptions || page == PageConfirm || page == PageProgress) { await RunAsync(); return; } // 进度页上的按钮是「重试」
        if (page == PageDone)
        {
            if (_mode == WizardMode.Install && LaunchBox.IsChecked == true)
                try { AppProcess.Start(_opts.Dir); } catch (Exception e) { Log.W("启动失败: " + e.Message); }
            Close();
            return;
        }
        Go(_index + 1);
    }

    // ---------- 安装位置 ----------

    void Browse()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择安装位置（将在其中创建 DeviceBatteryTray 文件夹）",
            SelectedPath = Directory.Exists(DirBox.Text) ? DirBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var dir = dlg.SelectedPath;
        // 选的是父文件夹时自动加一层程序文件夹，避免把文件散落在桌面之类的地方
        if (!string.Equals(Path.GetFileName(dir.TrimEnd('\\')), Product.Id, StringComparison.OrdinalIgnoreCase))
            dir = Path.Combine(dir, Product.Id);
        DirBox.Text = dir;
        DirBox.CaretIndex = dir.Length;
        DirBox.ScrollToHorizontalOffset(double.MaxValue);
        LocationError.IsOpen = false;
    }

    /// <summary>检查位置是否可用（能不能写入）。</summary>
    bool ValidateDir()
    {
        var dir = DirBox.Text.Trim();
        string? error = null;
        if (dir.Length == 0 || !Path.IsPathRooted(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            error = "路径无效。";
        else if (File.Exists(dir))
            error = "该路径是文件，请选择文件夹。";
        else
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (UnauthorizedAccessException) { error = "没有写入权限，请选择其他文件夹。"; }
            catch (Exception e) { error = e.Message; }
        }
        LocationError.Message = error ?? "";
        LocationError.IsOpen = error != null;
        if (error == null) _opts.Dir = Path.GetFullPath(dir);
        return error == null;
    }

    // ---------- 执行 ----------

    async Task RunAsync()
    {
        Go(Array.IndexOf(_pages, PageProgress));
        bool install = _mode == WizardMode.Install;
        ProgressTitle.Text = install ? (_existing != null ? "正在升级" : "正在安装") : "正在卸载";
        ErrorInfo.IsOpen = false;
        StatusText.Visibility = Visibility.Visible;
        Bar.Visibility = Visibility.Visible;
        _busy = true;
        CancelButton.IsEnabled = false;
        TitleBar.ShowClose = false;

        var progress = new Progress<Status>(s =>
        {
            StatusText.Text = s.Text;
            Bar.IsIndeterminate = s.Fraction == null;
            if (s.Fraction is double f) Bar.Value = f * 100;
        });
        try
        {
            if (install)
            {
                _opts.DesktopShortcut = DesktopBox.IsChecked == true;
                _opts.AutoStart = AutoStartBox.IsChecked == true;
                await Steps.InstallAsync(_opts, progress);
            }
            else await Steps.UninstallAsync(_uninstallDir, KeepDataBox.IsChecked == true, progress);
            ShowDone();
        }
        catch (Exception e)
        {
            Log.W((install ? "安装" : "卸载") + "失败: " + e);
            ShowError(e.Message);
        }
    }

    void ShowDone()
    {
        _busy = false;
        TitleBar.ShowClose = true;
        if (_mode == WizardMode.Install)
        {
            DoneTitle.Text = _existing != null ? "升级完成" : "安装完成";
        }
        else
        {
            DoneTitle.Text = "卸载完成";
            LaunchBox.Visibility = Visibility.Collapsed;
        }
        Go(Array.IndexOf(_pages, PageDone));
    }

    void ShowError(string message)
    {
        _busy = false;
        TitleBar.ShowClose = true;
        StatusText.Visibility = Visibility.Collapsed;
        Bar.Visibility = Visibility.Collapsed;
        ErrorInfo.Title = _mode == WizardMode.Install ? "安装失败" : "卸载失败";
        ErrorInfo.Message = message + $"\n日志：{Log.FilePath}";
        ErrorInfo.IsOpen = true;
        NextButton.Content = "重试";
        NextButton.IsEnabled = true;
        CancelButton.Content = "关闭";
        CancelButton.IsEnabled = true;
    }

#if DEBUG
    /// <summary>开发用：直接显示某个页面的样子（不执行任何操作），见 Preview.cs。</summary>
    internal void ShowPreview(string page)
    {
        WelcomeInfo.IsOpen = false;
        switch (page)
        {
            case "location": Go(1); break;
            case "options": Go(2); RuntimeInfo.IsOpen = true; break;
            case "progress":
            case "error":
                Go(3);
                ProgressTitle.Text = "正在安装";
                StatusText.Text = "正在复制文件…";
                Bar.Value = 62;
                CancelButton.IsEnabled = false;
                if (page == "error") ShowError("替换程序文件失败：文件正被另一个程序使用。");
                break;
            case "done": case "uninstall-done": ShowDone(); break;
            default: Go(0); break;
        }
        if (_pages[_index] == PageWelcome || _pages[_index] == PageConfirm) NextButton.IsEnabled = true;
    }
#endif

    // ---------- 左侧步骤 ----------

    void UpdateSteps()
    {
        StepList.Children.Clear();
        for (int i = 0; i < _steps.Length; i++)
        {
            // 到了完成页，所有步骤（包括「完成」本身）都打勾
            bool done = i < _index || _pages[_index] == PageDone, current = i == _index;
            var dot = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5) };
            var mark = new TextBlock
            {
                Text = done ? "✓" : (i + 1).ToString(),
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            dot.Child = mark;
            if (done || current)
            {
                dot.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
                dot.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                mark.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
            }
            else
            {
                dot.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
                mark.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            }
            var label = new TextBlock
            {
                Text = _steps[i], FontSize = 14, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, current ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            row.Children.Add(dot);
            row.Children.Add(label);
            StepList.Children.Add(row);
        }
    }

    // ---------- 工具 ----------

    /// <summary>程序图标里最大的一帧（256px）。</summary>
    internal static ImageSource LoadLogo()
    {
        var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/Setup;component/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    /// <summary>比较 "0.1.0" 形式的版本号。</summary>
    static int CompareVersions(string a, string b) =>
        (Version.TryParse(a, out var va) ? va : new Version(0, 0)).CompareTo(Version.TryParse(b, out var vb) ? vb : new Version(0, 0));
}
