using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static readonly TimeSpan StatusPeriodicInterval = TimeSpan.FromSeconds(1);

    // Status.ini Effect 26/28 with Interval=1. Same vitals path as passive recovery, so pending potion
    // commits and death handling behave the same way.
    private async Task AdvanceStatusPeriodicRecoveryOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (session, state) in _playerStatusStates.ToArray())
        {
            if (!Volatile.Read(ref state.PeriodicRecoveryActive) || now < state.NextPeriodicRecoveryAt) continue;
            long health = 0, mana = 0;
            await state.Gate.WaitAsync(cancellationToken);
            try
            {
                var active = state.RuntimeStatuses.Values
                    .Where(status => status.ExpiresAt > now && (status.HpPerSecond > 0 || status.MpPerSecond > 0))
                    .ToArray();
                if (active.Length == 0)
                {
                    Volatile.Write(ref state.PeriodicRecoveryActive, false);
                    continue;
                }

                health = active.Sum(static status => (long)status.HpPerSecond);
                mana = active.Sum(static status => (long)status.MpPerSecond);
                state.NextPeriodicRecoveryAt = now + StatusPeriodicInterval;
            }
            finally
            {
                state.Gate.Release();
            }

            GameSessionContext? recovered = null;
            lock (_gate)
            {
                if (_sessions.TryGetValue(session, out var current) && current.WorldReady &&
                    ApplyAuthoritativeRecoveryPulseLocked(current, now, health, mana).VitalsChanged)
                {
                    recovered = current;
                }
            }

            if (recovered is not null) await PublishRecoveredVitalsAsync(recovered, cancellationToken);
        }
    }

    /// <summary>False when an active status of the same kind outranks it, so an item is not wasted.</summary>
    internal async Task<bool> CanApplyRuntimeStatusAsync(ClientSession session, SkillStatusEffectDefinition definition,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state)) return true;
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            return !state.RuntimeStatuses.TryGetValue(definition.Kind, out var existing) ||
                existing.ExpiresAt <= now || existing.Priority <= definition.Priority;
        }
        finally
        {
            state.Gate.Release();
        }
    }
}
