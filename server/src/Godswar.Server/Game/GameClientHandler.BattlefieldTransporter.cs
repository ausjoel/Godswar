using System.Buffers.Binary;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const string BattlefieldTransportUnavailableMessage =
        "Battlefield transportation is temporarily unavailable.";

    private BattlefieldTransporterDialogueContext?
        _battlefieldTransporterDialogueContext;

    private async Task HandleBattlefieldTransporterAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !IsCanonicalBattlefieldTransporterAction(
                packet,
                route,
                npcId,
                dialogIndex) ||
            !TryAuthorizeBattlefieldTransporterAction(route, npcId))
        {
            Console.Error.WriteLine(
                "[battlefield-transporter] rejected malformed or " +
                $"unauthorized action character=" +
                $"{_character?.Name ?? "<none>"} npc={npcId} " +
                $"dialog={dialogIndex} sub={subId} length={packet.Length}");
            return;
        }

        if (BattlefieldTransporterProtocol.TryGetNiMiniPage(
                dialogIndex,
                subId,
                arguments,
                out var pageSubIds))
        {
            _battlefieldTransporterDialogueContext =
                _battlefieldTransporterDialogueContext! with
                {
                    NiMiniPageIssued = true
                };
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    pageSubIds),
                cancellationToken,
                "BattlefieldTransporterNiMiniPage");
            return;
        }

        if (subId == BattlefieldTransporterProtocol.NiMiniRootSubId &&
            _battlefieldTransporterDialogueContext?.NiMiniPageIssued != true)
        {
            Console.Error.WriteLine(
                "[battlefield-transporter] rejected Ni Mini entry without " +
                $"current page context character={_character.Name} " +
                $"npc={npcId}");
            return;
        }

        if (!BattlefieldTransporterProtocol.TryResolveDestination(
                route.NpcKey,
                npcId,
                _character.CurrentMap,
                _character.Camp,
                dialogIndex,
                subId,
                arguments,
                out var destination))
        {
            Console.Error.WriteLine(
                "[battlefield-transporter] rejected unknown path " +
                $"character={_character.Name} npc={npcId} sub={subId}");
            return;
        }

        ClearBattlefieldTransporterDialogueContext();

        // Editor override (npc_teleport_overrides). Opening hours and the upper level limit stay as compiled.
        var edited = NpcContentOverrides.Current.Teleports.GetValueOrDefault((
            route.NpcKey,
            subId == BattlefieldTransporterProtocol.LelantineFarmSingleChannelSubId
                ? BattlefieldTransporterProtocol.LelantineFarmSubId
                : subId));
        if (edited is not null)
        {
            destination = BattlefieldTransporterProtocol.ApplyEdit(
                destination,
                edited.TargetMapId,
                edited.X,
                edited.Z,
                edited.MinimumLevel);
        }

        // GM accounts (the developer-command allowlist) skip the level and
        // opening-hours gates so a GM can use the transporter to test or run an event.
        var operatorBypass = await HasOperatorAuthorityAsync(cancellationToken);
        if (!operatorBypass &&
            (_character.Level < destination.MinimumLevel ||
             _character.Level > destination.MaximumLevel))
        {
            await SendBattlefieldAdmissionResultAsync(
                npcId,
                destination.Kind,
                closed: false,
                cancellationToken);
            Console.WriteLine(
                "[battlefield-transporter] level gate rejected " +
                $"character={_character.Name} level={_character.Level} " +
                $"destination={destination.DisplayName} required=" +
                $"{destination.MinimumLevel}-{destination.MaximumLevel}");
            return;
        }

        if (!operatorBypass && !BattlefieldSchedulePolicy.IsOpen(
                destination.Kind,
                _realmCalendar,
                DateTimeOffset.UtcNow))
        {
            await SendBattlefieldAdmissionResultAsync(
                npcId,
                destination.Kind,
                closed: true,
                cancellationToken);
            Console.WriteLine(
                "[battlefield-transporter] schedule gate rejected " +
                $"character={_character.Name} " +
                $"destination={destination.DisplayName}");
            return;
        }

        var transitioned = destination.TargetMapId is >= byte.MinValue and
                <= byte.MaxValue
            ? await TryBeginMapTransitionAsync(
                checked((byte)destination.TargetMapId),
                destination.TargetX,
                destination.TargetZ,
                $"npc-battlefield-transporter:{route.NpcKey}:{subId}",
                cancellationToken)
            : SceneTransitionOutcome.RejectedWithoutRelocation;
        if (transitioned == SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return;
        }
        if (transitioned == SceneTransitionOutcome.RejectedWithoutRelocation)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(
                    BattlefieldTransportUnavailableMessage),
                cancellationToken,
                "BattlefieldTransporterUnavailable");
            Console.Error.WriteLine(
                "[battlefield-transporter] transition rejected " +
                $"character={_character.Name} npc={npcId} sub={subId} " +
                $"target={destination.TargetMapId}");
            return;
        }

        Console.WriteLine(
            "[battlefield-transporter] admitted " +
            $"character={_character.Name} npc={npcId} sub={subId} " +
            $"destination={destination.DisplayName} " +
            $"map={destination.SourceMapId}->{destination.TargetMapId}");
    }

    private bool TryIssueBattlefieldTransporterDialogueContext(
        NpcSpawnDefinition npc)
    {
        ClearBattlefieldTransporterDialogueContext();
        if (_account is null ||
            _character is null ||
            _character.CurrentHp <= 0 ||
            !BattlefieldTransporterProtocol.IsEndpoint(
                npc.NpcKey,
                npc.InteractionId) ||
            !IsWithinBattlefieldTransporterInteractionDistance(npc) ||
            !_registry.TryGetSessionWorldInstanceId(
                _session,
                out var worldInstanceId))
        {
            return false;
        }

        _battlefieldTransporterDialogueContext =
            new BattlefieldTransporterDialogueContext(
                _account.Id,
                _character.Id,
                npc.NpcKey,
                npc.InteractionId,
                _character.CurrentMap,
                worldInstanceId,
                DateTimeOffset.UtcNow +
                    BattlefieldTransporterProtocol.MenuContextLifetime);
        return true;
    }

    private bool TryAuthorizeBattlefieldTransporterAction(
        NpcDialogueRouteDefinition route,
        uint npcId)
    {
        var context = _battlefieldTransporterDialogueContext;
        return context is not null &&
            _account is not null &&
            _character is not null &&
            _character.CurrentHp > 0 &&
            context.ExpiresAt > DateTimeOffset.UtcNow &&
            context.AccountId == _account.Id &&
            context.CharacterId == _character.Id &&
            context.SourceMapId == _character.CurrentMap &&
            context.NpcInteractionId == npcId &&
            string.Equals(
                context.NpcKey,
                route.NpcKey,
                StringComparison.Ordinal) &&
            TryResolveMapNpc(npcId, out var npc) &&
            IsWithinBattlefieldTransporterInteractionDistance(npc) &&
            _registry.TryGetSessionWorldInstanceId(
                _session,
                out var currentWorldInstanceId) &&
            currentWorldInstanceId == context.SourceWorldInstanceId;
    }

    private bool IsWithinBattlefieldTransporterInteractionDistance(
        NpcSpawnDefinition npc)
    {
        if (_character is null)
        {
            return false;
        }

        var deltaX = (double)_character.PositionX - npc.X;
        var deltaZ = (double)_character.PositionZ - npc.Z;
        var maximum =
            (double)BattlefieldTransporterProtocol.MaximumInteractionDistance;
        return double.IsFinite(deltaX) &&
            double.IsFinite(deltaZ) &&
            deltaX * deltaX + deltaZ * deltaZ <= maximum * maximum;
    }

    private void ClearBattlefieldTransporterDialogueContext() =>
        _battlefieldTransporterDialogueContext = null;

    private async Task SendBattlefieldAdmissionResultAsync(
        uint npcId,
        BattlefieldDestinationKind destination,
        bool closed,
        CancellationToken cancellationToken)
    {
        var (dialogIndex, subId) = destination switch
        {
            BattlefieldDestinationKind.Pindus when
                _character is { Level: < 31 } =>
                (BattlefieldTransporterProtocol
                    .PindusMinimumLevelResultDialogIndex,
                    BattlefieldTransporterProtocol
                        .PindusMinimumLevelResultSubId),
            BattlefieldDestinationKind.Pindus =>
                (BattlefieldTransporterProtocol.PindusResultDialogIndex,
                    closed
                        ? BattlefieldTransporterProtocol
                            .PindusClosedResultSubId
                        : BattlefieldTransporterProtocol
                            .PindusLevelResultSubId),
            BattlefieldDestinationKind.NiMiniLower or
            BattlefieldDestinationKind.NiMiniUpper =>
                (BattlefieldTransporterProtocol.NiMiniResultDialogIndex,
                    BattlefieldTransporterProtocol
                        .NiMiniUnavailableResultSubId),
            // The farm words its own refusals inside the activity window, so the
            // refusal is answered under dialog index 47 rather than the
            // transport menu's index 1.
            BattlefieldDestinationKind.LelantineFarm =>
                (BattlefieldTransporterProtocol.LelantineFarmResultDialogIndex,
                    closed
                        ? BattlefieldTransporterProtocol
                            .LelantineFarmClosedResultSubId
                        : BattlefieldTransporterProtocol
                            .LelantineFarmLevelResultSubId),
            _ => throw new InvalidOperationException(
                "The always-open Duel Arena has no admission result.")
        };
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                subId),
            cancellationToken,
            "BattlefieldTransporterAdmissionRejected");
    }

    private static bool IsCanonicalBattlefieldTransporterAction(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        packet.Length == BattlefieldTransporterProtocol.ActionPacketBytes &&
        packet.Buffer.Length ==
            BattlefieldTransporterProtocol.ActionPacketBytes &&
        packet.Payload.Length >= 12 &&
        route.Behavior == NpcDialogueBehavior.BattlefieldTransporter &&
        route.DialogIndex == BattlefieldTransporterProtocol.DialogIndex &&
        BattlefieldTransporterProtocol.IsEndpoint(route.NpcKey, npcId) &&
        dialogIndex == BattlefieldTransporterProtocol.DialogIndex &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex;
}
