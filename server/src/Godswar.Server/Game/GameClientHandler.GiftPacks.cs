using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool IsGiftPackProjection(PetDurableReceipt receipt) =>
        receipt.Status is PetDurableReceiptStatus.GiftPackOpened or PetDurableReceiptStatus.GiftPackBagFull ||
        receipt.Status == PetDurableReceiptStatus.ConsumableCooldownActive && _character is not null &&
        receipt.KitBagSlot is >= 0 and < 96 && GiftPackPolicy.TryResolve(RequireItemContent().Templates,
            KitBagSlots.GetItemId(_character.KitBag, receipt.KitBagSlot), out _);

    private async Task<bool> SendGiftPackProjectionAsync(PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition, string bagBefore, CancellationToken token)
    {
        if (_character is null) return false;
        if (TryHoldGiftPackProjection(receipt, disposition, bagBefore)) return true;
        IReadOnlyList<WonderlandChestItemReward> rewards = [];
        if (receipt.Succeeded && disposition == PetDurableExecutionDisposition.Committed)
        {
            var evidence = receipt.GiftPack ??
                throw new InvalidDataException("A committed gift pack requires its durable evidence.");
            rewards = evidence.Rewards.Select(r => new WonderlandChestItemReward(r.ItemId, checked((short)r.Quantity), r.Bound)).ToArray();
        }
        await SendWonderlandRewardProjectionAsync(bagBefore, rewards, token);
        if (receipt.GiftPack is { } opened &&
            GiftPackPolicy.TryResolve(RequireItemContent().Templates, opened.PackTemplateId, out var pack) && pack.TitleId != 0)
            await _registry.PublishItemTitleAsync(_session, checked((uint)pack.TitleId), 0, token);
        return true;
    }
}
