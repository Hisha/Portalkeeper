using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Portalkeeper.Models;
using Portalkeeper.ViewModels;
namespace Portalkeeper.Views;
public partial class ManagePatchesWindow : Window
{
    public ManagePatchesWindow() => InitializeComponent();
    private async void Install_Click(object? sender, RoutedEventArgs e) => await Act(sender, false);
    private async void Remove_Click(object? sender, RoutedEventArgs e) => await Act(sender, true);
    private async System.Threading.Tasks.Task Act(object? sender, bool remove)
    {
        if (DataContext is not MainViewModel vm || sender is not Button { DataContext: PatchInfo patch }) return;
        if (remove && !await ConfirmRemoval.ShowAsync(this, patch.Definition.Name)) return;
        try { IsEnabled = false; await vm.ManagePatchAsync(patch.Definition.Id, remove); }
        catch (Exception ex)
        {
            IsEnabled = true;
            await new Window { Title = "Patch operation failed", Width = 520, Height = 220,
                Content = new TextBlock { Text = ex.Message, Margin = new Avalonia.Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog(this);
        }
        finally { IsEnabled = true; }
    }
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
internal static class ConfirmRemoval
{
    public static async System.Threading.Tasks.Task<bool> ShowAsync(Window owner, string name)
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = $"Remove {name}? A backup will be retained. Required components will block launch until restored.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var yes = new Button { Content = "REMOVE" }; var no = new Button { Content = "CANCEL" };
        panel.Children.Add(yes); panel.Children.Add(no);
        var dialog = new Window { Title = "Confirm removal", Width = 500, Height = 240, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }
}
