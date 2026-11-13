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
        LastSeenText = isThisTerminal ? "esta caja" : dto.LastSeenAt is { } s ? $"visto hace {Ago(nowUtc - s)}" : "nunca visto";
        LastSyncText = dto.Kind == TerminalKind.Master ? string.Empty
            : dto.LastSyncAt is { } y ? $"última sync hace {Ago(nowUtc - y)}" : "sin ventas recibidas";
    }

    public TerminalDto Dto { get; }
    public bool IsThisTerminal { get; }
    public bool IsOnline { get; }
    public string Code => Dto.Code;
    public string Name => Dto.Name;
    public string KindLabel => Dto.KindLabel;
    public string KindColor => Dto.Kind == TerminalKind.Master ? "#2563EB" : "#475569";
    public string DotColor => IsOnline ? "#34D399" : "#64748B";
    public string LastSeenText { get; }
    public string LastSyncText { get; }
    public string OpenShiftText => Dto.OpenShiftNumber is { } n
        ? $"🟢 Turno #{n}" + (string.IsNullOrWhiteSpace(Dto.OpenShiftCashier) ? string.Empty : $" · {Dto.OpenShiftCashier}")
        : "Sin turno abierto";
    public string OpenShiftColor => Dto.OpenShiftNumber != null ? "#A7F3D0" : "#64748B";
    public string DetailText => string.Join(" · ", new[]
    {
        Dto.AppVersion is { Length: > 0 } v ? $"v{v}" : null,
        Dto.LastAddress,
        Dto.IsActive ? null : "inactiva"
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    internal static string Ago(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)} s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min"
        : t.TotalHours < 48 ? $"{(int)t.TotalHours} h"
        : $"{(int)t.TotalDays} días";
}

/// <summary>Row of the inbox receipts list (one per envelope received from a satellite).</summary>
public sealed class InboxReceiptRowViewModel
{
    public InboxReceiptRowViewModel(SyncInboxReceiptDto dto) => Dto = dto;

    public SyncInboxReceiptDto Dto { get; }
    public string TimeText => Dto.ReceivedAt.ToLocalTime().ToString("dd/MM HH:mm:ss");
    public string TerminalCode => Dto.TerminalCode;
    public string EventText => Dto.EventType switch
    {
        SyncEventTypes.OrderCompleted => "Venta",
        SyncEventTypes.CashShiftOpened => "Apertura de turno",
        SyncEventTypes.CashShiftClosed => "Cierre de turno",
        SyncEventTypes.CashMovementRecorded => "Movimiento de caja",
        SyncEventTypes.BillPaymentRecorded => "Recaudo",
        SyncEventTypes.BillPaymentVoided => "Anulación de recaudo",
        _ => Dto.EventType
    };
    public string OutcomeText => Dto.Outcome switch
    {
        SyncInboxOutcome.Applied => "✔ Aplicado",
        SyncInboxOutcome.Duplicate => "Duplicado",
        SyncInboxOutcome.Ignored => "Ignorado",
        SyncInboxOutcome.Rejected => "✖ Rechazado",
        _ => Dto.Outcome.ToString()
    };
    public string OutcomeColor => Dto.Outcome switch
    {
        SyncInboxOutcome.Applied => "#34D399",
        SyncInboxOutcome.Rejected => "#F87171",
        _ => "#94A3B8"
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
    [ObservableProperty] private string _terminalsSummary = string.Empty;

    /// <summary>"CAJA-01 · Caja principal · Principal"</summary>
    public string ThisTerminalText
    {
        get
        {
            var code = _terminalContext.IsSet ? _terminalContext.Code : SafeCode(_terminalSettings.Code);
            var name = _terminalContext.IsSet ? _terminalContext.Name : _terminalSettings.Name;
            return $"🖥️ Esta caja: {code} · {name} · {(_terminalSettings.IsSatellite ? "Satélite" : "Principal")}";
        }
    }

    /// <summary>URL the satellites must put in Terminal:MasterUrl (this machine's LAN address + server port).</summary>
    public string MasterUrlText => $"http://{LoqzenServer.LanAddress()}:{_serverSettings.Port}";
    public string MasterUrlHint => _serverSettings.Enabled
        ? "Escriba esta dirección en Terminal:MasterUrl del appsettings.json de cada caja satélite."
        : "⚠️ El servidor LAN está desactivado (Server:Enabled=false): las cajas satélite no podrán conectarse.";
    public string MasterUrlHintColor => _serverSettings.Enabled ? "#94A3B8" : "#FBBF24";

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
            TerminalsSummary = satellites.Count == 0
                ? "Aún no hay cajas satélite registradas: aparecerán aquí con su primer latido."
                : $"{satellites.Count(t => t.IsOnline)} de {satellites.Count} satélites en línea (vistas en los últimos 90 s)";
        }
        catch (Exception ex)
        {
            Notification = $"❌ Cajas: {ex.Message}";
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
            Notification = $"❌ Recepciones: {ex.Message}";
        }
        OnPropertyChanged(nameof(HasInboxReceipts));
        OnPropertyChanged(nameof(ThisTerminalText));
        OnPropertyChanged(nameof(MasterUrlText));
        IsLoadingTerminals = false;
    }
}

