using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Domain.Entities;
using Core.Domain.Enums;

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
    public ObservableCollection<KitchenOrderItem> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ActionButtonText), nameof(ActionButtonColorHex), nameof(IsNew), nameof(IsArchived), nameof(IsActive))]
    private KitchenOrderStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrintButtonColorHex), nameof(PrintTooltip))]
    private DateTime? _printedAt;

    [ObservableProperty] private DateTime? _deliveredAt;
    [ObservableProperty] private int _elapsedMinutes;
    [ObservableProperty] private string _urgencyColorHex = "#10B981";

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
    public bool IsArchived => Status == KitchenOrderStatus.Delivered;
    public bool IsActive => !IsArchived;
    public double BorderOpacity => IsLate && BlinkPhase ? 0.25 : 1.0;

    public string StatusText => Status switch
    {
        KitchenOrderStatus.Pending => "PENDIENTE",
        KitchenOrderStatus.InPreparation => "EN PREPARACIÓN",
        KitchenOrderStatus.Ready => "LISTO",
        KitchenOrderStatus.Delivered => "ENTREGADO",
        _ => "CANCELADO"
    };

    public string ActionButtonText => Status switch
    {
        KitchenOrderStatus.Pending => "👨‍🍳 PREPARAR",
        KitchenOrderStatus.InPreparation => "🔔 MARCAR LISTO",
        KitchenOrderStatus.Ready => "✔️ ENTREGAR",
        _ => "ARCHIVAR"
    };

    public string ActionButtonColorHex => Status switch
    {
        KitchenOrderStatus.Pending => "#3B82F6",
        KitchenOrderStatus.InPreparation => "#10B981",
        KitchenOrderStatus.Ready => "#6366F1",
        _ => "#64748B"
    };

    /// <summary>Green = never printed (attention), grey = already printed.</summary>
    public string PrintButtonColorHex => PrintedAt == null ? "#059669" : "#334155";

    public string PrintTooltip => PrintedAt == null
        ? "Imprimir comanda (aún no impresa)"
        : $"Reimprimir (impresa {PrintedAt.Value.ToLocalTime():HH:mm})";
}
