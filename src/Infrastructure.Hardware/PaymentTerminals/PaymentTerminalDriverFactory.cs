using System;
using System.Collections.Generic;
using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Domain.Enums;

namespace Infrastructure.Hardware.PaymentTerminals;

/// <summary>
/// Picks the driver by provider from the registered <see cref="IPaymentTerminalDriver"/> instances.
/// Register every driver as an IPaymentTerminalDriver singleton and this factory as the IPaymentTerminalDriverFactory.
/// </summary>
public class PaymentTerminalDriverFactory : IPaymentTerminalDriverFactory
{
    private readonly Dictionary<PaymentTerminalProvider, IPaymentTerminalDriver> _drivers = new();

    public PaymentTerminalDriverFactory(IEnumerable<IPaymentTerminalDriver> drivers)
    {
        foreach (var d in drivers) _drivers[d.Provider] = d;
    }

    public IReadOnlyList<PaymentTerminalProvider> AvailableProviders => new List<PaymentTerminalProvider>(_drivers.Keys);

    public IPaymentTerminalDriver GetDriver(PaymentTerminalProvider provider) =>
        _drivers.TryGetValue(provider, out var d) ? d
        : throw new InvalidOperationException($"No hay driver para el proveedor de datáfono '{PaymentTerminalProviders.Label(provider)}'.");
}
