using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Enums;

namespace Core.Application.Services;

/// <summary>
/// Sales service interface for checkout operations.
/// Encapsulates order lifecycle: creation, item management, discounts, and payment processing.
/// </summary>
public interface ISalesService
{
    /// <summary>
    /// Creates a new draft order associated with a cash shift.
    /// </summary>
    /// <param name="customerId">Optional customer ID for the order.</param>
    /// <param name="cashShiftId">The cash shift this order belongs to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created order DTO.</returns>
    Task<OrderDto> CreateOrderAsync(Guid? customerId, Guid cashShiftId, CancellationToken ct = default);

    /// <summary>
    /// Adds an item to an existing order. Calculates line totals including tax.
    /// </summary>
    /// <param name="orderId">The order ID to add the item to.</param>
    /// <param name="productId">The product ID to add.</param>
    /// <param name="quantity">Quantity to add (units or base quantity for weighed items).</param>
    /// <param name="weightKg">Optional weight in kg for weighed products.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated order DTO.</returns>
    Task<OrderDto> AddItemAsync(Guid orderId, Guid productId, decimal quantity, decimal? weightKg = null, CancellationToken ct = default);

    /// <summary>
    /// Removes an item from an order.
    /// </summary>
    /// <param name="orderId">The order ID.</param>
    /// <param name="itemId">The item ID to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated order DTO.</returns>
    Task<OrderDto> RemoveItemAsync(Guid orderId, Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// Updates the quantity of an existing order item.
    /// </summary>
    /// <param name="orderId">The order ID.</param>
    /// <param name="itemId">The item ID to update.</param>
    /// <param name="newQuantity">The new quantity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated order DTO.</returns>
    Task<OrderDto> UpdateItemQuantityAsync(Guid orderId, Guid itemId, decimal newQuantity, CancellationToken ct = default);

    /// <summary>
    /// Applies a discount amount to the order.
    /// </summary>
    /// <param name="orderId">The order ID.</param>
    /// <param name="discountAmount">The discount amount to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated order DTO.</returns>
    Task<OrderDto> ApplyDiscountAsync(Guid orderId, decimal discountAmount, CancellationToken ct = default);

    /// <summary>
    /// Applies a discount to the order using DiscountDto (percentage or fixed amount).
    /// </summary>
    /// <param name="orderId">The order ID.</param>
    /// <param name="discount">The discount information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated order DTO.</returns>
    Task<OrderDto> ApplyDiscountAsync(Guid orderId, DiscountDto discount, CancellationToken ct = default);

    /// <summary>
    /// Processes payment for an order. Updates order status, creates payment record, 
    /// and writes OutboxMessage in the same transaction.
    /// </summary>
    /// <param name="orderId">The order ID to process payment for.</param>
    /// <param name="method">Payment method (Cash, Card, Transfer, etc.).</param>
    /// <param name="amount">The payment amount.</param>
    /// <param name="tenderedAmount">Amount tendered by customer (for cash change calculation).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Payment result with success status and change amount.</returns>
    Task<PaymentResultDto> ProcessPaymentAsync(Guid orderId, PaymentMethod method, decimal amount, decimal tenderedAmount, CancellationToken ct = default);

    /// <summary>
    /// Gets the current draft order for a cash shift, or null if none exists.
    /// </summary>
    /// <param name="cashShiftId">The cash shift ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current draft order DTO or null.</returns>
    Task<OrderDto?> GetCurrentOrderAsync(Guid cashShiftId, CancellationToken ct = default);

    /// <summary>
    /// Gets an order by its ID.
    /// </summary>
    /// <param name="orderId">The order ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The order DTO or null if not found.</returns>
    Task<OrderDto?> GetByIdAsync(Guid orderId, CancellationToken ct = default);
}
