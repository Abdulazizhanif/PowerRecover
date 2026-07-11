using System.Windows;

namespace PowerRecover.App;

public partial class App : Application
{
    public App()
    {
        // Make any silent crash visible instead of just disappearing
        DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(
                $"PowerRecover hit an unexpected error:\n\n{e.Exception}",
                "PowerRecover - Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            MessageBox.Show(
                $"PowerRecover failed to start:\n\n{e.ExceptionObject}",
                "PowerRecover - Startup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}
