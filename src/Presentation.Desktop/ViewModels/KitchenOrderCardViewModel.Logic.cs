using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Core.Application.Kitchen;
using Core.Domain.Entities;

namespace Presentation.Desktop.ViewModels;

public partial class KitchenOrderCardViewModel
{
    private readonly KdsSettings _settings;
    private readonly Func<KitchenOrderCardViewModel, Task> _advance;
    private readonly Func<KitchenOrderCardViewModel, Task> _print;
    private readonly Func<KitchenOrderCardViewModel, Task> _undo;

    public KitchenOrderCardViewModel(KitchenOrder entity, KdsSettings settings, DateTime utcNow,
        Func<KitchenOrderCardViewModel, Task> advance,
        Func<KitchenOrderCardViewModel, Task> print,
        Func<KitchenOrderCardViewModel, Task> undo)
    {
        _settings = settings;
        _advance = advance;
        _print = print;
        _undo = undo;
        Id = entity.Id;
        Apply(entity);
        AdvanceStatusCommand = new AsyncRelayCommand(() => RunBusy(_advance));
        PrintTicketCommand = new AsyncRelayCommand(() => RunBusy(_print));
        UndoArchiveCommand = new AsyncRelayCommand(() => RunBusy(_undo));
        Tick(utcNow);
    }

    public IAsyncRelayCommand AdvanceStatusCommand { get; }
    public IAsyncRelayCommand PrintTicketCommand { get; }
    public IAsyncRelayCommand UndoArchiveCommand { get; }

    private async Task RunBusy(Func<KitchenOrderCardViewModel, Task> action)
    {
        if (IsBusy) return; // ignore double taps on a touch screen
        IsBusy = true;
        try { await action(this); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Refreshes persisted fields IN PLACE so the card (and its animation) is not recreated.
    /// Items are merged by Id (update IsCancelled/Notes, add new, drop missing) for the same reason.
    /// </summary>
    public void Apply(KitchenOrder e)
    {
        OrderNumber = e.OrderNumber;
        TableNumber = e.TableNumber;
        WaiterName = e.WaiterName;
        Station = e.Station;
        GeneralNotes = e.GeneralNotes;
        CreatedAt = e.CreatedAt;
        Status = e.Status;
        PrintedAt = e.PrintedAt;
        DeliveredAt = e.DeliveredAt;
        MergeItems(e);
    }

    private void MergeItems(KitchenOrder e)
    {
        var incoming = e.Items ?? Array.Empty<KitchenOrderItem>();
        var incomingIds = new HashSet<Guid>(incoming.Select(i => i.Id));

        for (var i = Items.Count - 1; i >= 0; i--)
            if (!incomingIds.Contains(Items[i].Id)) Items.RemoveAt(i);

        foreach (var item in incoming)
        {
            var existing = Items.FirstOrDefault(x => x.Id == item.Id);
            if (existing != null) existing.Apply(item);
            else Items.Add(new KitchenItemViewModel(item));
        }
    }

    /// <returns>false when an archived card's undo window has expired (caller removes it).</returns>
    public bool Tick(DateTime utcNow)
    {
        ElapsedMinutes = (int)Math.Max(0, (utcNow - CreatedAt).TotalMinutes);
        var urgency = KdsTiming.Urgency(CreatedAt, utcNow, _settings);
        UrgencyColorHex = urgency switch
        {
            KdsUrgency.OnTime => "#10B981",
            KdsUrgency.Warning => "#F59E0B",
            _ => "#EF4444"
        };
        IsLate = urgency == KdsUrgency.Late && !IsArchived;
        BlinkPhase = IsLate && !BlinkPhase;

        if (!IsArchived || DeliveredAt is not { } delivered) return true;

        var left = KdsTiming.ArchiveRemainingSeconds(delivered, utcNow, _settings);
        ArchiveProgress = _settings.ArchiveDelaySeconds <= 0 ? 0 : left / _settings.ArchiveDelaySeconds;
        ArchiveText = $"Archivando en {Math.Ceiling(left)}s";
        return left > 0;
    }
}
