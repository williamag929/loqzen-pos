using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;

namespace Core.Application.Hardware;

public interface IPrinterService
{
    Task<bool> PrintReceiptAsync(ReceiptDto receipt, CancellationToken cancellationToken = default);
    Task<bool> PrintKitchenTicketAsync(ReceiptDto receipt, string station, CancellationToken cancellationToken = default);
    Task<bool> OpenCashDrawerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Prints pre-formatted lines (Z report, bill-payment receipt, movement slip) with the same ESC/POS styles
    /// as receipts (bold/large/center) and a cut. False + TXT fallback on failure, like PrintReceiptAsync.
    /// </summary>
    Task<bool> PrintLinesAsync(System.Collections.Generic.IReadOnlyList<Core.Application.Receipts.ReceiptLine> lines, string documentName, CancellationToken cancellationToken = default);
}

public class WeightChangedEventArgs : EventArgs
{
    public decimal WeightKg { get; set; }
    public bool IsStable { get; set; }
}

public interface IScaleService
{
    event EventHandler<WeightChangedEventArgs>? WeightChanged;
    Task<decimal?> ReadWeightAsync(CancellationToken cancellationToken = default);
    bool IsConnected { get; }
    void StartListening();
    void StopListening();
}

public interface ICashDrawerService
{
    Task<bool> OpenDrawerAsync(CancellationToken cancellationToken = default);
}

public interface IBarcodeScannerService
{
    event EventHandler<string>? BarcodeScanned;
    void StartListening();
    void StopListening();
}
