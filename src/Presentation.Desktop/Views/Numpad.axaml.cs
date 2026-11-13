using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Presentation.Desktop.Views;

public partial class Numpad : UserControl
{
    public static readonly StyledProperty<ICommand?> DigitCommandProperty =
        AvaloniaProperty.Register<Numpad, ICommand?>(nameof(DigitCommand));
    public static readonly StyledProperty<ICommand?> ClearCommandProperty =
        AvaloniaProperty.Register<Numpad, ICommand?>(nameof(ClearCommand));
    public static readonly StyledProperty<ICommand?> ExactCommandProperty =
        AvaloniaProperty.Register<Numpad, ICommand?>(nameof(ExactCommand));
    /// <summary>Caption of the big blue key (default "Exacto"; PIN dialogs use "OK" / "Entrar").</summary>
    public static readonly StyledProperty<string> ExactLabelProperty =
        AvaloniaProperty.Register<Numpad, string>(nameof(ExactLabel), "Exacto");

    public ICommand? DigitCommand { get => GetValue(DigitCommandProperty); set => SetValue(DigitCommandProperty, value); }
    public ICommand? ClearCommand { get => GetValue(ClearCommandProperty); set => SetValue(ClearCommandProperty, value); }
    public ICommand? ExactCommand { get => GetValue(ExactCommandProperty); set => SetValue(ExactCommandProperty, value); }
    public string ExactLabel { get => GetValue(ExactLabelProperty); set => SetValue(ExactLabelProperty, value); }

    public Numpad()
    {
        InitializeComponent();
    }
}
