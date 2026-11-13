using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Presentation.Desktop.ViewModels;

namespace Presentation.Desktop.Views;

/// <summary>
/// Pure view concern: translates a horizontal swipe into ViewModel navigation.
/// Swipe left (finger moves right→left) = next panel; swipe right = previous.
/// Handled on the tunnel phase so it works over buttons/scrollers too, but only fires for clearly
/// horizontal gestures (so taps and vertical scrolling are untouched).
/// </summary>
public partial class ComandaView : UserControl
{
    private const double MinSwipeDistance = 120;
    private Point? _start;

    public ComandaView()
    {
        InitializeComponent();
        var area = this.FindControl<Panel>("SwipeArea")!;
        area.AddHandler(PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        area.AddHandler(PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e) => _start = e.GetPosition(this);

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_start is not { } start || DataContext is not ComandaViewModel vm) return;
        _start = null;

        var delta = e.GetPosition(this) - start;
        if (System.Math.Abs(delta.X) < MinSwipeDistance || System.Math.Abs(delta.X) < 2 * System.Math.Abs(delta.Y)) return;

        vm.Swipe(towardsRight: delta.X < 0);
        e.Handled = true; // don't also "click" the button where the finger lifted
    }
}
