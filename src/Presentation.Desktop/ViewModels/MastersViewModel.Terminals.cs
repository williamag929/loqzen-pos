using Presentation.Desktop.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Server;
using Presentation.Desktop.Localization;

namespace Presentation.Desktop.ViewModels;

/// <summary>Row of the registers list: ● online dot, kind, last seen / last sync, open shift.</summary>
public sealed class TerminalRowViewModel
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    public TerminalRowViewModel(TerminalDto dto, DateTime nowUtc, bool isThisTerminal)
    {
        Dto = dto;
        IsThisTerminal = isThisTerminal;
        IsOnline = isThisTerminal || dto.IsOnline(nowUtc, OnlineWindow);
        LastSeenText = isThisTerminal ? T("Masters.Terminals.ThisRegister") : dto.LastSeenAt is { } s ? T("Masters.Terminals.SeenAgo", Ago(nowUtc - s)) : T("Masters.Terminals.NeverSeen");
        LastSyncText = dto.Kind == TerminalKind.Master ? string.Empty
            : dto.LastSyncAt is { } y ? T("Masters.Terminals.LastSyncAgo", Ago(nowUtc - y)) : T("Masters.Terminals.NoSalesReceived");
    }

    private static string T(string key, params object?[] args) => Localizer.Instance.T(key, args);

    public TerminalDto Dto { get; }
    public bool IsThisTerminal { get; }
    public bool IsOnline { get; }
    public string Code => Dto.Code;
    public string Name => Dto.Name;
    public string KindLabel => Dto.Kind == TerminalKind.Master ? T("Masters.Terminals.Kind.Master") : T("Masters.Terminals.Kind.Satellite");
    public string KindColor => Dto.Kind == TerminalKind.Master ? ThemePalette.Accent : ThemePalette.TextFaint;
    public string DotColor => IsOnline ? ThemePalette.SuccessBright : ThemePalette.TextFaint;
    public string LastSeenText { get; }
    public string LastSyncText { get; }
    public string OpenShiftText => Dto.OpenShiftNumber is { } n
        ? T("Masters.Terminals.OpenShift", n) + (string.IsNullOrWhiteSpace(Dto.OpenShiftCashier) ? string.Empty : $" · {Dto.OpenShiftCashier}")
        : T("Masters.Terminals.NoOpenShift");
    public string OpenShiftColor => Dto.OpenShiftNumber != null ? ThemePalette.SuccessBright : ThemePalette.TextFaint;
    public string DetailText => string.Join(" · ", new[]
    {
        Dto.AppVersion is { Length: > 0 } v ? $"v{v}" : null,
        Dto.LastAddress,
        Dto.IsActive ? null : T("Masters.Terminals.InactiveDetail")
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    internal static string Ago(TimeSpan t) =>
        t.TotalSeconds < 60 ? T("Masters.Terminals.Ago.Seconds", Math.Max(0, (int)t.TotalSeconds))
        : t.TotalMinutes < 60 ? T("Masters.Terminals.Ago.Minutes", (int)t.TotalMinutes)
        : t.TotalHours < 48 ? T("Masters.Terminals.Ago.Hours", (int)t.TotalHours)
        : T("Masters.Terminals.Ago.Days", (int)t.TotalDays);
}

/// <summary>Row of the inbox receipts list (one per envelope received from a satellite).</summary>
public sealed class InboxReceiptRowViewModel
{
    public InboxReceiptRowViewModel(SyncInboxReceiptDto dto) => Dto = dto;

    private static string T(string key) => Localizer.Instance.T(key);

    public SyncInboxReceiptDto Dto { get; }
    public string TimeText => Dto.ReceivedAt.ToLocalTime().ToString("dd/MM HH:mm:ss");
    public string TerminalCode => Dto.TerminalCode;
    public string EventText => Dto.EventType switch
    {
        SyncEventTypes.OrderCompleted => T("Masters.Terminals.Event.Sale"),
        SyncEventTypes.CashShiftOpened => T("Masters.Terminals.Event.ShiftOpened"),
        SyncEventTypes.CashShiftClosed => T("Masters.Terminals.Event.ShiftClosed"),
        SyncEventTypes.CashMovementRecorded => T("Masters.Terminals.Event.CashMovement"),
        SyncEventTypes.BillPaymentRecorded => T("Masters.Terminals.Event.BillPayment"),
        SyncEventTypes.BillPaymentVoided => T("Masters.Terminals.Event.BillPaymentVoided"),
        _ => Dto.EventType
    };
    public string OutcomeText => Dto.Outcome switch
    {
        SyncInboxOutcome.Applied => T("Masters.Terminals.Outcome.Applied"),
        SyncInboxOutcome.Duplicate => T("Masters.Terminals.Outcome.Duplicate"),
        SyncInboxOutcome.Ignored => T("Masters.Terminals.Outcome.Ignored"),
        SyncInboxOutcome.Rejected => T("Masters.Terminals.Outcome.Rejected"),
        _ => Dto.Outcome.ToString()
    };
    public string OutcomeColor => Dto.Outcome switch
    {
        SyncInboxOutcome.Applied => ThemePalette.SuccessBright,
        SyncInboxOutcome.Rejected => ThemePalette.DangerBright,
        _ => ThemePalette.TextMuted
    };
    public string ErrorText => Dto.Error ?? string.Empty;
    public bool HasError => !string.IsNullOrWhiteSpace(Dto.Error);
    public string AggregateText => Dto.AggregateId.ToString("N")[..8];
}

/// <summary>"🖥️ Cajas": registers known to this database, the URL satellites must use and the last inbox receipts.</summary>
public partial class MastersViewModel
{
    public bool IsTerminalsTab => ActiveTab == MastersTab.Terminals;
    public ObservableCollection<TerminalRowViewModel> Terminals { get; } = new();
    public ObservableCollection<InboxReceiptRowViewModel> InboxReceipts { get; } = new();
    public bool HasTerminals => Terminals.Count > 0;
    public bool HasInboxReceipts => InboxReceipts.Count > 0;

    [ObservableProperty] private bool _isLoadingTerminals;

    /// <summary>Satellite counts from the last load (null = not loaded yet); the summary text is derived so it follows the language.</summary>
    private (int Online, int Total)? _satelliteCounts;
    public string TerminalsSummary => _satelliteCounts switch
    {
        null => string.Empty,
        (_, 0) => T("Masters.Terminals.NoSatellites"),
        (var online, var total) => T("Masters.Terminals.Summary", online, total)
    };

    /// <summary>"CAJA-01 · Caja principal · Principal"</summary>
    public string ThisTerminalText
    {
        get
        {
            var code = _terminalContext.IsSet ? _terminalContext.Code : SafeCode(_terminalSettings.Code);
            var name = _terminalContext.IsSet ? _terminalContext.Name : _terminalSettings.Name;
            return T("Masters.Terminals.ThisTerminal", code, name, _terminalSettings.IsSatellite ? T("Masters.Terminals.Kind.Satellite") : T("Masters.Terminals.Kind.Master"));
        }
    }

    /// <summary>URL the satellites must put in Terminal:MasterUrl (this machine's LAN address + server port).</summary>
    public string MasterUrlText => $"http://{LoqzenServer.LanAddress()}:{_serverSettings.Port}";
    public string MasterUrlHint => _serverSettings.Enabled
        ? T("Masters.Terminals.MasterUrlHint")
        : T("Masters.Terminals.ServerDisabled");
    public string MasterUrlHintColor => _serverSettings.Enabled ? ThemePalette.TextMuted : ThemePalette.WarningBright;

    /// <summary>NormalizeCode without throwing on a bad config (the tab still renders).</summary>
    private static string SafeCode(string? code)
    {
        try { return TerminalSettings.NormalizeCode(code); }
        catch { return (code ?? string.Empty).Trim().ToUpperInvariant(); }
    }

    [RelayCommand]
    public void ShowTerminals()
    {
        ActiveTab = MastersTab.Terminals;
        _ = LoadTerminalsAsync();
    }

    /// <summary>"↻ Actualizar": registers + inbox receipts, each in its own scope (never throws to the UI).</summary>
    [RelayCommand]
    public async Task LoadTerminalsAsync()
    {
        if (IsLoadingTerminals) return;
        IsLoadingTerminals = true;
        try
        {
            var now = DateTime.UtcNow;
            var thisCode = _terminalContext.IsSet ? _terminalContext.Code : SafeCode(_terminalSettings.Code);
            var terminals = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ITerminalService>().GetAllAsync();
            });
            Terminals.Clear();
            foreach (var t in terminals)
                Terminals.Add(new TerminalRowViewModel(t, now, string.Equals(t.Code, thisCode, StringComparison.OrdinalIgnoreCase)));
            var satellites = Terminals.Where(t => t.Dto.Kind == TerminalKind.Satellite).ToList();
            _satelliteCounts = (satellites.Count(t => t.IsOnline), satellites.Count);
            OnPropertyChanged(nameof(TerminalsSummary));
        }
        catch (Exception ex)
        {
            Notification = T("Masters.Terminals.LoadError", ex.Message);
        }
        OnPropertyChanged(nameof(HasTerminals));

        try
        {
            var receipts = await Task.Run(async () =>
            {
                using var scope = _scopes.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ISyncInboxService>().GetRecentAsync(50);
            });
            InboxReceipts.Clear();
            foreach (var r in receipts) InboxReceipts.Add(new InboxReceiptRowViewModel(r));
        }
        catch (Exception ex)
        {
            Notification = T("Masters.Terminals.InboxError", ex.Message);
        }
        OnPropertyChanged(nameof(HasInboxReceipts));
        OnPropertyChanged(nameof(ThisTerminalText));
        OnPropertyChanged(nameof(MasterUrlText));
        IsLoadingTerminals = false;
    }

    /// <summary>Language toggle: rows cache translated texts, so re-wrap the same DTOs (no database access).</summary>
    private void RefreshTerminalRowsLanguage()
    {
        if (Terminals.Count > 0)
        {
            var now = DateTime.UtcNow;
            var rows = Terminals.Select(t => (t.Dto, t.IsThisTerminal)).ToList();
            Terminals.Clear();
            foreach (var (dto, mine) in rows) Terminals.Add(new TerminalRowViewModel(dto, now, mine));
        }
        if (InboxReceipts.Count > 0)
        {
            var dtos = InboxReceipts.Select(r => r.Dto).ToList();
            InboxReceipts.Clear();
            foreach (var d in dtos) InboxReceipts.Add(new InboxReceiptRowViewModel(d));
        }
    }
}

