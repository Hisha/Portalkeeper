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
    

    private async void RealmCheckAgain_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        await viewModel.RediscoverRealmConfigurationAsync();
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

    private async void Calendar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var result = await viewModel.LoadCalendarAsync();
        if (result.Feed is null)
        {
            var error = new Window { Title = "Realm Calendar", Width = 520, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBlock { Text = result.Status, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(24) } };
            await error.ShowDialog(this);
            return;
        }

        var window = new CalendarWindow
        {
            DataContext = new CalendarViewModel(result.Feed, result.Status)
        };
        await window.ShowDialog(this);
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
