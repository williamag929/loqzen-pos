# Demo: Flujo de Devolución en Carrito

## Escenario
Cliente compra 3 Coca-Colas pero luego decide devolver 1 antes de pagar.

## Pasos

### 1. Agregar Productos
```
[ Carrito ]
🟦 Coca-Cola 350ml
   $3.000 x 3 = $9.000
   [+] [-] [🔄]

Subtotal: $9.000
IVA (19%): $1.710
Total: $10.710
```

### 2. Presionar Botón 🔄 (Devolver)
✅ Validación: 0 devueltas < 3 agregadas → OK
➡️ Agrega item de devolución al carrito

### 3. Carrito Actualizado
```
[ Carrito ]
🟦 Coca-Cola 350ml          (fondo azul #0F172A)
   $3.000 x 3 = $9.000
   [+] [-] [🔄]

🟥 🔄 DEVOLUCIÓN: Coca-Cola   (fondo rojo #450A0A)
   (Nota crédito - ajuste en venta)
   $3.000 x -1 = -$3.000
   [✕]

Subtotal: $6.000
IVA (19%): $1.140
Total: $7.140
```

### 4. Pantalla Cliente (Customer Display)
```
[ Pantalla de Cliente ]
🔵 Coca-Cola 350ml
   $3.000 × 3 = $9.000

🔴 ⚠️ DEVOLUCIÓN: Coca-Cola
   (Nota crédito en esta venta)
   $3.000 × -1 = -$3.000

━━━━━━━━━━━━━━━━━━━━━━
TOTAL: $7.140
```

### 5. Finalizar Pago
Presionar **[💳 COBRAR]** → Modal de pago

Seleccionar: Efectivo
Recibido: $10.000
Cambio: $2.860

Presionar **[Confirmar]** → Procesa venta

### 6. Recibo Impreso
```
LOQZEN POS
Local Principal
NIT 900.000.000-1
--------------------------------
Orden: ORD-20260112-0001
Fecha: 2026-01-12 15:30
Cajero: Principal
--------------------------------
   3 Coca-Cola 350ml     9.000
--------------------------------

*** NOTA CREDITO ***
  -1 DEVOL: Coca-Cola   -3.000
     (Ajuste en venta)
--------------------------------
Subtotal:               $6.000
IVA (19%):              $1.140
Total:                  $7.140
--------------------------------
Pago: Efectivo
Recibido:              $10.000
Cambio:                 $2.860
--------------------------------
Gracias por su compra
```

## Validación: Intentar Devolver Más de lo Agregado

```
[ Carrito ]
🟦 Coca-Cola 350ml
   $3.000 x 3 = $9.000

🟥 🔄 DEVOLUCIÓN: Coca-Cola
   $3.000 x -3 = -$9.000
```

Presionar 🔄 de nuevo:

❌ **"No puede devolver más de lo agregado en esta venta"**

(returnedQty: 3, normalQty: 3 → 3 >= 3 → bloqueado)

---

## Código Relevante

### CheckoutViewModel.RemoveItem
```csharp
[RelayCommand]
public void RemoveItem(CartItemDto item)
{
    if (item.IsReturn)
    {
        CartItems.Remove(item);
        StatusMessage = "Devolución removida";
        return;
    }

    var normalQty = CartItems
        .Where(i => i.ProductId == item.ProductId && !i.IsReturn)
        .Sum(i => i.IsWeighed ? (i.WeightKg ?? i.Quantity) : i.Quantity);
    
    var returnedQty = CartItems
        .Where(i => i.ProductId == item.ProductId && i.IsReturn)
        .Sum(i => Math.Abs(i.IsWeighed ? (i.WeightKg ?? i.Quantity) : i.Quantity));

    if (returnedQty >= normalQty)
    {
        StatusMessage = "❌ No puede devolver más de lo agregado en esta venta";
        return;
    }

    var returnItem = new CartItemDto
    {
        ProductId = item.ProductId,
        Name = item.Name,
        UnitPrice = item.UnitPrice,
        Quantity = -1m,
        IsReturn = true
    };

    CartItems.Add(returnItem);
    StatusMessage = $"🔄 Devolución agregada: {item.Name}";
}
```

### PrinterService (separación de items)
```csharp
var normalItems = receipt.Items.Where(i => !i.IsReturn).ToList();
var returnItems = receipt.Items.Where(i => i.IsReturn).ToList();

// Imprimir normales
foreach (var item in normalItems) { ... }

// Imprimir sección NOTA CREDITO
if (returnItems.Any())
{
    writer.Write("*** NOTA CREDITO ***\n");
    foreach (var item in returnItems)
    {
        var name = $"DEVOL: {item.Name}";
        writer.Write($"{qty} {name} {total}\n");
        writer.Write("     (Ajuste en venta)\n");
    }
}
```

---

**Status:** ✅ Implementado y testeado
**Commit:** `10ecb6f`
