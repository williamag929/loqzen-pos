namespace Presentation.Desktop;

/// <summary>
/// Configuration for hardware peripherals. Bound from appsettings.json "Hardware" section.
/// When UseMock* is true, Mock implementations are registered; otherwise real drivers are used.
/// </summary>
public class HardwareSettings
{
    public bool UseMockPrinter { get; set; } = true;
    public bool UseMockScale { get; set; } = true;
    public bool UseMockScanner { get; set; } = true;

    public string PrinterHost { get; set; } = "127.0.0.1";
    public int PrinterPort { get; set; } = 9100;

    public string ScalePortName { get; set; } = "/dev/ttyUSB0";
    public int ScaleBaudRate { get; set; } = 9600;
}
