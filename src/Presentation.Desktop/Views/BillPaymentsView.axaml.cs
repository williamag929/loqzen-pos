using Avalonia.Controls;

namespace Presentation.Desktop.Views;

public partial class BillPaymentsView : UserControl
{
    /// <summary>Below this width the 3-column layout no longer fits (half-screen tiling, 15" terminals).</summary>
    private const double CompactWidth = 1250;
    /// <summary>Below this the numpad also moves under the amount/payment block.</summary>
    private const double NarrowWidth = 950;

    public BillPaymentsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
    }

    /// <summary>
    /// Pure layout (no business state): wide = billers | form | today's payments;
    /// compact = billers | form, payments list below; narrow = numpad stacked under the form fields.
    /// </summary>
    private void ApplyLayout(double width)
    {
        var compact = width < CompactWidth;
        var narrow = width < NarrowWidth;

        MainGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 200 : 260);
        MainGrid.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(380);
        Grid.SetColumn(PaymentsPanel, compact ? 0 : 2);
        Grid.SetColumnSpan(PaymentsPanel, compact ? 2 : 1);
        Grid.SetRow(PaymentsPanel, compact ? 1 : 0);
        PaymentsPanel.MaxHeight = compact ? 240 : double.PositiveInfinity;
        PaymentsPanel.Margin = compact ? new Avalonia.Thickness(0, 12, 0, 0) : default;
        Grid.SetRowSpan(BillersPanel, 1);

        FormGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(330);
        Grid.SetColumn(NumpadPanel, narrow ? 0 : 1);
        Grid.SetRow(NumpadPanel, narrow ? 1 : 0);
        NumpadPanel.Margin = narrow ? new Avalonia.Thickness(0, 12, 0, 0) : default;
        AmountPanel.Margin = narrow ? default : new Avalonia.Thickness(0, 0, 12, 0);
    }
}
