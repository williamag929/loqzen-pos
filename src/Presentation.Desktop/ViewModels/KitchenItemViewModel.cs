using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Domain.Entities;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// One line of a KDS card. Observable so a cancellation (manager override) or a note change shows
/// up on the next poll without recreating the card (which would restart its animations).
/// </summary>
public sealed partial class KitchenItemViewModel : ObservableObject
{
    public KitchenItemViewModel(KitchenOrderItem entity)
    {
        Id = entity.Id;
        ProductId = entity.ProductId;
        ProductName = entity.ProductName;
        Quantity = entity.Quantity;
        Apply(entity);
    }

    public Guid Id { get; }
    public Guid ProductId { get; }
    public string ProductName { get; }
    public decimal Quantity { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    private string? _notes;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>Cancelled after reaching the kitchen: struck-through + "ANULADO" badge.</summary>
    [ObservableProperty] private bool _isCancelled;

    public void Apply(KitchenOrderItem e)
    {
        Notes = e.Notes;
        IsCancelled = e.IsCancelled;
    }
}
