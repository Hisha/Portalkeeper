using Avalonia.Controls;
using Avalonia.Interactivity;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

public partial class ManageAddonsWindow : Window
{
    public ManageAddonsWindow()
    {
        InitializeComponent();
    }

    private async void Refresh_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        await viewModel.RefreshAddonsAsync();
    }

    private void Close_Click(
        object? sender,
        RoutedEventArgs e)
    {
        Close();
    }
}