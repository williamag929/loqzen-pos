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
            }
        };
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
