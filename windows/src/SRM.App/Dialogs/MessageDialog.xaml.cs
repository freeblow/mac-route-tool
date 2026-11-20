using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using SRM.Core.Localization;

namespace SRM.App.Dialogs;

public enum DialogKind
{
    Info,
    Warning,
    Error,
    Question,
}

public partial class MessageDialog : Window
{
    private bool _cancellable;

    private MessageDialog()
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);
    }

    public static void Info(Window? owner, string title, string message, string detail = "") =>
        Show(owner, DialogKind.Info, title, message, detail, Loc.T("Ui.Dialog.Ok"), null);

    public static void Warning(Window? owner, string title, string message, string detail = "") =>
        Show(owner, DialogKind.Warning, title, message, detail, Loc.T("Ui.Dialog.Ok"), null);

    public static void Error(Window? owner, string title, string message, string detail = "") =>
        Show(owner, DialogKind.Error, title, message, detail, Loc.T("Ui.Dialog.Ok"), null);

    public static bool Confirm(Window? owner, string title, string message, string detail = "", string? confirmText = null, bool danger = false) =>
        Show(owner, danger ? DialogKind.Error : DialogKind.Question, title, message, detail,
            confirmText ?? Loc.T("Ui.Dialog.Confirm"), Loc.T("Ui.Dialog.Cancel"));

    private static bool Show(Window? owner, DialogKind kind, string title, string message, string detail, string confirmText, string? cancelText)
    {
        var dialog = new MessageDialog();
        if (owner is not null && !ReferenceEquals(owner, dialog) && owner.IsLoaded)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.Configure(kind, title, message, detail, confirmText, cancelText);
        return dialog.ShowDialog() == true;
    }

    private void Configure(DialogKind kind, string title, string message, string detail, string confirmText, string? cancelText)
    {
        _cancellable = cancelText is not null;
        TitleText.Text = title;
        MessageText.Text = message;
        DetailText.Text = detail;
        DetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirmText;
        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        switch (kind)
        {
            case DialogKind.Warning:
                IconText.Text = "!";
                IconBadge.Background = (Brush)FindResource("WarningSoftBrush");
                IconText.Foreground = (Brush)FindResource("WarningBrush");
                break;
            case DialogKind.Error:
                IconText.Text = "!";
                IconBadge.Background = (Brush)FindResource("DangerSoftBrush");
                IconText.Foreground = (Brush)FindResource("DangerBrush");
                ConfirmButton.Style = (Style)FindResource("DangerSolidButton");
                break;
            case DialogKind.Question:
                IconText.Text = "?";
                break;
            default:
                IconText.Text = "i";
                break;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
