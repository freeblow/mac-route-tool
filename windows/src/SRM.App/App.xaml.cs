using System.Windows;
using SRM.App.Dialogs;

namespace SRM.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                MessageDialog.Error(MainWindow, "程序出现异常", args.Exception.Message, args.Exception.GetType().Name);
            }
            catch
            {
                MessageBox.Show(args.Exception.Message, "SRM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }
}
