using Avalonia.Controls;
using Presentation.Desktop.ViewModels;

namespace Presentation.Desktop.Views;

public partial class CheckoutView : UserControl
{
    public CheckoutView()
    {
        InitializeComponent();
        // Narrow windows (half-screen tiling, small monitors): hide the receipt column so the view
        // never overflows the window and the payment modal stays fully visible.
        SizeChanged += (_, e) => (DataContext as CheckoutViewModel)?.ApplyViewportWidth(e.NewSize.Width);
    }
}
