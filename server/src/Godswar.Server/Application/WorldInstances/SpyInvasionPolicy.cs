using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// The capital-city spies (Spartan spies in Athens, Athenian spies in Sparta)
/// exist only while an invasion runs: Monday, Wednesday and Saturday at 19:00
/// realm time (Asia/Manila, UTC+8) for 30 minutes. A wave arrives at 19:00 and
/// every 5 minutes after it (19:00, :05, :10, :15, :20, :25): a spy that was
/// killed returns with the next wave, survivors leave when the window closes.
/// Off unless <c>event.spy_invasion</c> is 1 (spies then stay permanent).
/// </summary>
internal static class SpyInvasionPolicy
{
    private static readonly TimeSpan RealmOffset = TimeSpan.FromHours(8);
    private static readonly TimeSpan Start = TimeSpan.FromHours(19);
    private static readonly TimeSpan Slot = TimeSpan.FromMinutes(5);
    private const int SlotsPerWindow = 6;
    private static readonly TimeSpan Window = TimeSpan.FromTicks(Slot.Ticks * SlotsPerWindow);

    private static readonly DayOfWeek[] Days =
        [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Saturday];

    // Map 0 (Sparta) holds the Athenian spies, map 1 (Athens) the Spartan ones.
    private static readonly HashSet<string> TemplateKeys = new(StringComparer.Ordinal)
    {
        "A_normals_greecewarrior_002", "A_normals_greecewarrior_004", "A_normals_greecewarrior_012",
        "A_normalsA_greecewarrior_002", "A_normalsB_greecewarrior_002",
        "A_normals_greecewarrior_001", "A_normals_greecewarrior_003", "A_normals_greecewarrior_008",
        "A_normalsA_greecewarrior_001", "A_normalsB_greecewarrior_001"
    };

    public static bool IsSpy(short mapId, string? templateKey) =>
        mapId is 0 or 1 && templateKey is not null && TemplateKeys.Contains(templateKey);

    public static bool Enabled =>
        ServerRates.Current.Absolute(ServerRateKeys.SpyInvasion) == 1;

    /// <summary>True while a window is open (start-inclusive, end-exclusive).</summary>
    public static bool IsActive(DateTimeOffset instant)
    {
        var local = instant.ToOffset(RealmOffset);
        for (var back = 0; back <= 1; back++)
        {
            var day = local.Date.AddDays(-back);
            if (Array.IndexOf(Days, day.DayOfWeek) < 0)
            {
                continue;
            }

            var opens = new DateTimeOffset(day + Start, RealmOffset);
            if (local >= opens && local < opens + Window)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first wave boundary strictly after <paramref name="instant"/>.</summary>
    public static DateTimeOffset NextWaveStart(DateTimeOffset instant)
    {
        var local = instant.ToOffset(RealmOffset);
        for (var ahead = -1; ahead <= 8; ahead++)
        {
            var day = local.Date.AddDays(ahead);
            if (Array.IndexOf(Days, day.DayOfWeek) < 0)
            {
                continue;
            }

            for (var slot = 0; slot < SlotsPerWindow; slot++)
            {
                var wave = new DateTimeOffset(day + Start, RealmOffset) + Slot * slot;
                if (wave > instant)
                {
                    return wave;
                }
            }
        }

        throw new InvalidOperationException("No spy wave within the next week.");
    }
}
