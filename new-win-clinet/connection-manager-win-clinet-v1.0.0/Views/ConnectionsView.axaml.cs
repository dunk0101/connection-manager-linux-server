using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ConnectionManager.Models;
using ConnectionManager.ViewModels;

namespace ConnectionManager.Views;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView() => InitializeComponent();

    private ConnectionsViewModel? Vm => DataContext as ConnectionsViewModel;

    private static Connection? ConnectionOf(object? sender) => (sender as Control)?.DataContext as Connection;

    /// <summary>Click a row to select it.</summary>
    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm != null && ConnectionOf(sender) is { } c && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Vm.SelectedConnection = c;
    }

    /// <summary>Double-click a row to edit it (as before) - but not when double-clicking its buttons.</summary>
    private void Row_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null)
            return;
        if (Vm != null && ConnectionOf(sender) is { } c)
        {
            Vm.OpenEditDialogCommand.Execute(c);
            e.Handled = true;
        }
    }

    private void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm != null && ConnectionOf(sender) is { } c)
            Vm.ConnectCommand.Execute(c);
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm != null && ConnectionOf(sender) is { } c)
            Vm.MoveUpCommand.Execute(c);
    }

    private void MoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm != null && ConnectionOf(sender) is { } c)
            Vm.MoveDownCommand.Execute(c);
    }
}
