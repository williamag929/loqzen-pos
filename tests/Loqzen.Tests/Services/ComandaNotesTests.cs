using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Services;

/// <summary>Per-item notes ("sin cebolla"): editing, hold-while-editing, propagation to KDS and receipt.</summary>
public class ComandaNotesTests : ComandaTestBase
{
    private async Task<(Guid comandaId, Guid itemId)> OneBurgerAsync()
    {
        var c = await Service.OpenComandaAsync("Mesa 1", "Ana");
        var dto = await Service.AddItemAsync(c.Id, Burger.Id);
        return (c.Id, dto.Items.Single().Id);
    }

    [Fact]
    public async Task SetNotes_OnPendingItem_IsSaved_Trimmed()
    {
        var (cid, iid) = await OneBurgerAsync();
        var dto = await Service.SetItemNotesAsync(cid, iid, "  sin cebolla  ");
        dto.Items.Single().Notes.Should().Be("sin cebolla");

        (await Service.SetItemNotesAsync(cid, iid, "   ")).Items.Single().Notes.Should().BeNull("blank clears the note");
    }

    [Fact]
    public async Task SetNotes_OnConfirmedItem_Throws()
    {
        var (cid, iid) = await OneBurgerAsync();
        await Service.ConfirmPendingNowAsync(cid);
        var act = () => Service.SetItemNotesAsync(cid, iid, "sin cebolla");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cocina*");
    }

    [Fact]
    public async Task SetNotes_TooLong_Throws()
    {
        var (cid, iid) = await OneBurgerAsync();
        var act = () => Service.SetItemNotesAsync(cid, iid, new string('x', ComandaSettings.MaxNotesLength + 1));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task HeldItem_IsNotAutoConfirmed_EvenAfterDelay()
    {
        var (cid, iid) = await OneBurgerAsync();
        await Service.SetItemHoldAsync(cid, iid, true);

        Clock.Advance(TimeSpan.FromMinutes(5));
        (await Service.ConfirmDueItemsAsync()).Should().Be(0, "cashier is still typing the note");
        (await Db.KitchenOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SavingNotes_ReleasesHold_AndKitchenReceivesItemWithNote()
    {
        var (cid, iid) = await OneBurgerAsync();
        await Service.SetItemHoldAsync(cid, iid, true);
        Clock.Advance(TimeSpan.FromSeconds(45));
        await Service.SetItemNotesAsync(cid, iid, "sin cebolla");

        (await Service.ConfirmDueItemsAsync()).Should().Be(1, "delay already elapsed → goes right away");
        var ko = await Db.KitchenOrders.Include(k => k.Items).SingleAsync();
        ko.Items.Single().Notes.Should().Be("sin cebolla");
    }

    [Fact]
    public async Task SendNow_ConfirmsHeldItems_AndClearsHold()
    {
        var (cid, iid) = await OneBurgerAsync();
        await Service.SetItemHoldAsync(cid, iid, true);
        var dto = await Service.ConfirmPendingNowAsync(cid);
        dto.Items.Single().Status.Should().Be(ComandaItemStatus.Confirmed);
        dto.Items.Single().IsOnHold.Should().BeFalse();
    }

    [Fact]
    public async Task Notes_AppearOnReceiptPreview()
    {
        var (cid, iid) = await OneBurgerAsync();
        var dto = await Service.SetItemNotesAsync(cid, iid, "sin cebolla");
        var text = ReceiptFormatter.ToPlainText(ReceiptFormatter.Format(ReceiptDtoFactory.FromComanda(dto, "Caja"), isPreview: true));
        text.Should().Contain("(sin cebolla)");
    }
}
