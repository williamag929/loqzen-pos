using System;
using System.Text;
using System.Threading;
using Core.Application.Hardware;

namespace Infrastructure.Hardware.Scanners;

public class UsbHidBarcodeScannerService : IBarcodeScannerService
{
    private readonly StringBuilder _buffer = new();
    private DateTime _lastKeystrokeTime = DateTime.MinValue;
    private const int MaxInterKeystrokeDelayMs = 60; // Barcode guns type very fast (< 50ms)
    private bool _isListening;

    public event EventHandler<string>? BarcodeScanned;

    public void StartListening()
    {
        _isListening = true;
    }

    public void StopListening()
    {
        _isListening = false;
        _buffer.Clear();
    }

    /// <summary>
    /// Processes keystrokes from global keyboard interception or active window key events.
    /// Standard USB HID barcode guns simulate fast keystrokes followed by Enter ('\r' or '\n').
    /// </summary>
    public void ProcessKeyInput(char character)
    {
        if (!_isListening) return;

        var now = DateTime.UtcNow;
        if ((now - _lastKeystrokeTime).TotalMilliseconds > MaxInterKeystrokeDelayMs && _buffer.Length > 0)
        {
            // Human typing or delay exceeded: reset buffer
            _buffer.Clear();
        }

        _lastKeystrokeTime = now;

        if (character == '\r' || character == '\n')
        {
            if (_buffer.Length >= 3)
            {
                var barcode = _buffer.ToString().Trim();
                _buffer.Clear();
                BarcodeScanned?.Invoke(this, barcode);
            }
            else
            {
                _buffer.Clear();
            }
        }
        else if (!char.IsControl(character))
        {
            _buffer.Append(character);
        }
    }
}

public class MockBarcodeScannerService : IBarcodeScannerService
{
    public event EventHandler<string>? BarcodeScanned;
    private bool _isListening;

    public void StartListening()
    {
        _isListening = true;
    }

    public void StopListening()
    {
        _isListening = false;
    }

    /// <summary>
    /// Simulates scanning a barcode (e.g. for testing in UI or development)
    /// </summary>
    public void SimulateScan(string barcode)
    {
        if (_isListening)
        {
            BarcodeScanned?.Invoke(this, barcode);
        }
    }
}
