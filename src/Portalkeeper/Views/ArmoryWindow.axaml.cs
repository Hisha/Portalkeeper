using Avalonia.Controls;

namespace Portalkeeper.Views;

public partial class ArmoryWindow : Window
{
    public ArmoryWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as Portalkeeper.ViewModels.ArmoryViewModel)?.Dispose();
    }
}
