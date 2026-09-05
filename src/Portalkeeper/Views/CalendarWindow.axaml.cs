using Avalonia.Controls;
using Avalonia.Interactivity;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

public partial class CalendarWindow : Window
{
    public CalendarWindow() => InitializeComponent();
    private void Previous_Click(object? sender, RoutedEventArgs e) { if (DataContext is CalendarViewModel vm) vm.PreviousMonth(); }
    private void Next_Click(object? sender, RoutedEventArgs e) { if (DataContext is CalendarViewModel vm) vm.NextMonth(); }
}
