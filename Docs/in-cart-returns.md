# In-Cart Return System (Credit Notes)

## Overview
Sistema de devolución de productos durante la venta activa. Permite ajustar el ticket antes de finalizar el pago, emitiendo nota de crédito en el mismo recibo.

## Fase 1: Funcionalidad Básica ✅

### Flujo de Usuario
1. Cajero agrega productos al carrito normalmente
2. Cliente decide devolver un producto antes de pagar
3. Cajero presiona botón **"🔄"** en el item del carrito
4. Sistema valida que no se devuelva más de lo agregado
5. Aparece item de devolución con fondo rojo en carrito
6. Al finalizar venta, recibo imprime sección "*** NOTA CREDITO ***"
7. Total final refleja descuento de la devolución

### Validaciones
```csharp
// Evita devolver más de lo agregado en esta venta
var normalQty = CartItems.Where(i => i.ProductId == productId && !i.IsReturn).Sum(...)
var returnedQty = CartItems.Where(i => i.ProductId == productId && i.IsReturn).Sum(...)

if (returnedQty >= normalQty) {
    StatusMessage = "❌ No puede devolver más de lo agregado en esta venta";
    return;
}
```

### Ejemplo de Recibo
```
LOQZEN POS
Local Principal
NIT 900.000.000-1
--------------------------------
Orden: ORD-20260112-0001
Fecha: 2026-01-12 15:30
Cajero: Principal
--------------------------------
   2 Coca-Cola 350ml     6.000
   1 Pan Integral       12.000
--------------------------------

*** NOTA CREDITO ***
  -1 DEVOL: Coca-Cola   -3.000
     (Ajuste en venta)
--------------------------------
Subtotal:              $15.000
IVA (19%):              $2.850
Total:                 $17.850
--------------------------------
Pago: Efectivo
Recibido:              $20.000
Cambio:                 $2.150
--------------------------------
```

### UI - Checkout View
**Item Normal:**
- Fondo: \`#0F172A\` (azul oscuro)
- Borde: \`#1E293B\`
- Controles: +/- para cantidad
- Botón eliminar: 🔄 (ahora es "devolver")

**Item Devolución:**
- Fondo: \`#450A0A\` (rojo oscuro)
- Borde: \`#991B1B\` (rojo fuerte)
- Label: **"🔄 DEVOLUCIÓN: [nombre]"**
- Subtítulo: *(Nota crédito - ajuste en venta)*
- Color texto: \`#FCA5A5\` (rojo claro)
- Sin controles +/-, solo botón ✕ para quitar

### UI - Customer Display
**Item Normal:**
- Fondo: \`#0B132B\`
- Borde: \`#3A506B\`

**Item Devolución:**
- Fondo: \`#450A0A\`
- Borde: \`#991B1B\`
- Label: **"⚠️ DEVOLUCIÓN: [nombre]"**
- Subtítulo: *(Nota crédito en esta venta)*
- Color texto: \`#FCA5A5\`

## Fase 2: Autorización de Gerente (TODO)

### Pendiente de Implementar:
1. **Modal de PIN Manager**
2. **Entidad ManagerOverride**
3. **Configuración en appsettings.json**
4. **Audit Trail**

## Archivos Modificados (Fase 1)

| Archivo | Cambios |
|---------|------|
| \`Core.Application/DTOs/ReceiptDto.cs\` | Agregado \`IsReturn\` y \`DisplayName\` |
| \`Infrastructure.Hardware/Printers/PrinterServices.cs\` | Separación items normales vs returns |
| \`Presentation.Desktop/ViewModels/CheckoutViewModel.cs\` | Lógica RemoveItem → validación |
| \`Presentation.Desktop/Views/CheckoutView.axaml\` | Dual template Panel |
| \`Presentation.Desktop/Views/CustomerDisplayWindow.axaml\` | Dual template |

## Status
✅ Fase 1 completada (commit \`10ecb6f\`)
📋 Next: Implementar modal de autorización manager (Fase 2)
