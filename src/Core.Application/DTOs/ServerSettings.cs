namespace Core.Application.DTOs;

/// <summary>Bound from appsettings.json section "Server": the Kestrel host embedded in the desktop app.</summary>
public class ServerSettings
{
    /// <summary>Start the LAN server with the desktop app (KDS web page, REST, WebSocket).</summary>
    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 5055;

    /// <summary>"0.0.0.0" = every interface (tablets on the LAN); "127.0.0.1" = this machine only.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";
}
