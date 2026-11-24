namespace DeviceBatteryTray.Views;

public partial class DevicesPage : System.Windows.Controls.UserControl
{
    public DevicesPage()
    {
        DataContext = MainWindow.ViewModel;
        InitializeComponent();
    }
}
