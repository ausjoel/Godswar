using System.Collections.Concurrent;
using Godswar.Server.Application.World.Content;
using Godswar.Server.State;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const float MonsterLootPickupRadius = 12f;

    /// <summary>
    /// Captured corpse window for a looted field kill. The reference kept the
    /// corpse of the Athens kill on object 10492 from 01:37:23.758 until its
    /// 10023 removal at 01:37:43.820, and its captured Medusa rules use the
    /// same twenty seconds.
    /// </summary>
    private const int CorpseWithLootMilliseconds = 20_000;

    private readonly ConcurrentDictionary<
        MonsterLootRuntimeKey,
        MonsterLootRuntimeState> _monsterLoot = [];

    internal bool TryResolveMedusaMonsterRule(
        ClientSession session,
        MonsterDamageResult damage,
        out MedusaMonsterRule rule)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(damage);
        rule = default;
        if (!_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var resolved = InvokeWorldOwner(
            runtime,
            map =>
            {
                var found = map.TryResolveMedusaMonsterRule(
                    damage.ObjectId,
                    damage.Monster.SpawnGeneration,
                    out var value);
                return (Found: found, Rule: value);
            });
        rule = resolved.Rule;
        return resolved.Found;
    }

    internal MonsterLootPresentation? PrepareMedusaMonsterLoot(
        ClientSession session,
        MonsterDamageResult damage,
        Guid deathEventId,
        DateTimeOffset diedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(damage);
        if (deathEventId == Guid.Empty || !damage.Killed ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return null;
        }

        var prepared = InvokeWorldOwner(
            runtime,
            map =>
            {
                if (!map.TryResolveMedusaMonsterRule(
                        damage.ObjectId,
                        damage.Monster.SpawnGeneration,
                        out var rule))
                {
                    return default(PreparedMonsterLoot?);
                }

                var rolled = MedusaMonsterContentCatalog.Current.RollLoot(
                    rule.Difficulty,
                    rule.TemplateAlias,
                    deathEventId);
                var entries = rolled.Select((drop, index) =>
                        new MonsterLootEntry(
                            index,
                            drop.LootIndex,
                            drop.ItemId,
                            drop.Quantity))
                    .ToArray();
                var delayMilliseconds = entries.Length == 0
                    ? rule.CorpseWithoutLootMilliseconds
                    : rule.CorpseWithLootMilliseconds;
                DateTimeOffset? expiresAt = delayMilliseconds.HasValue
                    ? diedAt + TimeSpan.FromMilliseconds(
                        delayMilliseconds.Value)
                    : null;
                if (!map.TrySetMonsterCorpseDespawnAt(
                        damage.ObjectId,
                        damage.Monster.SpawnGeneration,
                        expiresAt))
                {
                    return default(PreparedMonsterLoot?);
                }
                return new PreparedMonsterLoot(rule, entries, expiresAt);
            });
        if (prepared is null)
        {
            return null;
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            damage.ObjectId);
        if (prepared.Entries.Count == 0)
        {
            _monsterLoot.TryRemove(key, out _);
            return new(
                damage.ObjectId,
                damage.Monster.SpawnGeneration,
                deathEventId,
                []);
        }

        var state = new MonsterLootRuntimeState(
            context.CharacterId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            prepared.ExpiresAt,
            prepared.Entries,
            damage.Monster.X,
            damage.Monster.Z);
        _monsterLoot[key] = state;
        return new(
            damage.ObjectId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            prepared.Entries);
    }

    /// <summary>
    /// Rolls the database-owned drop table of the killed monster's template and
    /// starts tracking its ground items. Unlike the Medusa path this needs no
    /// instance rule: the table is keyed by the published monster template key,
    /// which is the same identity the client already received in its spawn
    /// frame.
    /// </summary>
    internal MonsterLootPresentation? PrepareMonsterLoot(
        ClientSession session,
        MonsterDamageResult damage,
        Guid deathEventId,
        DateTimeOffset diedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(damage);
        if (deathEventId == Guid.Empty || !damage.Killed ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            context.MapId != damage.Monster.Definition.MapId ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return null;
        }

        // AREA LOOT: one snapshot for both the lookup and the roll, and the
        // table is chosen per (template, map): "template@map" first, then the
        // plain template table (unchanged behaviour for every other monster).
        var lootContent = MonsterLootContentCatalog.Current;
        if (lootContent.ResolveTableKey(
                damage.Monster.Definition.TemplateKey,
                damage.Monster.Definition.MapId) is not { } lootKey)
        {
            return null;
        }

        var rolled = lootContent.RollLoot(
            lootKey,
            deathEventId,
            diedAt); // EVENT DROPS (q07)
        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            damage.ObjectId);
        if (rolled.Count == 0)
        {
            _monsterLoot.TryRemove(key, out _);
            return null;
        }

        var entries = rolled.Select((drop, index) =>
                new MonsterLootEntry(
                    index,
                    drop.LootIndex,
                    drop.ItemId,
                    drop.Quantity,
                    drop.BoundOnPickup,
                    drop.Attributes))
            .ToArray();
        // The client only opens a corpse's loot window while that corpse is on
        // screen, so a looted field corpse keeps the captured window instead of
        // the five-second default. The reference kept the corpse of a looted
        // Athens kill (object 10492) for 20.06 s.
        var expiresAt = diedAt +
            TimeSpan.FromMilliseconds(CorpseWithLootMilliseconds);
        var corpseAccepted = InvokeWorldOwner(
            runtime,
            map => map.TrySetMonsterCorpseDespawnAt(
                damage.ObjectId,
                damage.Monster.SpawnGeneration,
                expiresAt));
        if (!corpseAccepted)
        {
            MonsterLootTrace.Log(
                $"corpse-window-rejected map={context.MapId} monster={damage.ObjectId} generation={damage.Monster.SpawnGeneration}");
        }

        _monsterLoot[key] = new MonsterLootRuntimeState(
            context.CharacterId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            expiresAt,
            entries,
            damage.Monster.X,
            damage.Monster.Z);
        MonsterLootTrace.Log(
            $"prepared map={context.MapId} monster={damage.ObjectId} corpse=({damage.Monster.X:F2},{damage.Monster.Z:F2}) drops={entries.Length} corpseWindow={corpseAccepted} expires={expiresAt:O}");
        return new(
            damage.ObjectId,
            damage.Monster.SpawnGeneration,
            deathEventId,
            entries);
    }

    /// <summary>
    /// True when this session still owns an unclaimed ground item on the given
    /// corpse at the drop index the client clicked, which is what the client's
    /// corpse click has to be answered for. The captured reply echoes that same
    /// index, so an index the corpse never dropped must stay unanswered.
    /// </summary>
    internal bool HasPendingMonsterLoot(
        ClientSession session,
        uint monsterObjectId,
        int dropIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (monsterObjectId == 0 || dropIndex < 0 ||
            !_sessions.TryGetValue(session, out var context))
        {
            return false;
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            monsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state) ||
            state.ClaimantCharacterId != context.CharacterId)
        {
            return false;
        }

        lock (state.Gate)
        {
            return state.Entries.ContainsKey(dropIndex) &&
                !state.Pending.ContainsKey(dropIndex);
        }
    }

    internal bool TryReserveMonsterLootPickup(
        ClientSession session,
        uint monsterObjectId,
        int pickupIndex,
        DateTimeOffset now,
        out MonsterLootPickupReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(session);
        reservation = default!;
        if (monsterObjectId == 0 || pickupIndex is < 0 or >= 32 ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var key = new MonsterLootRuntimeKey(
            context.WorldInstanceId,
            monsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return false;
        }

        return TryReserveLootEntry(
            session,
            context,
            runtime,
            key,
            state,
            pickupIndex,
            now,
            out reservation);
    }

    /// <summary>
    /// Reserves a ground item for the installed client's pickup request. That
    /// request carries the item id and the destination bag slot but not the
    /// corpse, so the owning loot state is found by the echoed ground key when
    /// the client sends one, and by item id otherwise.
    /// </summary>
    internal bool TryReserveGroundLootPickup(
        ClientSession session,
        uint itemId,
        uint groundKeyHigh,
        uint groundKeyLow,
        DateTimeOffset now,
        out MonsterLootPickupReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(session);
        reservation = default!;
        if (itemId == 0 ||
            !_sessions.TryGetValue(session, out var context) ||
            !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var hasGroundKey = groundKeyHigh != 0 || groundKeyLow != 0;
        var candidates = new List<(
            MonsterLootRuntimeKey Key,
            MonsterLootRuntimeState State,
            int PickupIndex)>();
        foreach (var pair in _monsterLoot)
        {
            var state = pair.Value;
            if (state.ClaimantCharacterId != context.CharacterId ||
                pair.Key.WorldInstanceId != context.WorldInstanceId)
            {
                continue;
            }

            foreach (var entry in state.Entries)
            {
                var key = MonsterLootGroundKey.Resolve(
                    state.DeathEventId,
                    entry.Value.RuleLootIndex);
                var matchesKey =
                    hasGroundKey &&
                    key.High == groundKeyHigh &&
                    key.Low == groundKeyLow;
                if (matchesKey || entry.Value.ItemId == itemId)
                {
                    candidates.Add((pair.Key, state, entry.Key));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // An exact ground-key match wins; otherwise the item id decides, which
        // is all the client sends for a normal pickup.
        candidates.Sort(static (left, right) =>
            left.PickupIndex.CompareTo(right.PickupIndex));
        if (hasGroundKey)
        {
            foreach (var candidate in candidates)
            {
                var key = MonsterLootGroundKey.Resolve(
                    candidate.State.DeathEventId,
                    candidate.State.Entries[candidate.PickupIndex]
                        .RuleLootIndex);
                if (key.High == groundKeyHigh && key.Low == groundKeyLow)
                {
                    return TryReserveLootEntry(
                        session,
                        context,
                        runtime,
                        candidate.Key,
                        candidate.State,
                        candidate.PickupIndex,
                        now,
                        out reservation);
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (TryReserveLootEntry(
                    session,
                    context,
                    runtime,
                    candidate.Key,
                    candidate.State,
                    candidate.PickupIndex,
                    now,
                    out reservation))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryReserveLootEntry(
        ClientSession session,
        GameSessionContext context,
        WorldInstanceRuntime runtime,
        MonsterLootRuntimeKey key,
        MonsterLootRuntimeState state,
        int pickupIndex,
        DateTimeOffset now,
        out MonsterLootPickupReservation reservation)
    {
        reservation = default!;
        // The corpse is already gone from the map runtime: a field kill clears
        // both IsAlive and IsSpawned as soon as the lethal hit commits, so the
        // pickup has to validate against the death position captured with the
        // drop instead of a live monster snapshot. A live snapshot is still
        // used while the corpse exists so a respawned generation cannot hand
        // out the old loot.
        var corpseX = state.CorpseX;
        var corpseZ = state.CorpseZ;
        var monsterAttempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var found = map.TryGetMonsterSnapshot(
                    key.MonsterObjectId,
                    out var monster);
                return (Found: found, Monster: monster);
            });
        if (monsterAttempt.Found)
        {
            var live = monsterAttempt.Monster;
            if (live.SpawnGeneration != state.SpawnGeneration)
            {
                return false;
            }

            if (live.IsSpawned)
            {
                corpseX = live.X;
                corpseZ = live.Z;
            }
        }

        if (DistanceSquared(
                context.Character.PositionX,
                context.Character.PositionZ,
                corpseX,
                corpseZ) >
            MonsterLootPickupRadius * MonsterLootPickupRadius)
        {
            return false;
        }

        lock (state.Gate)
        {
            if (state.ClaimantCharacterId != context.CharacterId ||
                state.ExpiresAt is { } expiresAt && now >= expiresAt ||
                !state.Entries.TryGetValue(pickupIndex, out var entry) ||
                state.Pending.ContainsKey(pickupIndex))
            {
                if (state.ExpiresAt is { } expired && now >= expired)
                {
                    _monsterLoot.TryRemove(
                        new KeyValuePair<
                            MonsterLootRuntimeKey,
                            MonsterLootRuntimeState>(key, state));
                }
                return false;
            }

            var attemptId = Guid.NewGuid();
            state.Pending.Add(pickupIndex, attemptId);
            reservation = new(
                key.WorldInstanceId,
                key.MonsterObjectId,
                state.SpawnGeneration,
                state.DeathEventId,
                pickupIndex,
                entry.RuleLootIndex,
                entry.ItemId,
                entry.Quantity,
                attemptId,
                entry.BoundOnPickup,
                entry.Attributes);
            return true;
        }
    }

    internal void CompleteMonsterLootPickup(
        MonsterLootPickupReservation reservation)
    {
        var key = new MonsterLootRuntimeKey(
            reservation.WorldInstanceId,
            reservation.MonsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            if (!state.Pending.TryGetValue(
                    reservation.PickupIndex,
                    out var attemptId) ||
                attemptId != reservation.AttemptId)
            {
                return;
            }
            state.Pending.Remove(reservation.PickupIndex);
            state.Entries.Remove(reservation.PickupIndex);
            if (state.Entries.Count == 0)
            {
                _monsterLoot.TryRemove(
                    new KeyValuePair<
                        MonsterLootRuntimeKey,
                        MonsterLootRuntimeState>(key, state));
            }
        }
    }

    internal void ReleaseMonsterLootPickup(
        MonsterLootPickupReservation reservation)
    {
        var key = new MonsterLootRuntimeKey(
            reservation.WorldInstanceId,
            reservation.MonsterObjectId);
        if (!_monsterLoot.TryGetValue(key, out var state))
        {
            return;
        }
        lock (state.Gate)
        {
            if (state.Pending.TryGetValue(
                    reservation.PickupIndex,
                    out var attemptId) &&
                attemptId == reservation.AttemptId)
            {
                state.Pending.Remove(reservation.PickupIndex);
            }
        }
    }

    private static float DistanceSquared(
        float leftX,
        float leftZ,
        float rightX,
        float rightZ)
    {
        var deltaX = leftX - rightX;
        var deltaZ = leftZ - rightZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private readonly record struct MonsterLootRuntimeKey(
        WorldInstanceId WorldInstanceId,
        uint MonsterObjectId);

    private sealed class MonsterLootRuntimeState
    {
        public MonsterLootRuntimeState(
            int claimantCharacterId,
            uint spawnGeneration,
            Guid deathEventId,
            DateTimeOffset? expiresAt,
            IReadOnlyList<MonsterLootEntry> entries,
            float corpseX,
            float corpseZ)
        {
            ClaimantCharacterId = claimantCharacterId;
            SpawnGeneration = spawnGeneration;
            DeathEventId = deathEventId;
            ExpiresAt = expiresAt;
            Entries = entries.ToDictionary(
                static entry => entry.PickupIndex);
            CorpseX = corpseX;
            CorpseZ = corpseZ;
        }

        public object Gate { get; } = new();
        public int ClaimantCharacterId { get; }
        public uint SpawnGeneration { get; }
        public Guid DeathEventId { get; }
        public DateTimeOffset? ExpiresAt { get; }

        /// <summary>
        /// Where the corpse died. The map runtime despawns a field corpse as
        /// soon as the lethal hit commits, so the ground items keep their own
        /// position for the pickup radius check.
        /// </summary>
        public float CorpseX { get; }
        public float CorpseZ { get; }

        public Dictionary<int, MonsterLootEntry> Entries { get; }
        public Dictionary<int, Guid> Pending { get; } = [];
    }

    private sealed record PreparedMonsterLoot(
        MedusaMonsterRule Rule,
        IReadOnlyList<MonsterLootEntry> Entries,
        DateTimeOffset? ExpiresAt);
}

internal readonly record struct MonsterLootEntry(
    int PickupIndex,
    int RuleLootIndex,
    uint ItemId,
    int Quantity,
    bool? BoundOnPickup = null,
    ItemGrantAttributes Attributes = default);

internal sealed record MonsterLootPresentation(
    uint MonsterObjectId,
    uint SpawnGeneration,
    Guid DeathEventId,
    IReadOnlyList<MonsterLootEntry> Entries);

internal sealed record MonsterLootPickupReservation(
    WorldInstanceId WorldInstanceId,
    uint MonsterObjectId,
    uint SpawnGeneration,
    Guid DeathEventId,
    int PickupIndex,
    int RuleLootIndex,
    uint ItemId,
    int Quantity,
    Guid AttemptId,
    bool? BoundOnPickup = null,
    ItemGrantAttributes Attributes = default);
