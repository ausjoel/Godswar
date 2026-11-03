using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task PublishAtlantisCompletionRewardsAsync(AtlantisCompletionEvidence evidence,
        AtlantisCompletionRewardReceipt receipt, CancellationToken cancellationToken)
    {
        var request = evidence.Request ?? throw new InvalidOperationException("Missing completion award evidence.");
        foreach (var reward in receipt.Members)
        {
            Task? completion = null;
            ClientSession? recipient = null;
            lock (_gate)
            {
                // A finisher may already have teleported. The current account
                // and character lease governs projection, not the old map.
                var current = _sessions.Values.FirstOrDefault(context =>
                    context.AccountId == reward.AccountId && context.CharacterId == reward.CharacterId &&
                    context.WorldReady && IsCurrentAccountSession(context.AccountId,
                        context.Session, context.Ownership));
                if (current is null) continue;
                var character = current.Character;
                if (character.MedusaRewardRevision <= reward.RewardRevision)
                {
                    character.MedusaHonorPoints = reward.HonorAfter;
                    character.MedusaRewardRevision = reward.RewardRevision;
                }
                // Only a completed run grants a title; an incomplete settlement
                // pays HardPoints alone.
                if (reward.AwardedTitleId != 0)
                {
                    character.AddOwnedTitle(reward.AwardedTitleId);
                }
                recipient = current.Session;
                var packets = new ReadOnlyMemory<byte>[]
                {
                    PacketBuilder.MedusaDesignationInfo(character.SelectedTitleId, character.OwnedTitleIds),
                    PacketBuilder.RepetitionReward(receipt.Award.HardPoints),
                    PacketBuilder.PlayerDetail(character)
                };
                if (!recipient.TryAdmitExactBatch(packets, out completion)) recipient.Disconnect();
            }
            if (completion is not null && recipient is not null)
                await ObserveAtlantisRewardWriteAsync(recipient, completion);
        }

        var leaderId = _atlantisLeaderCharacterIds.GetValueOrDefault(receipt.WorldInstanceId);
        var name = evidence.Members.FirstOrDefault(member => member.CharacterId == leaderId)?.Name ??
            evidence.Members[0].Name;
        var solo = request.AdmittedMembers.Count == 1;
        var subject = new string(name.Take(32).Select(character => character <= 127 ? character : '?').ToArray());
        // A completed run keeps its original wording; a run that ended before the
        // completion threshold announces the tier it actually earned, with no
        // title.
        var announcement = receipt.Award.TitleId == 0
            ? solo
                ? $"{subject} left Atlantis with +{receipt.Award.HardPoints} HardPoints."
                : $"{subject}'s party left Atlantis with +{receipt.Award.HardPoints} HardPoints each."
            : solo
                // RATES: the amount follows the award (multiplier), not a fixed 2,800.
                ? $"{subject} cleared Atlantis solo! +{receipt.Award.HardPoints.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} HardPoints and the title Deep Sea Hunter."
                : $"{subject}'s party cleared Atlantis! +{receipt.Award.HardPoints.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} HardPoints each and the title Seabed Explorer.";
        var notice = PacketBuilder.CenteredAnnouncement(announcement);
        var writes = new List<(ClientSession Session, Task Write)>();
        lock (_gate)
        {
            foreach (var context in _sessions.Values.Where(context => context.WorldReady &&
                context.RealmId == request.RealmId &&
                IsCurrentAccountSession(context.AccountId, context.Session, context.Ownership)))
            {
                if (context.Session.TryAdmitExact(notice, out var write)) writes.Add((context.Session, write));
                else context.Session.Disconnect();
            }
        }
        foreach (var write in writes) await ObserveAtlantisRewardWriteAsync(write.Session, write.Write);
    }

    private static async Task ObserveAtlantisRewardWriteAsync(ClientSession session, Task write)
    {
        try { await write; }
        catch (Exception) { session.Disconnect(); }
    }
}
