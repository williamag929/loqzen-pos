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

public enum ComandaStatus
{
    Open = 0,        // Atendiendo mesa (se pueden agregar items)
    ReadyToPay = 1,  // Lista para pago (panel derecho)
    Closed = 2,      // Pagada / facturada
    Cancelled = 3
}

public enum ComandaItemStatus
{
    Pending = 0,     // Amarillo: en ventana de espera, editable/cancelable
    Confirmed = 1,   // Verde: confirmado y enviado a cocina (KDS)
    Cancelled = 2
}

public enum KitchenOrderStatus
{
    Pending = 0,        // Pendiente por iniciar
    InPreparation = 1,  // En preparación
    Ready = 2,          // Listo para entrega
    Delivered = 3,      // Entregado a mesa
    Cancelled = 4       // Cancelado
}

/// <summary>Ordered by privilege: a higher value can do everything a lower one can.</summary>
public enum UserRole
{
    Kitchen = 0,   // KDS only
    Waiter = 1,    // Comandas + KDS
    Cashier = 2,   // + Punto de venta + Turno de caja
    Manager = 3,   // + Maestros, autoriza devoluciones/descuentos/anulaciones
    Admin = 4      // + gestiona usuarios
}

/// <summary>Sensitive actions that can be configured to require a manager PIN.</summary>
public enum AuthorizedAction
{
    ReturnItem = 0,        // Devolución en carrito (nota crédito)
    ApplyDiscount = 1,     // Descuento sobre la venta
    CancelKitchenItem = 2, // Anular un producto que ya está en cocina
    OpenCashDrawer = 3,    // Abrir cajón sin venta
    CloseShift = 4         // Cerrar turno / arqueo
}
