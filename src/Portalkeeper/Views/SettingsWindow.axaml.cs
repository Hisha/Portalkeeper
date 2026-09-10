using Portalkeeper.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Portalkeeper.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private async void ChangeRealm_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CanSwitchRealm)
            await new RealmSelectionWindow(vm).ShowDialog(this);
    }
    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.CheckForUpdatesAsync();
    }
    private void ViewRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ViewRelease();
    }
    private void Close_Click(
        object? sender,
        RoutedEventArgs e)
    {
        Close();
    }
}
