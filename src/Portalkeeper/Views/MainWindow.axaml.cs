using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void LocateClient_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var folders =
            await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Locate World of Warcraft 3.3.5a",
                    AllowMultiple = false
                });

        var folder = folders.FirstOrDefault();

        if (folder is null)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.SetClientDirectory(folder.Path.LocalPath);
    }
    
    private async void ManageAddons_Click(
    object? sender,
    RoutedEventArgs e)
	{
	    if (DataContext is not MainViewModel viewModel)
	        return;
	
	    var window =
	        new ManageAddonsWindow
	        {
	            DataContext = viewModel
	        };
	
	    await window.ShowDialog(this);
	}
    

    private async void EnterRealm_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var hiddenForGame = false;

        await viewModel.EnterRealmAsync(() =>
        {
            if (!viewModel.HidePortalkeeperWhileGameRuns)
                return;

            hiddenForGame = true;
            Hide();
        });

        if (hiddenForGame)
        {
            Show();
            Activate();
        }
    }

    private async void Settings_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var window = new SettingsWindow
        {
            DataContext = viewModel
        };

        await window.ShowDialog(this);
    }
}
