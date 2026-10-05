namespace SyWater.Valve.Domain.Valves;

/// <summary>The numbers of E5 in one place.</summary>
public sealed record ValvePolicy(TimeSpan AckTimeout, TimeSpan CommunicationWindow)
{
    /// <summary>
    /// HU-020: "Si el dispositivo no responde en 30 segundos, se informa el fallo".
    /// The ESP32 reports its valve every 60 s, so 3 min without news = "Sin comunicación".
    /// </summary>
    public static ValvePolicy Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3));
}
