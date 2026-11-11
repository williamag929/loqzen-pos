using System;
using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.Hardware;

namespace Infrastructure.Hardware.Scales;

public class CasSerialScaleService : IScaleService, IDisposable
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _serialPort;
    private decimal _lastWeight = 0;
    private readonly Regex _casRegex = new(@"ST,GS,\+?([0-9.]+)\s*kg", RegexOptions.IgnoreCase);

    public event EventHandler<WeightChangedEventArgs>? WeightChanged;
    public bool IsConnected => _serialPort?.IsOpen ?? false;

    public CasSerialScaleService(string portName = "/dev/ttyUSB0", int baudRate = 9600)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public void StartListening()
    {
        try
        {
            if (_serialPort == null)
            {
                _serialPort = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 500
                };
                _serialPort.DataReceived += OnDataReceived;
            }

            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CAS SW-20RS Scale Connection Warning]: {ex.Message}");
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return;

        try
        {
            var line = _serialPort.ReadLine();
            var match = _casRegex.Match(line);
            if (match.Success && decimal.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var weight))
            {
                _lastWeight = weight;
                WeightChanged?.Invoke(this, new WeightChangedEventArgs
                {
                    WeightKg = weight,
                    IsStable = line.StartsWith("ST")
                });
            }
        }
        catch
        {
            // Timeout or transient read error
        }
    }

    public Task<decimal?> ReadWeightAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<decimal?>(_lastWeight);
    }

    public void StopListening()
    {
        if (_serialPort != null && _serialPort.IsOpen)
        {
            _serialPort.Close();
        }
    }

    public void Dispose()
    {
        StopListening();
        _serialPort?.Dispose();
    }
}

public class MockScaleService : IScaleService
{
    public event EventHandler<WeightChangedEventArgs>? WeightChanged;
    public bool IsConnected => true;
    private decimal _mockWeight = 1.25m;

    public void SetMockWeight(decimal weightKg)
    {
        _mockWeight = weightKg;
        WeightChanged?.Invoke(this, new WeightChangedEventArgs
        {
            WeightKg = _mockWeight,
            IsStable = true
        });
    }

    public Task<decimal?> ReadWeightAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<decimal?>(_mockWeight);
    }

    public void StartListening()
    {
        WeightChanged?.Invoke(this, new WeightChangedEventArgs
        {
            WeightKg = _mockWeight,
            IsStable = true
        });
    }

    public void StopListening()
    {
    }
}
