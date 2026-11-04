using System.Text.Json;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition> OpenGiftPackAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, int characterId, int bagSlot, LockedBagItem pack,
        LockedCharacter character, GiftPackDefinition definition, CancellationToken token)
    {
        if (pack.Stack <= 0 || character.Level < definition.MinimumLevel ||
            definition.MaximumLevel > 0 && character.Level > definition.MaximumLevel)
            return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        var rewards = new List<(uint ItemId, int Quantity, short Bound, short StackCap)>();
        await using var genderRead = CreateCommand("SELECT gender FROM public.character_base WHERE id = @characterId;",
            connection, transaction);
        genderRead.Parameters.AddWithValue("characterId", characterId);
        var male = await genderRead.ExecuteScalarAsync(token) is string gender && gender == "male";
        foreach (var listed in GiftPackPolicy.RollRewards(definition, character.Profession))
        {
            var reward = male && listed.MaleItemId != 0 ? listed with { ItemId = listed.MaleItemId } : listed;
            if (!_itemContent.Templates.TryGet(reward.ItemId, out var template))
                return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
            using var stats = JsonDocument.Parse(template.StatsJson);
            var overlap = stats.RootElement.TryGetProperty("Overlap", out var value) &&
                (value.ValueKind == JsonValueKind.Number ? value.TryGetInt16(out var number) ? number : (short)0 :
                    short.TryParse(value.GetString(), out var text) ? text : (short)0) is var parsed && parsed > 0 ? parsed : (short)1;
            rewards.Add((reward.ItemId, reward.Quantity,
                pack.Bound || stats.RootElement.TryGetProperty("BindType", out _) ? (short)1 : (short)0, overlap));
        }
        if (rewards.Count == 0) return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        var bag = await ReadWonderlandSackBagAsync(connection, transaction, characterId, token);
        if (!bag.TryGetValue(checked((short)bagSlot), out var source) || source.Id != pack.ItemId)
            throw new InvalidDataException("The locked gift pack is absent from its bag.");
        // A used-up key leaves the plan, and the reward grant must see its slot as it will be.
        short keySlot = -1;
        WonderlandSackBagRow? keyRow = null;
        if (definition.KeyItemId != 0)
        {
            foreach (var (slot, row) in bag)
            {
                if (row.Item.Id != definition.KeyItemId || row.Item.Stack < 1) continue;
                (keySlot, keyRow) = (slot, row);
                break;
            }
            if (keyRow is null) return new(PetDurableReceiptStatus.UnsupportedItem, KitBagSlot: bagSlot);
        }
        var planned = string.Empty;
        foreach (var (slot, row) in bag)
        {
            if (slot == bagSlot && pack.Stack == 1 || slot == keySlot && row.Item.Stack == 1) continue;
            var item = slot == bagSlot || slot == keySlot ? row.Item with { Stack = checked((short)(row.Item.Stack - 1)) } : row.Item;
            planned = KitBagSlots.SetSlot(planned, slot, item.ToCompactString());
        }
        // The whole pack must fit or nothing is consumed.
        foreach (var reward in rewards)
            if (!KitBagItemGrantPlanner.TryAdd(planned, reward.ItemId, reward.Quantity, reward.StackCap, reward.Bound, out planned))
                return new(PetDurableReceiptStatus.GiftPackBagFull, KitBagSlot: bagSlot);

        var consumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, bagSlot, pack, token);
        var revision = await AdvanceInventoryRevisionAsync(connection, transaction, characterId, character.InventoryRevision, token);
        var mutations = new List<InventoryMutation>
        {
            new(pack.ItemId, consumed.MutationKind, pack.BeforeState, consumed.AfterState, "gift_pack_consumed", revision)
        };
        if (keyRow is not null)
        {
            var key = new LockedBagItem(keyRow.Id, checked((int)keyRow.Item.Id), 0, keyRow.Item.Bound != 0, keyRow.Item.Stack, keyRow.Json);
            var keyConsumed = await ConsumeOneStackItemAsync(connection, transaction, characterId, keySlot, key, token);
            mutations.Add(new(keyRow.Id, keyConsumed.MutationKind, keyRow.Json, keyConsumed.AfterState, "gift_pack_key_consumed", revision));
            if (keyRow.Item.Stack == 1) bag.Remove(keySlot);
            else bag[keySlot] = keyRow with { Item = keyRow.Item with { Stack = checked((short)(keyRow.Item.Stack - 1)) } };
        }
        if (definition.TitleId != 0)
        {
            await using var title = CreateCommand("""
                INSERT INTO public.character_title_grants(character_id,title_id,granted_by,note)
                VALUES(@characterId,@titleId,'item',@note) ON CONFLICT DO NOTHING;
                """, connection, transaction);
            title.Parameters.AddWithValue("characterId", characterId);
            title.Parameters.AddWithValue("titleId", definition.TitleId);
            title.Parameters.AddWithValue("note", $"bag item {pack.PropId}");
            await title.ExecuteNonQueryAsync(token);
        }
        foreach (var itemId in rewards.Select(r => r.ItemId).Distinct())
            mutations.AddRange(await GrantWonderlandSackRewardAsync(connection, transaction, characterId,
                bag, planned, itemId, bagSlot, pack.Stack == 1, revision, token, "gift_pack_reward"));
        var evidence = new GiftPackOpenEvidence(pack.ItemId, checked((uint)pack.PropId), bagSlot,
            GiftPackOpenEvidence.Format(rewards.Select(r => (r.ItemId, r.Quantity, r.Bound))));
        if (!evidence.IsValid) throw new InvalidDataException("Invalid gift pack settlement evidence.");
        return new(PetDurableReceiptStatus.GiftPackOpened, KitBagSlot: bagSlot, InventoryMutations: mutations, GiftPack: evidence);
    }
}
