using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Presentation.Desktop.Views;

/// <summary>
/// Reusable receipt preview. Bind <see cref="Lines"/> to an ObservableCollection&lt;ReceiptLine&gt;.
/// Auto-scrolls to the bottom when lines change so the cashier always sees the latest item + total.
/// </summary>
public partial class ReceiptPreviewControl : UserControl
{
    public static readonly StyledProperty<IEnumerable?> LinesProperty =
        AvaloniaProperty.Register<ReceiptPreviewControl, IEnumerable?>(nameof(Lines));

    public IEnumerable? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public ReceiptPreviewControl()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LinesProperty) return;

        if (change.OldValue is INotifyCollectionChanged oldCol) oldCol.CollectionChanged -= OnLinesChanged;
        if (change.NewValue is INotifyCollectionChanged newCol) newCol.CollectionChanged += OnLinesChanged;
        this.FindControl<ItemsControl>("LinesHost")!.ItemsSource = Lines;
        ScrollToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("Scroller")?.ScrollToEnd(), DispatcherPriority.Background);
}
