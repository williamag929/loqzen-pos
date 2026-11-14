using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Domain.Enums;
using Presentation.Desktop.Localization;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// One KDS card — state &amp; display text. Behaviour (commands, Tick) is in KitchenOrderCardViewModel.Logic.cs.
/// Visual states: new (running border light), urgency colour (green/yellow/red; red blinks when late),
/// print button green until printed then grey, archived (dimmed + countdown bar + undo).
/// </summary>
public partial class KitchenOrderCardViewModel : ObservableObject
{
    public Guid Id { get; }
    public string OrderNumber { get; private set; } = string.Empty;
    public string TableNumber { get; private set; } = string.Empty;
    public string WaiterName { get; private set; } = string.Empty;
    public string Station { get; private set; } = string.Empty;
    public string? GeneralNotes { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public ObservableCollection<KitchenItemViewModel> Items { get; } = new();

    private static string T(string key, params object?[] args) => Localizer.Instance.T(key, args);

    /// <summary>"Atiende: {waiter}" line (KdsView header).</summary>
    public string WaiterLine => T("Kds.Card.Waiter", WaiterName);
    /// <summary>"Nota: {text}" strip; empty when the ticket has no general note.</summary>
    public string GeneralNotesLine => string.IsNullOrWhiteSpace(GeneralNotes) ? string.Empty : T("Kds.Card.Note", GeneralNotes);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ActionButtonText), nameof(IsNew), nameof(IsInPreparation), nameof(IsReady), nameof(IsArchived), nameof(IsActive))]
    private KitchenOrderStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrinted), nameof(PrintTooltip))]
    private DateTime? _printedAt;

    [ObservableProperty] private DateTime? _deliveredAt;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    private int _elapsedMinutes;
    /// <summary>⏱ badge text ("12m").</summary>
    public string ElapsedText => T("Kds.Card.Elapsed", ElapsedMinutes);
    /// <summary>Hex of the current theme's Success/Warning/Danger token (KdsView binds it as BorderBrush / runner colour).</summary>
    [ObservableProperty] private string _urgencyColorHex = ThemePalette.Success;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderOpacity))]
    private bool _isLate;

    /// <summary>Toggled every tick while late → red border blinks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BorderOpacity))]
    private bool _blinkPhase;

    [ObservableProperty] private double _archiveProgress;
    [ObservableProperty] private string _archiveText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Just arrived and nobody pressed PREPARAR yet → running border light.</summary>
    public bool IsNew => Status == KitchenOrderStatus.Pending;
    /// <summary>XAML classes on the advance button: (new) Accent · "inprep" Success · "ready" Purple.</summary>
    public bool IsInPreparation => Status == KitchenOrderStatus.InPreparation;
    public bool IsReady => Status == KitchenOrderStatus.Ready;
    public bool IsArchived => Status == KitchenOrderStatus.Delivered;
    public bool IsActive => !IsArchived;
    public double BorderOpacity => IsLate && BlinkPhase ? 0.25 : 1.0;

    public string StatusText => Status switch
    {
        KitchenOrderStatus.Pending => T("Kds.Status.Pending"),
        KitchenOrderStatus.InPreparation => T("Kds.Status.InPreparation"),
        KitchenOrderStatus.Ready => T("Kds.Status.Ready"),
        KitchenOrderStatus.Delivered => T("Kds.Status.Delivered"),
        _ => T("Kds.Status.Cancelled")
    };

    public string ActionButtonText => Status switch
    {
        KitchenOrderStatus.Pending => T("Kds.Action.Prepare"),
        KitchenOrderStatus.InPreparation => T("Kds.Action.MarkReady"),
        KitchenOrderStatus.Ready => T("Kds.Action.Deliver"),
        _ => T("Kds.Action.Archive")
    };

    /// <summary>Language switch: every text built in code, in place (the card is never recreated). ArchiveText follows on the next tick.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionButtonText));
        OnPropertyChanged(nameof(PrintTooltip));
        OnPropertyChanged(nameof(WaiterLine));
        OnPropertyChanged(nameof(GeneralNotesLine));
        OnPropertyChanged(nameof(ElapsedText));
    }

    /// <summary>Print button: SuccessStrong until printed (attention), then the neutral Border fill (XAML class "printed").</summary>
    public bool IsPrinted => PrintedAt != null;

    public string PrintTooltip => PrintedAt == null
        ? T("Kds.Print.NotPrinted")
        : T("Kds.Print.Reprint", PrintedAt.Value.ToLocalTime().ToString("HH:mm"));
}
