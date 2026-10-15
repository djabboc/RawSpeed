using System.Windows;

namespace RawSpeed.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "RawSpeed · 操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
