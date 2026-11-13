using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Per-item notes editor ("sin cebolla"). Opening it puts the item ON HOLD so the confirmation
/// timer can't send it to the kitchen without the note; Save/Cancel release the hold.
/// </summary>
public partial class ComandaViewModel
{
    private static readonly string[] DefaultQuickNotes =
    {
        "Sin cebolla", "Sin tomate", "Sin salsas", "Sin hielo", "Poco picante", "Extra queso",
        "Bien cocido", "Término medio", "Para llevar", "Alergia: maní", "Sin gluten", "Aparte"
    };

    public ObservableCollection<QuickNoteChipViewModel> QuickNoteChips { get; } = new();

    [ObservableProperty] private bool _isNotesEditorOpen;
    [ObservableProperty] private string _notesEditorTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotesCounter))]
    private string _notesText = string.Empty;

    public string NotesCounter => $"{NotesText.Length}/{ComandaSettings.MaxNotesLength}";
    public int MaxNotesLength => ComandaSettings.MaxNotesLength;

    private Guid? _editingItemId;

    private IEnumerable<string> QuickNotesSource =>
        _settings.QuickNotes is { Count: > 0 } configured ? configured : DefaultQuickNotes;

    private async void OpenNotes(ComandaItemRowViewModel row)
    {
        if (Current == null) return;
        if (!row.IsPending)
        {
            StatusMessage = "⚠️ Ya está en cocina; la nota ya no se puede cambiar.";
            return;
        }

        var comandaId = Current.Id;
        var held = false;
        await RunAsync(async svc =>
        {
            Apply(await svc.SetItemHoldAsync(comandaId, row.Dto.Id, true));
            held = true;
        });
        if (!held) return; // e.g. it was confirmed a moment ago — RunAsync showed why

        _editingItemId = row.Dto.Id;
        NotesEditorTitle = $"📝 Nota para: {row.Name}";
        NotesText = row.Dto.Notes ?? string.Empty;
        RebuildChips();
        IsNotesEditorOpen = true;
    }

    private void RebuildChips()
    {
        var active = SplitNotes(NotesText);
        QuickNoteChips.Clear();
        foreach (var text in QuickNotesSource)
            QuickNoteChips.Add(new QuickNoteChipViewModel(text, active.Contains(text, StringComparer.OrdinalIgnoreCase), ToggleChip));
    }

    /// <summary>Chips add/remove their text as a comma-separated part, preserving any free text.</summary>
    private void ToggleChip(QuickNoteChipViewModel chip)
    {
        var parts = SplitNotes(NotesText);
        var existing = parts.FindIndex(p => p.Equals(chip.Text, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) parts.RemoveAt(existing); else parts.Add(chip.Text);

        var joined = string.Join(", ", parts);
        if (joined.Length > ComandaSettings.MaxNotesLength)
        {
            StatusMessage = $"⚠️ La nota no puede superar {ComandaSettings.MaxNotesLength} caracteres.";
            return;
        }
        NotesText = joined;
        RebuildChips();
    }

    /// <summary>Keeps chip highlight in sync when the cashier types/deletes free text.</summary>
    partial void OnNotesTextChanged(string value)
    {
        var active = SplitNotes(value);
        foreach (var chip in QuickNoteChips)
            chip.IsActive = active.Contains(chip.Text, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> SplitNotes(string? text) =>
        (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    [RelayCommand]
    private void ClearNotes() => NotesText = string.Empty;

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        if (Current == null || _editingItemId is not { } itemId) { CloseEditor(); return; }
        var comandaId = Current.Id;
        var text = NotesText;
        var saved = false;
        await RunAsync(async svc =>
        {
            Apply(await svc.SetItemNotesAsync(comandaId, itemId, text));
            saved = true;
        });
        if (!saved) return; // keep editor open so the text isn't lost; message already shown

        CloseEditor();
        StatusMessage = string.IsNullOrWhiteSpace(text) ? "Nota eliminada" : $"📝 Nota guardada: {text.Trim()}";
    }

    [RelayCommand]
    private async Task CancelNotesAsync()
    {
        var itemId = _editingItemId;
        CloseEditor();
        if (Current == null || itemId == null) return;
        var comandaId = Current.Id;
        // Release the hold without changing the note. Ignore "already in kitchen" here.
        await RunAsync(async svc => Apply(await svc.SetItemHoldAsync(comandaId, itemId.Value, false)));
    }

    private void CloseEditor()
    {
        IsNotesEditorOpen = false;
        _editingItemId = null;
    }
}

public sealed partial class QuickNoteChipViewModel : ObservableObject
{
    public QuickNoteChipViewModel(string text, bool isActive, Action<QuickNoteChipViewModel> onToggle)
    {
        Text = text;
        _isActive = isActive;
        ToggleCommand = new RelayCommand(() => onToggle(this));
    }

    public string Text { get; }
    public IRelayCommand ToggleCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Background), nameof(Label))]
    private bool _isActive;

    public Avalonia.Media.IBrush Background => UiBrush.Parse(IsActive ? "#CA8A04" : "#1E293B");
    public string Label => IsActive ? $"✓ {Text}" : Text;
}
