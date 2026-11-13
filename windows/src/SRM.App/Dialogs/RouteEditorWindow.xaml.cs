using System.Windows;
using System.Windows.Input;
using SRM.Core;

namespace SRM.App.Dialogs;

public sealed class InterfaceOption
{
    public int? Index { get; init; }
    public string Text { get; init; } = string.Empty;

    public override string ToString() => Text;
}

public partial class RouteEditorWindow : Window
{
    private readonly RouteEntry? _existing;

    public RouteEditorWindow(RouteEntry? existing, IReadOnlyList<NicInfo> nics)
    {
        InitializeComponent();
        _existing = existing;
        HeaderTitle.Text = existing is null ? "添加静态路由" : "编辑静态路由";

        var options = new List<InterfaceOption>
        {
            new() { Index = null, Text = "自动（按网关自动选择网卡）" },
        };
        foreach (var nic in nics.Where(item => item.Index > 1))
        {
            var label = nic.Address.Length > 0
                ? $"{nic.Index} · {nic.Name}（{nic.Address}）"
                : $"{nic.Index} · {nic.Name}";
            options.Add(new InterfaceOption { Index = nic.Index, Text = label });
        }
        InterfaceBox.ItemsSource = options;

        if (existing is null)
        {
            InterfaceBox.SelectedIndex = 0;
        }
        else
        {
            DestinationBox.Text = existing.Destination;
            GatewayBox.Text = existing.Gateway;
            NoteBox.Text = existing.Note;
            EnabledBox.IsChecked = existing.Enabled;
            InterfaceBox.SelectedItem = options.FirstOrDefault(item => item.Index == existing.InterfaceIndex) ?? options[0];
        }

        Loaded += (_, _) =>
        {
            DestinationBox.Focus();
            DestinationBox.SelectAll();
        };
    }

    public RouteEntry? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Ipv4.TryParsePrefix(DestinationBox.Text, out var destination, out var error))
        {
            ShowError(error);
            DestinationBox.Focus();
            return;
        }
        if (!Ipv4.TryParse(GatewayBox.Text, out var gateway))
        {
            ShowError("网关地址不正确，应形如 10.40.81.254。");
            GatewayBox.Focus();
            return;
        }

        Result = _existing ?? new RouteEntry();
        Result.Destination = destination;
        Result.Gateway = Ipv4.ToText(gateway);
        Result.InterfaceIndex = (InterfaceBox.SelectedItem as InterfaceOption)?.Index;
        Result.Note = NoteBox.Text.Trim();
        Result.Enabled = EnabledBox.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

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
