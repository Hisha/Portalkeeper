using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Portalkeeper.Models;
using Portalkeeper.ViewModels;
namespace Portalkeeper.Views;

public partial class RealmSelectionWindow : Window
{
    public RealmSelectionWindow() { InitializeComponent(); }
    public RealmSelectionWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
        RealmList.SelectedItem = viewModel.AvailableRealms.FirstOrDefault(c => RealmChoice.SamePath(c.Path, viewModel.SelectedRealmPath));
    }
    private void RealmList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (UseRealmButton is not null) UseRealmButton.IsEnabled = RealmList.SelectedItem is RealmChoice;
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    private async void UseRealm_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || RealmList.SelectedItem is not RealmChoice choice) return;
        try
        {
            IsEnabled = false;
            StatusText.Text = "Loading realm...";
            if (await viewModel.SelectRealmAsync(choice.Path)) Close();
            else StatusText.Text = "Unable to switch realms right now. Close the game or choose an available realm.";
        }
        catch (Exception) { StatusText.Text = "Unable to load this realm. Check the configuration and try again."; }
        finally { IsEnabled = true; }
    }
}
