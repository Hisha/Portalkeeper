using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Portalkeeper.Models;
using Portalkeeper.Services;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

public partial class AddAddonWindow : Window
{
    private AddonDefinition? _discoveredAddon;

    public AddAddonWindow()
    {
        InitializeComponent();
    }

    private async void Discover_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        try
        {
            IsEnabled = false;
            StatusTextBlock.Text = "Inspecting GitHub repository...";
            PreviewBorder.IsVisible = false;
            AddOnlyButton.IsEnabled = false;
            AddInstallButton.IsEnabled = false;

            _discoveredAddon =
                await viewModel.DiscoverPersonalAddonAsync(
                    GitUrlTextBox.Text ?? string.Empty);

            PreviewNameTextBlock.Text = _discoveredAddon.Name;
            PreviewVersionTextBlock.Text = _discoveredAddon.Version;
            PreviewFolderTextBlock.Text = _discoveredAddon.Folder;
            PreviewBranchTextBlock.Text = _discoveredAddon.SourceBranch;
            PreviewPathTextBlock.Text =
                string.IsNullOrWhiteSpace(_discoveredAddon.AddonPath)
                    ? "Repository root"
                    : _discoveredAddon.AddonPath;

            PreviewBorder.IsVisible = true;
            AddOnlyButton.IsEnabled = true;
            AddInstallButton.IsEnabled = true;
            StatusTextBlock.Text =
                "Addon discovered. Review the details before adding it to Portalkeeper.";
        }
        catch (Exception ex)
        {
            _discoveredAddon = null;
            StatusTextBlock.Text = UserErrorService.Format(ex);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void AddOnly_Click(
        object? sender,
        RoutedEventArgs e)
    {
        await AddAsync(false);
    }

    private async void AddInstall_Click(
        object? sender,
        RoutedEventArgs e)
    {
        await AddAsync(true);
    }

    private async System.Threading.Tasks.Task AddAsync(bool installIfMissing)
    {
        if (_discoveredAddon is null ||
            DataContext is not MainViewModel viewModel)
            return;

        try
        {
            IsEnabled = false;
            StatusTextBlock.Text = installIfMissing
                ? "Adding addon and installing if needed..."
                : "Adding addon to Portalkeeper...";

            await viewModel.AddPersonalAddonAsync(
                _discoveredAddon,
                installIfMissing);

            Close();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = UserErrorService.Format(ex);
            IsEnabled = true;
        }
    }

    private void Cancel_Click(
        object? sender,
        RoutedEventArgs e)
    {
        Close();
    }
}
