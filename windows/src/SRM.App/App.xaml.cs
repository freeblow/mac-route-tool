using System.Windows;
using SRM.App.Dialogs;
using SRM.Core.Localization;

namespace SRM.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Loc.Initialize();
        LocResources.Install();
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try
            {
                MessageDialog.Error(MainWindow, Loc.T("Ui.Error.Title"), args.Exception.Message, args.Exception.GetType().Name);
            }
            catch
            {
                MessageBox.Show(args.Exception.Message, "SRM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }
}
