namespace DeviceBatteryTray.Views;

public partial class AboutPage : System.Windows.Controls.UserControl
{
    public AboutPage()
    {
        DataContext = MainWindow.ViewModel;
        InitializeComponent();
    }
}
