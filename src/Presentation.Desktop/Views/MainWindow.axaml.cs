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
                vm.IsCompact = Bounds.Width > 0 && Bounds.Width < CompactWidth;
            }
        };

        // Nav bar degrades to short labels / icon-only tools below this width (see MainViewModel.IsCompact).
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel vm) vm.IsCompact = e.NewSize.Width < CompactWidth;
        };
    }

    private const double CompactWidth = 1200;

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
