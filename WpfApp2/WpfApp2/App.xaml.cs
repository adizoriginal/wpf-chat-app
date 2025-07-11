using System.Windows;

namespace WpfApp2
{
    public partial class App : Application
    {
        private void OnStartup(object sender, StartupEventArgs e)
        {
            new MainWindow().Show();
        }
    }
}