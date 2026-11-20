using System.Windows;
using SRM.Core.Localization;

namespace SRM.App.Dialogs;

public static class AppDialog
{
    public static void Info(Window? owner, string title, string message, string detail = "") =>
        MessageDialog.Info(owner, title, message, detail);

    public static void Warning(Window? owner, string title, string message, string detail = "") =>
        MessageDialog.Warning(owner, title, message, detail);

    public static void Error(Window? owner, string title, string message, string detail = "") =>
        MessageDialog.Error(owner, title, message, detail);

    public static bool Confirm(Window? owner, string title, string message, string detail = "", string? confirmText = null, bool danger = false) =>
        MessageDialog.Confirm(owner, title, message, detail, confirmText ?? Loc.T("Ui.Dialog.Confirm"), danger);
}
