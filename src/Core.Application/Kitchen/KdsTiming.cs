using System;

namespace Core.Application.Kitchen;

/// <summary>Bound from appsettings.json section "Kds".</summary>
public class KdsSettings
{
    /// <summary>Below this many minutes the order is on time (green).</summary>
    public int WarningMinutes { get; set; } = 5;

    /// <summary>Maximum preparation time. Above it the order is late (red, blinking).</summary>
    public int MaxPrepMinutes { get; set; } = 15;

    /// <summary>After "Entregar", the card stays (with undo) this many seconds before disappearing.</summary>
    public int ArchiveDelaySeconds { get; set; } = 20;

    /// <summary>How often the KDS checks the database for new / changed orders.</summary>
    public int PollSeconds { get; set; } = 3;
}

public enum KdsUrgency
{
    OnTime = 0,   // verde
    Warning = 1,  // amarillo
    Late = 2      // rojo + parpadeo (superó el tiempo máximo)
}

/// <summary>Pure time rules for the KDS, kept out of the UI so they can be unit-tested.</summary>
public static class KdsTiming
{
    public static KdsUrgency Urgency(DateTime createdAtUtc, DateTime nowUtc, KdsSettings s)
    {
        var minutes = Math.Max(0, (nowUtc - createdAtUtc).TotalMinutes);
        if (minutes < s.WarningMinutes) return KdsUrgency.OnTime;
        if (minutes <= s.MaxPrepMinutes) return KdsUrgency.Warning;
        return KdsUrgency.Late;
    }

    /// <summary>Seconds left before an archived card disappears (0 = remove now).</summary>
    public static double ArchiveRemainingSeconds(DateTime deliveredAtUtc, DateTime nowUtc, KdsSettings s) =>
        Math.Max(0, s.ArchiveDelaySeconds - (nowUtc - deliveredAtUtc).TotalSeconds);
}
