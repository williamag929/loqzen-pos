using Avalonia.Controls;

namespace Presentation.Desktop.Views;

public partial class DayConsolidationView : UserControl
{
    /// <summary>Below this width the 9-column table squashes the amounts: switch to two-line cards.</summary>
    private const double CompactWidth = 1000;

    public DayConsolidationView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
    }

    /// <summary>Pure layout (no business state).</summary>
    private void ApplyLayout(double width)
    {
        var compact = width < CompactWidth;
        WideHeader.IsVisible = !compact;
        WideTable.IsVisible = !compact;
        CompactList.IsVisible = compact;
    }
}
