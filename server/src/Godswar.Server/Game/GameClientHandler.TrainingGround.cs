using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// TRAINING: the Training Ground's player-facing half - the Instance Caller
/// entry (page 231), free revive at the entrance, the leader's end control, and
/// the completion-reward payout.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The Heracles page's own button: a character inside the level band opens the
    /// shared Enter window for the party (one exact run of map 210); one outside it
    /// gets the reference's own level line (1510), exactly as before.
    /// </summary>
    private async Task BeginTrainingGroundEntryAsync(
        uint npcId,
        int dialogIndex,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var destination = InstanceCallerProtocol.TrainingGroundDestination;
        if (_character.Level < destination.MinimumLevel ||
            _character.Level > destination.MaximumLevel)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    InstanceCallerProtocol.AtlantisLevelResultSubId),
                cancellationToken,
                "InstanceCallerTrainingGroundLevel");
            return;
        }

        // Daily-entry kind 10 needs its settings row and widened checks before
        // the first claim; idempotent and done once per process.
        if (_registry.TrainingGroundStore is { } store)
        {
            try
            {
                await store.EnsureSchemaAsync(cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[training] schema unavailable: {error.Message}");
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        InstanceCallerProtocol.QueueUnavailableResultSubId),
                    cancellationToken,
                    "InstanceCallerTrainingGroundUnavailable");
                return;
            }
        }

        Console.WriteLine(
            "[instance-caller] Training Ground entry " +
            $"character={_character.Name} level={_character.Level} " +
            $"map={destination.TargetMapId} scene={destination.ClientSceneId}");
        await BeginLegacyInstanceEntryCountdownAsync(
            npcId,
            dialogIndex,
            destination,
            cancellationToken);
    }

    /// <summary>
    /// IGG acid=166 rule 3: a player killed in the Training Ground revives near the
    /// entrance and carries on. The generic path would restore the capital.
    /// </summary>
    private async Task<bool> TryHandleTrainingGroundReviveAsync(
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !TrainingGroundPolicy.IsTrainingGroundMap(_character.CurrentMap) ||
            !_registry.TryGetTrainingGroundInstance(_session, out var instanceId))
        {
            return false;
        }

        if (!_registry.TryReviveTrainingGroundPlayer(
                _session,
                out instanceId,
                out var lifeRevision))
        {
            Console.WriteLine(
                "[training] revive refused character=" +
                $"{_character.Name} instance={instanceId}");
            return true;
        }

        try
        {
            var outcome = await TryBeginSameMapSceneTransitionAsync(
                TrainingGroundPolicy.ArrivalX,
                TrainingGroundPolicy.ArrivalZ,
                "training-revive",
                () => _registry.IsSessionInWorldInstance(_session, instanceId) &&
                    _registry.TryGetPlayerLifeRevision(
                        _session,
                        out var currentLife) &&
                    currentLife == lifeRevision,
                cancellationToken,
                publishRevivalVitals: true);
            Console.WriteLine(
                "[training] free revive character=" +
                $"{_character.Name} instance={instanceId} life={lifeRevision} outcome={outcome}");
            if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation)
            {
                _session.Disconnect();
            }
        }
        catch
        {
            _session.Disconnect();
            throw;
        }

        return true;
    }

    /// <summary>The panel's Terminate control (no id) or the 12-byte end request (scene 256).</summary>
    private bool TryHandleTrainingGroundTermination(
        int? repetitionId,
        int repetitionIndex)
    {
        if (_character is null ||
            !TrainingGroundPolicy.IsTrainingGroundMap(_character.CurrentMap))
        {
            return false;
        }

        return _registry.TryTerminateTrainingGroundRunFromLeader(
            _session,
            repetitionId ?? TrainingGroundPolicy.ClientSceneId,
            repetitionIndex);
    }

    /// <summary>
    /// Pays this character's recorded Training Ground rewards (EXP and talent
    /// points) through the shared progression payout the Zeus' Gift and Lelantine
    /// Farm rewards use (level-ups, client gain packets, the exp.events rate).
    /// Each row is claimed atomically first (so it is paid at most once, whichever
    /// handler or login runs it) and is never put back: an interrupted payment is
    /// logged as lost rather than risked twice. Called from the existing 20-second
    /// reward loop (which also runs once on world entry).
    /// </summary>
    private async Task PayTrainingGroundRewardsAsync(CancellationToken cancellationToken)
    {
        if (_registry.TrainingGroundStore is not { } store || _character is not { } character)
        {
            return;
        }

        IReadOnlyList<TrainingGroundRewardGrant> claims;
        try
        {
            claims = await store.ClaimRewardsAsync(character.Id, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[training] reward claim failed character={character.Name}: {error.Message}");
            return;
        }

        foreach (var claim in claims)
        {
            // At most once: the row stays claimed whatever happens below. The
            // shared payout writes EXP/TP to the database before it refreshes stats
            // and sends packets, so putting the row back after a late failure (a
            // client that just closed, a failed paid mark) would pay it twice.
            try
            {
                await PayZeusGiftProgressionAsync(
                    claim.Experience,
                    checked(claim.TalentPoints * Godswar.Server.Application.ZeusGift.ZeusGiftPolicy.TalentExperiencePerPoint),
                    "training-ground",
                    cancellationToken);
                Console.WriteLine(
                    $"[training] reward paid character={character.Name} run={claim.RunId} " +
                    $"outcome={claim.Outcome} score={claim.Score} exp={claim.Experience} tp={claim.TalentPoints}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[training] reward payment interrupted, NOT retried (claimed) character={character.Name} " +
                    $"run={claim.RunId} exp={claim.Experience} tp={claim.TalentPoints}: {error.Message}");
                if (error is OperationCanceledException)
                {
                    throw;
                }
                continue;
            }

            try
            {
                await store.MarkPaidAsync(claim.RunId, claim.CharacterId, CancellationToken.None);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"[training] reward paid but paid_at not written run={claim.RunId}: {error.Message}");
            }
        }
    }
    /// <summary>
    /// Map 210 has no templates of its own; its GM rows may use the listed
    /// cross-map spider / scorpion templates (TrainingGroundPolicy.Templates).
    /// Every other map is untouched.
    /// </summary>
    private static void AddTrainingGroundTemplateKeys(short mapId, HashSet<string> catalogTemplateKeys)
    {
        if (TrainingGroundPolicy.IsTrainingGroundMap(mapId))
        {
            catalogTemplateKeys.UnionWith(TrainingGroundPolicy.Templates.Keys);
        }
    }

    /// <summary>Adds the Training Ground kill recorder to the settlement's commit callback.</summary>
    private Action? ChainTrainingGroundKill(Action? existing, MonsterDamageResult damageResult)
    {
        try
        {
            var record = _registry.CaptureTrainingGroundMonsterKill(_session, damageResult);
            return record is null ? existing : existing is null ? record : existing + record;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[training] death capture failed monster={damageResult.ObjectId}: {ex.Message}");
            return existing;
        }
    }
}
