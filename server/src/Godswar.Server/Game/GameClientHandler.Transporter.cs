using Godswar.Server.Application.Npc;
using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const string TransporterUnavailableMessage =
        "Transportation is temporarily unavailable. Please try again.";

    private TransporterDialogueContext? _transporterDialogueContext;

    private async Task HandleTransporterAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !IsCanonicalTransporterAction(
                packet,
                route,
                npcId,
                dialogIndex) ||
            !TryAuthorizeTransporterAction(route, npcId) ||
            !TransporterProtocol.TryResolveDestination(
                route.NpcKey,
                npcId,
                _character.CurrentMap,
                dialogIndex,
                subId,
                arguments,
                out var destination))
        {
            Console.Error.WriteLine(
                "[transporter] rejected malformed or unauthorized action " +
                $"character={_character?.Name ?? "<none>"} npc={npcId} " +
                $"dialog={dialogIndex} sub={subId} length={packet.Length}");
            return;
        }

        ClearTransporterDialogueContext();

        // Editor override (npc_teleport_overrides): another map, point or level for this button.
        var teleportOverride = NpcContentOverrides.Current.Teleports.GetValueOrDefault((route.NpcKey, subId));
        if (_character.Level < (teleportOverride?.MinimumLevel ?? destination.MinimumLevel))
        {
            await _session.SendAsync(
                PacketBuilder.CapturedNpcFunctionActionResponse(
                    npcId,
                    TransporterProtocol.ResultDialogIndex,
                    destination.LevelRequirementResultSubId),
                cancellationToken,
                "TransporterLevelRequirement");
            Console.WriteLine(
                "[transporter] level gate rejected " +
                $"character={_character.Name} level={_character.Level} " +
                $"destination={destination.DisplayName} " +
                $"required={destination.MinimumLevel}");
            return;
        }

        if (teleportOverride is not null)
        {
            var overridden = await TryBeginMapTransitionAsync(
                checked((byte)teleportOverride.TargetMapId),
                teleportOverride.X,
                teleportOverride.Z,
                $"npc-transporter-override:{route.NpcKey}:{subId}",
                cancellationToken);
            if (overridden is not SceneTransitionOutcome.CommittedAwaitingReadiness and not SceneTransitionOutcome.CommittedRequiresReconnect)
            {
                await SendTransporterUnavailableAsync(cancellationToken);
            }

            return;
        }

        if (!_gameplayCatalogs.MapTraversal.TryGetAutomaticLink(
                destination.ArrivalAnchorSourceMapId,
                destination.TargetMapId,
                out var entryAnchor) ||
            !_gameplayCatalogs.MapTraversal.TryResolveTargetArrival(
                entryAnchor,
                MapPortalTriggerRadius,
                out var arrival) ||
            destination.TargetMapId is < byte.MinValue or > byte.MaxValue)
        {
            Console.Error.WriteLine(
                "[transporter] reviewed destination has no safe arrival " +
                $"npc={npcId} sub={subId} " +
                $"anchor={destination.ArrivalAnchorSourceMapId}->" +
                $"{destination.TargetMapId}");
            await SendTransporterUnavailableAsync(cancellationToken);
            return;
        }

        var transitioned = await TryBeginMapTransitionAsync(
            checked((byte)destination.TargetMapId),
            arrival.TargetArrival.X,
            arrival.TargetArrival.Z,
            $"npc-transporter:{route.NpcKey}:{subId}",
            cancellationToken);
        if (transitioned != SceneTransitionOutcome.CommittedAwaitingReadiness)
        {
            if (transitioned == SceneTransitionOutcome.CommittedRequiresReconnect)
            {
                return;
            }
            Console.Error.WriteLine(
                "[transporter] transition rejected by map authority " +
                $"character={_character.Name} npc={npcId} sub={subId} " +
                $"target={destination.TargetMapId}");
            await SendTransporterUnavailableAsync(cancellationToken);
            return;
        }

        Console.WriteLine(
            "[transporter] admitted " +
            $"character={_character.Name} npc={npcId} sub={subId} " +
            $"destination={destination.DisplayName} " +
            $"map={destination.SourceMapId}->{destination.TargetMapId}");
    }

    private bool TryIssueTransporterDialogueContext(
        NpcSpawnDefinition npc)
    {
        ClearTransporterDialogueContext();
        if (_account is null ||
            _character is null ||
            _character.CurrentHp <= 0 ||
            !TransporterProtocol.IsEndpoint(
                npc.NpcKey,
                npc.InteractionId) ||
            !IsWithinTransporterInteractionDistance(npc) ||
            !_registry.TryGetSessionWorldInstanceId(
                _session,
                out var worldInstanceId))
        {
            return false;
        }

        _transporterDialogueContext = new TransporterDialogueContext(
            _account.Id,
            _character.Id,
            npc.NpcKey,
            npc.InteractionId,
            _character.CurrentMap,
            worldInstanceId,
            DateTimeOffset.UtcNow +
                TransporterProtocol.MenuContextLifetime);
        return true;
    }

    private bool TryAuthorizeTransporterAction(
        NpcDialogueRouteDefinition route,
        uint npcId)
    {
        var context = _transporterDialogueContext;
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
            IsWithinTransporterInteractionDistance(npc) &&
            _registry.TryGetSessionWorldInstanceId(
                _session,
                out var currentWorldInstanceId) &&
            currentWorldInstanceId == context.SourceWorldInstanceId;
    }

    private bool IsWithinTransporterInteractionDistance(
        NpcSpawnDefinition npc)
    {
        if (_character is null)
        {
            return false;
        }

        var deltaX = (double)_character.PositionX - npc.X;
        var deltaZ = (double)_character.PositionZ - npc.Z;
        var maximum =
            (double)TransporterProtocol.MaximumInteractionDistance;
        return double.IsFinite(deltaX) &&
            double.IsFinite(deltaZ) &&
            deltaX * deltaX + deltaZ * deltaZ <= maximum * maximum;
    }

    private void ClearTransporterDialogueContext() =>
        _transporterDialogueContext = null;

    private async Task SendTransporterUnavailableAsync(
        CancellationToken cancellationToken) =>
        await _session.SendAsync(
            PacketBuilder.ServerNote(TransporterUnavailableMessage),
            cancellationToken,
            "TransporterUnavailable");

    private static bool IsCanonicalTransporterAction(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        packet.Length == TransporterProtocol.ActionPacketBytes &&
        packet.Buffer.Length == TransporterProtocol.ActionPacketBytes &&
        packet.Payload.Length >= 12 &&
        route.Behavior == NpcDialogueBehavior.Transporter &&
        route.DialogIndex == TransporterProtocol.DialogIndex &&
        TransporterProtocol.IsEndpoint(route.NpcKey, npcId) &&
        dialogIndex == TransporterProtocol.DialogIndex &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex;
}
