using Godswar.Server.State;

namespace Godswar.Server.Game;

internal enum MonsterRespawnPolicy
{
    Timed = 0,
    Never = 1
}

internal static class MonsterRespawnPolicyRules
{
    /// <summary>Thermopylae, the only map with an authored field elite.</summary>
    private const short ThermopylaeMapId = 8;

    private const string ThermopylaeEliteTemplateKey =
        "B_eliteB_stymphalianbird_001";

    /// <summary>
    /// Authored cadences that differ from the runtime-wide ordinary respawn
    /// delay. World bosses resolve through <see cref="WorldBossCatalog"/>
    /// instead, so this table only carries field encounters: the runtime delay
    /// stays a single value and only the monsters listed here are special.
    /// </summary>
    private static readonly Dictionary<(short MapId, string TemplateKey), TimeSpan>
        AuthoredRespawnIntervals = BuildAuthoredRespawnIntervals();

    private static Dictionary<(short MapId, string TemplateKey), TimeSpan> BuildAuthoredRespawnIntervals()
    {
        var intervals = new Dictionary<(short MapId, string TemplateKey), TimeSpan>()
        {
            // Thermopylae holds exactly one field elite. An hour keeps it a
            // deliberate target rather than a continuous farm, and because a
            // monster only respawns after its own death the area can never
            // hold two at once.
            [(ThermopylaeMapId, ThermopylaeEliteTemplateKey)] =
                TimeSpan.FromHours(1)
        };
        // Lelantine Farm dogs come back a minute after they are caught or killed.
        foreach (var template in Godswar.Server.Application.WorldInstances.LelantineFarmDogPolicy.TemplateKeys)
            intervals[(Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId, template)] =
                TimeSpan.FromSeconds(60);
        // Farm elites (10 points a kill) wait two minutes; Cerberus (1,000 points) waits the length of one
        // 45-minute round, so it can be scored once per battle. GENERATED, no reference value exists.
        foreach (var elite in new[]
                 {
                     "A_elite_wolf_004", "B_eliteA_wolf_004", "B_eliteB_wolf_004", "A_elite_wolf_005", "B_elite_wolf_005"
                 })
            intervals[(Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId, elite)] = TimeSpan.FromMinutes(2);
        intervals[(Godswar.Server.Domain.World.Content.LelantineFarmProtocol.MapId, "B_boss_wolf_006")] =
            TimeSpan.FromMinutes(45);
        return intervals;
    }

    public static void Validate(MonsterRespawnPolicy policy)
    {
        if (policy is not (MonsterRespawnPolicy.Timed or MonsterRespawnPolicy.Never))
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy,
                "Unsupported monster respawn policy.");
        }
    }

    public static TimeSpan? ResolveOrdinaryDelay(
        MonsterRespawnPolicy policy,
        TimeSpan corpseDespawnDelay,
        TimeSpan? configuredRespawnDelay)
    {
        Validate(policy);
        if (policy == MonsterRespawnPolicy.Never)
        {
            if (configuredRespawnDelay is not null)
            {
                throw new ArgumentException(
                    "A never-respawn runtime cannot also define a respawn delay.",
                    nameof(configuredRespawnDelay));
            }

            return null;
        }

        var delay = configuredRespawnDelay ?? MonsterMapRuntime.DefaultRespawnDelay;
        if (delay <= corpseDespawnDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredRespawnDelay),
                "Monster respawn delay must be later than corpse despawn.");
        }

        return delay;
    }

    /// <summary>
    /// Resolves the cadence a killed monster waits before it reappears: a
    /// selected world boss keeps its scheduled interval, an authored field
    /// encounter uses its own, and everything else uses the runtime-wide delay.
    /// </summary>
    public static TimeSpan ResolveRespawnInterval(
        WorldBossCatalog worldBossCatalog,
        short mapId,
        string templateKey,
        TimeSpan ordinaryRespawnInterval)
    {
        ArgumentNullException.ThrowIfNull(worldBossCatalog);
        if (worldBossCatalog.IsWorldBoss(mapId, templateKey))
        {
            return worldBossCatalog.ResolveRespawnInterval(
                mapId,
                templateKey,
                ordinaryRespawnInterval);
        }

        return AuthoredRespawnIntervals.TryGetValue(
            (mapId, templateKey),
            out var authoredInterval)
            ? authoredInterval
            : ordinaryRespawnInterval;
    }

    public static void RejectTimedWorldBossConfiguration(
        MonsterRespawnPolicy policy,
        byte mapId,
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        WorldBossRespawnState? activeWorldBossRespawn,
        WorldBossCatalog worldBossCatalog)
    {
        Validate(policy);
        if (policy != MonsterRespawnPolicy.Never)
        {
            return;
        }

        if (activeWorldBossRespawn is not null)
        {
            throw new ArgumentException(
                "A never-respawn runtime cannot restore a timed world-boss respawn.",
                nameof(activeWorldBossRespawn));
        }

        if (definitions.Any(definition =>
                worldBossCatalog.IsWorldBoss(mapId, definition.TemplateKey)))
        {
            throw new ArgumentException(
                "A configured world boss requires the timed respawn policy.",
                nameof(worldBossCatalog));
        }
    }
}
