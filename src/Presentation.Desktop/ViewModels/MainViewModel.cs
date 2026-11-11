using CommunityToolkit.Mvvm.ComponentModel;

namespace Presentation.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private CheckoutViewModel _checkout = new();

    public string AppTitle => "Loqzen POS Standalone";
}
