using System.Windows;
using System.Windows.Input;

namespace DeviceBatteryTray.Views;

public partial class DevicesPage : System.Windows.Controls.UserControl
{
    public DevicesPage()
    {
        DataContext = MainWindow.ViewModel;
        InitializeComponent();
    }

    /// <summary>进入重命名时自动聚焦并全选，直接输入就能覆盖。</summary>
    void NameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is System.Windows.Controls.TextBox box)
            Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); box.SelectAll(); });
    }

    /// <summary>点到别处也算确认（Esc 已经先退出编辑，不会误保存）。</summary>
    void NameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceItem item } && item.Editing)
            item.CommitRenameCommand.Execute(null);
    }
}
