using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Portalkeeper.Models;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

public partial class ManageAddonsWindow : Window
{
    public ManageAddonsWindow()
    {
        InitializeComponent();
    }


    private async void AddAddon_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var window = new AddAddonWindow
        {
            DataContext = viewModel
        };

        await window.ShowDialog(this);
    }

    private async void RemovePersonalAddon_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            sender is not Button button ||
            button.DataContext is not AddonInfo addon)
            return;

        try
        {
            IsEnabled = false;
            await viewModel.RemovePersonalAddonAsync(addon.Definition.Id);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void AddonAction_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            sender is not Button button ||
            button.DataContext is not AddonInfo addon)
            return;

        try
        {
            IsEnabled = false;
            await viewModel.InstallOrUpdateAddonAsync(addon.Definition.Id);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void UpdateAll_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        try
        {
            IsEnabled = false;
            await viewModel.InstallOrUpdateAllAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
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

    private async System.Threading.Tasks.Task ShowErrorAsync(string message)
    {
        var dialog = new Window
        {
            Title = "Portalkeeper Addon Error",
            Width = 520,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(20),
                RowDefinitions = RowDefinitions.Parse("*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new Button
                    {
                        Content = "CLOSE",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        [Grid.RowProperty] = 1
                    }
                }
            }
        };

        if (dialog.Content is Grid grid && grid.Children[1] is Button closeButton)
            closeButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
    }
}
