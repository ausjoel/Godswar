using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<int> SendExperienceBoostStatusesAsync(
        byte mapId,
        byte? camp,
        string reason,
        CancellationToken cancellationToken,
        ClientSession? routingSession = null)
    {
        if (_store is null ||
            !TryResolveWorldInstance(
                mapId,
                routingSession,
                out var runtime))
        {
            return 0;
        }

        var recipients = InvokeWorldOwner(
            runtime,
            map => map.Snapshot()
                .Where(context =>
                    context.WorldReady &&
                    (camp is null ||
                     context.Character.Camp == camp.Value))
                .ToArray(),
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var sent = 0;
        foreach (var context in recipients)
        {
            try
            {
                if (!IsCurrentWorldInstanceRecipient(
                        runtime.InstanceId,
                        context))
                {
                    continue;
                }

                var boosts = await GetExperienceBoostStateAsync(
                    context.Session,
                    context.AccountId,
                    context.CharacterId,
                    context.Character.Camp,
                    context.MapId,
                    now,
                    cancellationToken);
                if (!IsCurrentWorldInstanceRecipient(
                        runtime.InstanceId,
                        context))
                {
                    continue;
                }

                if (await RefreshExperienceStatusesAndPublishAsync(
                        context.Session,
                        boosts,
                        now,
                        reason,
                        force: true,
                        broadcast: true,
                        cancellationToken))
                {
                    sent++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[status] EXP boost map sync failed character={context.DisplayName} reason={reason}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[status] EXP boost map sync map={mapId} camp={(camp?.ToString() ?? "all")} reason={reason} sent={sent}");
        return sent;
    }

    public Task<bool> RefreshExperienceStatusesAndPublishAsync(
        ClientSession session,
        ExperienceBoostState boosts,
        string reason,
        CancellationToken cancellationToken)
    {
        return RefreshExperienceStatusesAndPublishAsync(
            session,
            boosts,
            DateTimeOffset.UtcNow,
            reason,
            force: true,
            broadcast: true,
            cancellationToken);
    }

    internal async Task<bool> RefreshExperienceStatusesAndPublishAsync(
        ClientSession session,
        ExperienceBoostState boosts,
        DateTimeOffset now,
        string reason,
        bool force,
        bool broadcast,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(boosts);

        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var donatorStatsChanged =
                HasDonatorStatProjectionChanged(
                    state.ExperienceBoosts,
                    boosts);
            if (donatorStatsChanged &&
                !await RefreshDonatorCalculatedStatsAsync(
                    session,
                    cancellationToken))
            {
                return false;
            }
            if (donatorStatsChanged)
            {
                state.LocalCalculatedStatsSynchronizationPending = true;
            }

            state.ExperienceBoosts = boosts;
            var published = await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force,
                broadcast,
                cancellationToken,
                forceLocalGameDataSynchronization:
                    state.LocalCalculatedStatsSynchronizationPending,
                claimedDisconnects: admissionClaims);
            if (published)
            {
                state.LocalCalculatedStatsSynchronizationPending = false;
            }

            return published;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
        }
    }

    public async Task<bool> ApplyRuntimeStatusAndPublishAsync(
        ClientSession session,
        SkillStatusEffectDefinition definition,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // ROUND2: /bufflock on - the GM's own buffs last 24 hours.
        if (definition.Beneficial &&
            _sessions.TryGetValue(session, out var operatorContext) &&
            Application.Operators.OperatorControls.HasBuffLock(
                operatorContext.CharacterId) &&
            definition.Duration < TimeSpan.FromHours(24))
        {
            definition = definition with { Duration = TimeSpan.FromHours(24) };
        }

        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        ActiveRuntimeStatus? appliedStatus = null;
        var applied = false;
        var admissionClaims = new ExactStatusDisconnectClaims();
        var interruptionTask = Task.CompletedTask;
        TaskCompletionSource? interruptionNotificationBarrier = null;
        try
        {
            await state.Gate.WaitAsync(cancellationToken);
            try
            {
                if (state.RuntimeStatuses.TryGetValue(
                        definition.Kind,
                        out var existing) &&
                    existing.ExpiresAt > now &&
                    existing.Priority > definition.Priority)
                {
                    return false;
                }

                var interruptionReason =
                    PlayerSkillCastControlCatalog
                        .ResolveAppliedInterruption(
                            definition.StatusId);
                if (interruptionReason is not null)
                {
                    interruptionNotificationBarrier =
                        new TaskCompletionSource(
                            TaskCreationOptions
                                .RunContinuationsAsynchronously);
                    // The handler claims the pending generation
                    // synchronously before its first await. Claim before
                    // mutating the status so completion and interruption
                    // have one authoritative order even at the deadline.
                    interruptionTask =
                        RequestSkillCastInterruptionAsync(
                            session,
                            interruptionReason.Value,
                            cancellationToken,
                            interruptionNotificationBarrier.Task);
                }

                appliedStatus = new ActiveRuntimeStatus(
                    definition.StatusId,
                    definition.Kind,
                    definition.Priority,
                    definition.Beneficial,
                    now + definition.Duration,
                    new ClientStatusAggregate(
                        definition.HitBonus,
                        definition.CriticalAppendBonus,
                        0f,
                        PhysicalDefense:
                            definition.PhysicalDefenseBonus,
                        MagicDefense:
                            definition.MagicDefenseBonus,
                        Dodge: definition.DodgeBonus,
                        CriticalResistance:
                            definition.CriticalResistanceBonus,
                        PhysicalAttack: definition.PhysicalAttackBonus,
                        MagicAttack: definition.MagicAttackBonus,
                        PhysicalDamageBonusBasisPoints: definition.PhysicalDamageBonusBasisPoints,
                        MagicDamageBonusBasisPoints: definition.MagicDamageBonusBasisPoints),
                    checked(++state.Revision),
                    definition.PhysicalDamageReduction,
                    definition.MagicDamageReduction,
                    HpPerSecond: definition.HpPerSecond,
                    MpPerSecond: definition.MpPerSecond);
                state.RuntimeStatuses[definition.Kind] = appliedStatus;
                if (definition.HpPerSecond > 0 || definition.MpPerSecond > 0)
                {
                    state.NextPeriodicRecoveryAt = now + StatusPeriodicInterval;
                    Volatile.Write(ref state.PeriodicRecoveryActive, true);
                }
                RefreshSkillCastControlSnapshot(state);
                await PublishStatusSnapshotLockedAsync(
                    session,
                    state,
                    now,
                    reason,
                    force: true,
                    broadcast: true,
                    cancellationToken,
                    claimedDisconnects: admissionClaims);
                applied = true;
            }
            finally
            {
                state.Gate.Release();
                admissionClaims.CompleteAll(this);
                if (appliedStatus is not null)
                {
                    ScheduleRuntimeStatusExpiry(
                        session,
                        state,
                        appliedStatus);
                }
            }
        }
        finally
        {
            // Preserve status-before-interruption packet ordering while
            // claiming the pending generation before status mutation.
            // Always release the notice if status publication fails.
            interruptionNotificationBarrier?.TrySetResult();
            await interruptionTask;
        }

        return applied;
    }

    public bool IsRuntimeStatusActive(
        ClientSession session,
        int kind,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return false;
        }

        state.Gate.Wait();
        try
        {
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return EvaluatePlayerStatusEcsLocked(
                                session,
                                state,
                                context,
                                now)
                            .ActiveRuntimeStatuses
                            .Any(status => status.Kind == kind);
                    }
                }
            }

            return state.RuntimeStatuses.TryGetValue(kind, out var status) &&
                status.ExpiresAt > now;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<bool> SetPersistentRuntimeStatusAndPublishAsync(
        ClientSession session,
        int kind,
        uint statusId,
        int priority,
        bool beneficial,
        float movementSpeedBonus,
        bool active,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!float.IsFinite(movementSpeedBonus) || movementSpeedBonus < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(movementSpeedBonus),
                movementSpeedBonus,
                "A persistent runtime speed bonus must be finite and non-negative.");
        }

        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var changed = false;
            if (active)
            {
                var replacement = new ActiveRuntimeStatus(
                    statusId,
                    kind,
                    priority,
                    beneficial,
                    DateTimeOffset.MaxValue,
                    ClientStatusAggregate.Empty,
                    checked(++state.Revision),
                    MovementSpeedBonus: movementSpeedBonus);
                changed = !state.RuntimeStatuses.TryGetValue(kind, out var existing) ||
                    existing.StatusId != replacement.StatusId ||
                    existing.MovementSpeedBonus != replacement.MovementSpeedBonus ||
                    existing.ExpiresAt <= now;
                state.RuntimeStatuses[kind] = replacement;
            }
            else
            {
                changed = state.RuntimeStatuses.Remove(kind);
            }

            if (!changed)
            {
                return false;
            }

            RefreshSkillCastControlSnapshot(state);
            await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force: true,
                broadcast: true,
                cancellationToken,
                forceLocalGameDataSynchronization:
                    kind == MountCatalog.RuntimeStatusKind,
                claimedDisconnects: admissionClaims);
            return true;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
        }
    }

}
