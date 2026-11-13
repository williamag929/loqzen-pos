using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Enums;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// One line of the current comanda. Pending = yellow with countdown (can be cancelled);
/// Confirmed = green (already in kitchen; cancelling it needs a manager PIN).
/// </summary>
public sealed partial class ComandaItemRowViewModel : ObservableObject
{
    private readonly int _delaySeconds;

    public ComandaItemRowViewModel(ComandaItemDto dto, int delaySeconds, DateTime utcNow, Action<ComandaItemRowViewModel> onCancel, Action<ComandaItemRowViewModel> onEditNotes)
    {
        Dto = dto;
        _delaySeconds = delaySeconds;
        CancelCommand = new RelayCommand(() => onCancel(this));
        EditNotesCommand = new RelayCommand(() => onEditNotes(this));
        Tick(utcNow);
    }

    public ComandaItemDto Dto { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand EditNotesCommand { get; }
    public bool IsOnHold => Dto.IsOnHold;
    public string NotesButtonText => HasNotes ? "📝 Editar nota" : "📝 Nota";

    public string Name => Dto.ProductName;
    public string QuantityText => Dto.WeightKg is { } kg ? $"{kg:0.###} kg" : $"{Dto.Quantity:0.##} x";
    public string TotalText => ReceiptFormatter.Cop(Dto.Total);
    public string? Notes => Dto.Notes;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Dto.Notes);
    public string StationText => Dto.Station;

    public bool IsPending => Dto.Status == ComandaItemStatus.Pending;
    public bool IsConfirmed => Dto.Status == ComandaItemStatus.Confirmed;
    public bool IsCancelled => Dto.Status == ComandaItemStatus.Cancelled;

    public IBrush Background => UiBrush.Parse(Dto.Status switch
    {
        ComandaItemStatus.Pending => "#713F12",    // amarillo oscuro
        ComandaItemStatus.Confirmed => "#14532D",  // verde
        _ => "#1E293B"
    });

    public IBrush Accent => UiBrush.Parse(Dto.Status switch
    {
        ComandaItemStatus.Pending => "#FACC15",
        ComandaItemStatus.Confirmed => "#22C55E",
        _ => "#64748B"
    });

    [ObservableProperty] private string _stateText = string.Empty;

    /// <summary>0..1 progress of the confirmation window (for the countdown bar).</summary>
    [ObservableProperty] private double _progress;

    public void Tick(DateTime utcNow)
    {
        switch (Dto.Status)
        {
            case ComandaItemStatus.Pending when Dto.IsOnHold:
                StateText = "✋ en pausa (editando nota)";
                Progress = 0;
                break;
            case ComandaItemStatus.Pending:
                var elapsed = (utcNow - Dto.AddedAt).TotalSeconds;
                var left = Math.Max(0, (int)Math.Ceiling(_delaySeconds - elapsed));
                StateText = left > 0 ? $"⏳ {left}s" : "⏳ enviando…";
                Progress = _delaySeconds <= 0 ? 1 : Math.Clamp(elapsed / _delaySeconds, 0, 1);
                break;
            case ComandaItemStatus.Confirmed:
                StateText = "🟢 En cocina";
                Progress = 1;
                break;
            default:
                StateText = "Anulado";
                Progress = 0;
                break;
        }
    }
}
