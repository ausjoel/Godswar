using Godswar.Server.Infrastructure.Relations;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

/// <summary>
/// The friend and blacklist windows. The client's RequestMsgBox with Agree and Refuse
/// buttons decides the flow: a friend request waits for the other player's answer and
/// then adds both sides. The 2008 server added one side at once and never used that
/// box; the 30 per list limit and the same faction rule for friends come from it.
/// A blacklist entry needs no answer and may name an offline character.
/// </summary>
internal sealed partial class GameClientHandler
{
    private int? _relationPresenceCharacterId;

    private async Task<bool> TryHandleRelationPacketAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Opcode is not (
                Opcodes.RelationRequest or
                Opcodes.RelationResponse or
                Opcodes.RelationDelete))
        {
            return false;
        }

        if (_character is null)
        {
            return true;
        }

        // A database failure costs this one click, not the player's connection.
        try
        {
            switch (packet.Opcode)
            {
                case Opcodes.RelationRequest:
                    await HandleRelationRequestAsync(packet, cancellationToken);
                    break;
                case Opcodes.RelationResponse:
                    await HandleRelationResponseAsync(packet, cancellationToken);
                    break;
                default:
                    await HandleRelationDeleteAsync(packet, cancellationToken);
                    break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[relations] opcode={packet.Opcode} character={_character.Id} failed: {error.Message}");
        }

        return true;
    }

    private async Task HandleRelationRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!RelationProtocol.TryReadRequest(
                packet.Payload,
                out var targetName,
                out var kind))
        {
            return;
        }

        var target = targetName.Length > RelationProtocol.MaximumNameLength
            ? null
            : await RelationStore.FindActiveByNameAsync(
                character.RealmId.Value,
                targetName,
                cancellationToken);
        if (target is not { } found || found.CharacterId == character.Id)
        {
            await SendRelationErrorAsync(
                targetName,
                kind,
                RelationProtocol.RequestErrorNoSuchPlayer,
                cancellationToken);
            return;
        }

        var existing = await RelationStore.GetKindAsync(
            character.Id,
            found.CharacterId,
            cancellationToken);
        if (kind == RelationProtocol.KindBlacklist)
        {
            await AddToBlacklistAsync(found, existing, cancellationToken);
            return;
        }

        await RequestFriendAsync(found, existing, cancellationToken);
    }

    private async Task AddToBlacklistAsync(
        RelationEntry target,
        byte? existing,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        if (existing is not null)
        {
            await SendRelationNoteAsync(
                existing == RelationProtocol.KindFriend
                    ? $"Remove {target.Name} from your friends first."
                    : $"{target.Name} is already on your blacklist.",
                cancellationToken);
            return;
        }

        var result = await RelationStore.AddBlacklistAsync(
            character.Id,
            target.CharacterId,
            cancellationToken);
        if (result == RelationAddResult.ListFull)
        {
            await SendRelationErrorAsync(
                target.Name,
                RelationProtocol.KindBlacklist,
                RelationProtocol.RequestErrorListFull,
                cancellationToken);
            return;
        }

        if (result != RelationAddResult.Added)
        {
            return;
        }

        _registry.TryTakeRelationInvitation(character.Id, target.CharacterId);
        await _session.SendAsync(
            PacketBuilder.RelationAdded(
                WithPresence(target),
                RelationProtocol.KindBlacklist),
            cancellationToken,
            "RelationAdded");
    }

    private async Task RequestFriendAsync(
        RelationEntry target,
        byte? existing,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        if (existing == RelationProtocol.KindFriend)
        {
            await SendRelationErrorAsync(
                target.Name,
                RelationProtocol.KindFriend,
                RelationProtocol.RequestErrorAlreadyListed,
                cancellationToken);
            return;
        }

        if (existing == RelationProtocol.KindBlacklist)
        {
            await SendRelationNoteAsync(
                $"Remove {target.Name} from your blacklist first.",
                cancellationToken);
            return;
        }

        if (await RelationStore.IsFullAsync(
                character.Id,
                RelationProtocol.KindFriend,
                cancellationToken))
        {
            await SendRelationErrorAsync(
                target.Name,
                RelationProtocol.KindFriend,
                RelationProtocol.RequestErrorListFull,
                cancellationToken);
            return;
        }

        if (!_registry.TryFindOnlineRelationTarget(
                character.RealmId,
                target.Name,
                out var targetContext))
        {
            await SendRelationErrorAsync(
                target.Name,
                RelationProtocol.KindFriend,
                RelationProtocol.RequestErrorNoSuchPlayer,
                cancellationToken);
            return;
        }

        if (targetContext.Character.Camp != character.Camp)
        {
            await SendRelationNoteAsync(
                $"{target.Name} is in the other faction.",
                cancellationToken);
            return;
        }

        // Someone who blacklisted the requester just looks like someone who said no.
        if (await RelationStore.GetKindAsync(
                target.CharacterId,
                character.Id,
                cancellationToken) == RelationProtocol.KindBlacklist)
        {
            await _session.SendAsync(
                PacketBuilder.RelationResponse(target.Name, asking: false),
                cancellationToken,
                "RelationRefused");
            return;
        }

        _registry.RecordRelationInvitation(target.CharacterId, character.Id);
        await targetContext.Session.SendAsync(
            PacketBuilder.RelationResponse(character.Name, asking: true),
            cancellationToken,
            "RelationAsk");
    }

    private async Task HandleRelationResponseAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!RelationProtocol.TryReadNameAndFlag(
                packet.Payload,
                out var requesterName,
                out var agree))
        {
            return;
        }

        var requester = await RelationStore.FindActiveByNameAsync(
            character.RealmId.Value,
            requesterName,
            cancellationToken);
        if (requester is not { } found ||
            !_registry.TryTakeRelationInvitation(character.Id, found.CharacterId))
        {
            return;
        }

        _registry.TryFindOnlineByCharacterId(found.CharacterId, out var requesterContext);
        if (agree == 0)
        {
            if (requesterContext is not null)
            {
                await requesterContext.Session.SendAsync(
                    PacketBuilder.RelationResponse(character.Name, asking: false),
                    cancellationToken,
                    "RelationRefused");
            }

            return;
        }

        var result = await RelationStore.AddMutualFriendsAsync(
            character.Id,
            found.CharacterId,
            cancellationToken);
        switch (result)
        {
            case RelationAddResult.Added:
                await _session.SendAsync(
                    PacketBuilder.RelationAdded(
                        WithPresence(found),
                        RelationProtocol.KindFriend),
                    cancellationToken,
                    "RelationAdded");
                if (requesterContext is not null)
                {
                    await requesterContext.Session.SendAsync(
                        PacketBuilder.RelationAdded(
                            WithPresence(
                                new RelationEntry(
                                    character.Id,
                                    character.Name,
                                    character.Camp,
                                    character.Profession,
                                    character.Level,
                                    true)),
                            RelationProtocol.KindFriend),
                        cancellationToken,
                        "RelationAdded");
                }

                break;
            case RelationAddResult.ListFull:
                await SendRelationErrorAsync(
                    found.Name,
                    RelationProtocol.KindFriend,
                    RelationProtocol.RequestErrorListFull,
                    cancellationToken);
                break;
            case RelationAddResult.OtherListFull:
                await SendRelationNoteAsync(
                    $"{found.Name}'s friend list is full.",
                    cancellationToken);
                break;
            default:
                await SendRelationNoteAsync(
                    $"{found.Name} is already on a list of yours or has you on theirs.",
                    cancellationToken);
                break;
        }
    }

    private async Task HandleRelationDeleteAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        if (!RelationProtocol.TryReadNameAndFlag(
                packet.Payload,
                out var name,
                out var kind) ||
            kind > RelationProtocol.KindBlacklist)
        {
            return;
        }

        await RelationStore.DeleteAsync(
            character.Id,
            name,
            kind,
            cancellationToken);
        await SendRelationListsAsync(cancellationToken);
    }

    /// <summary>
    /// Both windows, in the place of the post-enter bootstrap where the captured
    /// server sent them empty. Also tells online friends this character arrived.
    /// </summary>
    private async Task SendRelationBootstrapAsync(CancellationToken cancellationToken)
    {
        await SendRelationListsAsync(cancellationToken);
        var character = _character;
        if (character is null || _relationPresenceCharacterId == character.Id)
        {
            return;
        }

        _relationPresenceCharacterId = character.Id;
        await PublishRelationPresenceAsync(character, true, cancellationToken);
    }

    private async Task SendRelationListsAsync(CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        var friends = new List<RelationEntry>();
        var blacklist = new List<RelationEntry>();
        try
        {
            foreach (var (kind, entry) in await RelationStore.ListAsync(
                         character.Id,
                         cancellationToken))
            {
                (kind == RelationProtocol.KindFriend ? friends : blacklist)
                    .Add(WithPresence(entry));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[relations] list failed character={character.Id}: {error.Message}");
        }

        await _session.SendAsync(
            PacketBuilder.RelationList(
                CurrentPlayerObjectId,
                RelationProtocol.KindFriend,
                friends),
            cancellationToken,
            "RelationList");
        await _session.SendAsync(
            PacketBuilder.RelationList(
                CurrentPlayerObjectId,
                RelationProtocol.KindBlacklist,
                blacklist),
            cancellationToken,
            "RelationList");
    }

    /// <summary>Called on the way out, after the session has left the registry.</summary>
    private async Task PublishRelationOfflineAsync()
    {
        if (_character is not { } character ||
            _relationPresenceCharacterId != character.Id)
        {
            return;
        }

        _relationPresenceCharacterId = null;

        // A replacing login of the same character may already be in the world.
        if (_registry.TryFindOnlineByCharacterId(character.Id, out _))
        {
            return;
        }

        await PublishRelationPresenceAsync(character, false, CancellationToken.None);
    }

    private async Task PublishRelationPresenceAsync(
        State.GameCharacter character,
        bool online,
        CancellationToken cancellationToken)
    {
        try
        {
            var packet = PacketBuilder.RelationPresence(
                character.Id,
                online,
                character.Level);
            foreach (var owner in await RelationStore.ListFriendOwnersAsync(
                         character.Id,
                         cancellationToken))
            {
                if (!_registry.TryFindOnlineByCharacterId(owner, out var context))
                {
                    continue;
                }

                // One friend's dead socket must not cost the others the news.
                try
                {
                    await context.Session.SendAsync(
                        packet,
                        cancellationToken,
                        "RelationPresence");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.WriteLine(
                        $"[relations] presence skipped owner={owner}: {error.GetType().Name}");
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[relations] presence failed character={character.Id}: {error.Message}");
        }
    }

    private RelationEntry WithPresence(RelationEntry entry) =>
        _registry.TryFindOnlineByCharacterId(entry.CharacterId, out var context)
            ? entry with { Online = true, Level = context.Character.Level }
            : entry with { Online = false };

    private Task SendRelationErrorAsync(
        string targetName,
        byte kind,
        sbyte error,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.RelationRequestError(
                _character!.Name,
                targetName,
                kind,
                error),
            cancellationToken,
            "RelationError");

    private Task SendRelationNoteAsync(string text, CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.ServerNote(text),
            cancellationToken,
            "RelationNote");

    /// <summary>
    /// True when the recipient has the sender on their blacklist. A failed read lets
    /// the message through: losing a blacklist check for a moment beats losing chat.
    /// </summary>
    private async Task<bool> IsBlockedFromPrivateChatAsync(
        string recipientName,
        CancellationToken cancellationToken)
    {
        try
        {
            var recipient = await RelationStore.FindActiveByNameAsync(
                _character!.RealmId.Value,
                recipientName,
                cancellationToken);
            return recipient is { } found &&
                   await RelationStore.GetKindAsync(
                       found.CharacterId,
                       _character.Id,
                       cancellationToken) == RelationProtocol.KindBlacklist;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[relations] blacklist read failed: {error.Message}");
            return false;
        }
    }
}
