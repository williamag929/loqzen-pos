namespace Core.Domain.Enums;

public enum OrderStatus
{
    Draft = 0,
    Pending = 1,
    Completed = 2,
    Cancelled = 3,
    Refunded = 4
}

public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    Transfer = 2,
    Other = 3
}

public enum CashShiftStatus
{
    Open = 0,
    Closed = 1
}

public enum SyncStatus
{
    Pending = 0,
    Synced = 1,
    Failed = 2
}

public enum KitchenOrderStatus
{
    Pending = 0,        // Pendiente por iniciar
    InPreparation = 1,  // En preparación
    Ready = 2,          // Listo para entrega
    Delivered = 3,      // Entregado a mesa
    Cancelled = 4       // Cancelado
}
