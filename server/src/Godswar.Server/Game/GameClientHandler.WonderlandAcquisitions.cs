using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendWonderlandRewardProjectionAsync(string bagBefore,
        IReadOnlyList<WonderlandChestItemReward> freshRewards, CancellationToken token)
    {
        if (_character is null) return;
        var packets = BuildWonderlandRewardProjection(_character, bagBefore, freshRewards);
        token.ThrowIfCancellationRequested();
        // An admitted batch must finish cleanup/restoration even if the caller
        // cancels afterward. Other inventory writes cannot split this sequence.
        if (!_session.TryAdmitExactBatch(packets.Select(packet => (ReadOnlyMemory<byte>)packet).ToArray(), out var write))
        {
            _session.Disconnect();
            throw new IOException("The committed Wonderland inventory projection could not be admitted.");
        }
        await write;
    }

    internal static IReadOnlyList<byte[]> BuildWonderlandRewardProjection(GameCharacter character,
        string bagBefore, IReadOnlyList<WonderlandChestItemReward> freshRewards)
    {
        var packets = PacketBuilder.KitBagMutationDeletionAcknowledgements(bagBefore, character.KitBag).ToList();
        if (freshRewards.Count > 0)
        {
            // Use the same native acquisition as Online Award. It both shows
            // the item notification and temporarily mutates the native bag.
            // Consumables first merge eligible page0 stacks, then put any
            // remainder into its first free slot. Reserve slot0 even in a full
            // bag; each <=99 reward fits there without advancing to page1.
            // Cleanup removes that remainder; final hydration restores merges.
            var scratchClear = PacketBuilder.StorageItemKitBagDelete(0);
            if (!packets.Any(packet => packet.AsSpan().SequenceEqual(scratchClear))) packets.Add(scratchClear);
            foreach (var reward in freshRewards)
            {
                if (reward.ItemId == 0 || reward.Quantity <= 0 || reward.Bound is < 0 or > 1)
                    throw new InvalidDataException("Wonderland acquisition requires a valid committed item reward.");
                var item = Enumerable.Range(0, KitBagItemGrantPlanner.SlotCount)
                    .Select(slot => KitBagSlots.GetItem(character.KitBag, slot))
                    .FirstOrDefault(candidate => candidate.Id == reward.ItemId && candidate.Bound == reward.Bound);
                // A newer snapshot may already have consumed the grant. The
                // receipt still describes the fresh award, while final hydration
                // must retain that newer inventory. The grant planner uses Q1/G1.
                if (item.IsEmpty) item = CompactItemEntry.Empty with
                    { Id = reward.ItemId, Quality = 1, Grade = 1, Bound = reward.Bound };
                var remaining = (int)reward.Quantity;
                while (remaining > 0)
                {
                    var quantity = checked((short)Math.Min(remaining, 99));
                    packets.Add(PacketBuilder.SystemAddItemWithAcquisitionLog(item with { Stack = quantity }));
                    packets.Add(scratchClear);
                    remaining -= quantity;
                }
            }
        }
        // Empty10033 records cannot remove an optimistic item; all transient
        // acquisitions above have already been evicted before the final snapshot.
        packets.AddRange(PacketBuilder.KitBagDetailPages(character));
        packets.AddRange(PacketBuilder.KitBagSlotIndexes(character));
        return packets;
    }
}
