using Avalonia.Controls;
using Presentation.Desktop.ViewModels;

namespace Presentation.Desktop.Views;

public partial class MainWindow : Window
{
    private CustomerDisplayWindow? _customerDisplayWindow;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (sender, args) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.RequestOpenCustomerDisplayWindow = OpenCustomerDisplay;
                vm.RequestCopyKdsUrl = CopyToClipboard;
                vm.SetWidth(Bounds.Width);
            }
        };

        // Nav bar width tiers (short tabs / collapsed "⋯" tools / brand subtitle): see MainViewModel.SetWidth.
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel vm) vm.SetWidth(e.NewSize.Width);
        };
    }

    private void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        _ = clipboard.SetTextAsync(text);
    }

    private void OpenCustomerDisplay()
    {
        if (DataContext is not MainViewModel vm) return;

        if (_customerDisplayWindow == null || !_customerDisplayWindow.IsVisible)
        {
            _customerDisplayWindow = new CustomerDisplayWindow
            {
                DataContext = vm.CustomerDisplay
            };
            _customerDisplayWindow.Closed += (s, e) => _customerDisplayWindow = null;
            _customerDisplayWindow.Show();
        }
        else
        {
            _customerDisplayWindow.Activate();
        }
    }
}
