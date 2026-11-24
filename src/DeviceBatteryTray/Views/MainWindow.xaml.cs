using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace DeviceBatteryTray.Views;

using DeviceBatteryTray.Ui;

/// <summary>主页面（WPF + WPF-UI）：设备列表，右上角进入设置（左右滑动切换）。关闭窗口程序继续在任务栏运行。</summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>页面通过它拿到数据。</summary>
    public static MainViewModel? ViewModel { get; private set; }

    /// <summary>程序图标（标题栏、关于页用）。</summary>
    public static System.Windows.Media.ImageSource AppIconSource => _icon ??= LoadIcon();
    private static BitmapSource? _icon;

    private readonly IAppModel _m;
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private MainPage _page;

    public MainWindow(IAppModel model, MainPage page)
    {
        _m = model;
        _page = page;
        ViewModel = new MainViewModel(model);
        DataContext = ViewModel;
        InitializeComponent();

        WindowBackdropType = WpfHost.Backdrop;
        TitleIcon.Source = AppIconSource;
        Icon = TitleIcon.Source;

        ViewModel.NavigateRequested += p => Page = p;
        _m.Changed += OnModelChanged;
        AppSettings.Changed += OnSettingsChanged;
        _tick.Tick += (_, _) => ViewModel?.Tick();
        _tick.Start();
        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this, WpfHost.Backdrop, updateAccents: true);
            WpfHost.ApplyTheme();
            Navigate(_page);
        };
        // 取消主题监听要在 Closing 里做：到 Closed 时窗口句柄已销毁，UnWatch 会抛异常并打断后续的 Closed 处理
        Closing += (_, _) =>
        {
            try { SystemThemeWatcher.UnWatch(this); } catch (Exception e) { Log.W("取消主题监听失败: " + e.Message); }
        };
        Closed += (_, _) =>
        {
            _m.Changed -= OnModelChanged;
            AppSettings.Changed -= OnSettingsChanged;
            _tick.Stop();
        };
    }

    public MainPage Page
    {
        get => _page;
        set { _page = value; if (IsLoaded) Navigate(value); }
    }

    private DevicesPage? _devices;
    private SettingsPage? _settings;
    private AboutPage? _about;

    /// <summary>切换页面：进设置 / 关于从右侧滑入，返回从左侧滑入（淡入 + 位移 24px，ease-out）。</summary>
    void Navigate(MainPage page)
    {
        bool forward = page != MainPage.Home;
        object next = page switch
        {
            MainPage.Settings => _settings ??= new SettingsPage(),
            MainPage.About => _about ??= new AboutPage(),
            _ => _devices ??= new DevicesPage(),
        };
        if (ReferenceEquals(Host.Content, next)) return;
        bool animate = Host.Content != null;
        Host.Content = next;
        if (!animate) return;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(240);
        HostShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(forward ? 24 : -24, 0, dur) { EasingFunction = ease });
        Host.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    void OnModelChanged() => ViewModel?.Sync();

    void OnSettingsChanged()
    {
        WpfHost.ApplyTheme();
        ViewModel?.Sync();
    }

    static BitmapSource LoadIcon()
    {
        using var bmp = AppIcon.Bitmap(256);
        var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); // 走 PNG 保留透明通道
        ms.Position = 0;
        return BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
