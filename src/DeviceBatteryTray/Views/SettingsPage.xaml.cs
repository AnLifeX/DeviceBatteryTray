namespace DeviceBatteryTray.Views;

public partial class SettingsPage : System.Windows.Controls.UserControl
{
    public SettingsPage()
    {
        DataContext = MainWindow.ViewModel;
        InitializeComponent();
    }
}
