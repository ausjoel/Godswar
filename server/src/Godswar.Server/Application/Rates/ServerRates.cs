using System.Collections.Frozen;

namespace Godswar.Server.Application.Rates;

// RATES: server-wide multipliers and limits (GodsWar-2.51/Polish patch).
//
// One table (public.server_rates) holds every value; the game server reads it
// at start-up and hot-reloads it (poll every few seconds, or POST /rates/reload
// on the loopback management port). Every multiplier defaults to 1 and every
// limit to the value the stock server hard-codes, so an empty or missing table
// leaves gameplay byte-for-byte unchanged. Gameplay code reads
// ServerRates.Current at the moment a reward or limit is decided; nothing is
// cached elsewhere, so a reload applies to the next kill / hand-in / tick.

internal enum ServerRateKind
{
    /// <summary>A factor applied to a stock amount or chance (1 = stock).</summary>
    Multiplier = 1,

    /// <summary>An absolute limit that replaces a stock constant.</summary>
    Absolute = 2
}

internal sealed record ServerRateDefinition(
    string Key,
    string Category,
    string Label,
    string Unit,
    ServerRateKind Kind,
    decimal DefaultValue,
    decimal MinimumValue,
    decimal MaximumValue,
    decimal Step,
    int SortOrder,
    string AppliesTo,
    string Description);

internal static class ServerRateKeys
{
    public const string MonsterExperience = "exp.monster";
    public const string MonsterTalentExperience = "tp.monster";
    public const string QuestExperience = "exp.quest";
    public const string QuestTalent = "tp.quest";
    public const string EventExperience = "exp.events";
    public const string PetMonsterExperience = "pet.exp_monster";
    public const string ZodiacEnergy = "zodiac.energy";
    public const string AtlantisHardPoints = "instance.atlantis_hardpoints";
    public const string MedusaHonor = "instance.medusa_honor";
    public const string FarmKillPoints = "instance.farm_points";
    public const string MovementSpeed = "move.speed";
    public const string DropChance = "drop.chance";
    public const string DropQuantity = "drop.quantity";
    public const string DropMaximumItems = "drop.max_items";
    // ATTR: rarity shifts for random-attribute drops (loot_roll_profiles).
    public const string DropRareAttributes = "drop.attr_rare";
    public const string DropQualityUp = "drop.quality_up";
    public const string DropGradeUp = "drop.grade_up";
    public const string QuestSilver = "money.quest_silver";
    public const string QuestGold = "money.quest_gold";
    public const string SellPrice = "money.sell_price";
    public const string ForgeSuccess = "forge.success";
    public const string HolyStoneSuccess = "forge.holystone_success";
    public const string HolyBoxDailyLimit = "holybox.daily_limit";
    public const string HolyBoxPerStoreMaximum = "holybox.per_store_max";
    public const string HolyBoxCapacity = "holybox.capacity";
    public const string HolyBoxMinimumLevel = "holybox.min_level";
    // ROUND2: costs, item EXP, daily caps and event windows.
    public const string TalentUpgradeCost = "cost.talent";
    public const string ZodiacLevelCost = "cost.zodiac_level";
    public const string ZodiacGridCost = "cost.zodiac_grid";
    public const string FactionCrierExperience = "exp.faction_crier";
    public const string FactionCrierTalent = "tp.faction_crier";
    public const string ExperiencePill = "exp.pill";
    public const string PetExperienceItems = "pet.exp_items";
    public const string LuckyGodsDaily = "cap.lucky_gods_daily";
    public const string WishingPoolDaily = "cap.wishing_pool_daily";
    public const string ZeusGiftsWeekday = "cap.zeus_gifts_weekday";
    public const string ZeusGiftsWeekend = "cap.zeus_gifts_weekend";
    public const string ZeusExchangesPerWeek = "cap.zeus_exchanges_week";
    public const string QuestDailyCompletions = "cap.quest_daily";
    public const string PindusAlwaysOpen = "event.pindus_always_open";
    public const string NiMiniAlwaysOpen = "event.nimini_always_open";
    public const string BattlefieldWindowMinutes = "event.battlefield_minutes";
    public const string BattlefieldShiftHours = "event.battlefield_shift_hours";
    public const string WonderlandCloseHour = "event.wonderland_close_hour";
    public const string ZeusStoneAnyDay = "event.zeus_stone_any_day";
    public const string SpyInvasion = "event.spy_invasion";
}

internal static class ServerRateCatalog
{
    public const string CharacterCategory = "Character EXP & Talent";
    public const string PetCategory = "Pets";
    public const string ZodiacCategory = "Zodiac";
    public const string InstanceCategory = "Instances";
    public const string DropCategory = "Monster drops";
    public const string MoneyCategory = "Money";
    public const string ForgeCategory = "Forging";
    public const string HolyBoxCategory = "Holy Box (Master Vestment Forger)";
    public const string CostCategory = "Upgrade costs"; // ROUND2
    public const string DailyCategory = "Daily and weekly limits"; // ROUND2
    public const string EventCategory = "Event times"; // ROUND2

    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        Multiplier(
            ServerRateKeys.MonsterExperience, CharacterCategory, 10,
            "Monster kill EXP", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Progression.cs PrepareMonsterKillRewardAsync",
            "Fighter EXP from a monster kill. Applied after the stock tier table " +
            "(20..279 x4), the 20-level gap rule, EXP potions/boost statuses and " +
            "the database global multiplier (monster_reward_settings, 1-5x, " +
            "restart). One kill is capped at 100,000,000 EXP."),
        Multiplier(
            ServerRateKeys.MonsterTalentExperience, CharacterCategory, 20,
            "Monster kill Talent EXP (TP)", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Progression.cs PrepareMonsterKillRewardAsync",
            "Talent EXP from a monster kill: stock 10 per eligible kill, and " +
            "100 Talent EXP = 1 Talent Point (TP). Applied after Talent boost " +
            "statuses. 2 = a Talent Point every 5 kills instead of 10."),
        Multiplier(
            ServerRateKeys.QuestExperience, CharacterCategory, 30,
            "Quest reward EXP", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "EXP paid when a quest is handed in (after the quest-appraisal " +
            "+10% bonus and any GM-tool quest_reward override)."),
        Multiplier(
            ServerRateKeys.QuestTalent, CharacterCategory, 40,
            "Quest reward Talent", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Talent amount paid when a quest is handed in (credited as Talent " +
            "EXP, 100 = 1 TP, exactly as the stock hand-in does)."),
        Multiplier(
            ServerRateKeys.EventExperience, CharacterCategory, 50,
            "Event NPC EXP & Talent", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.LuckyGods.cs, Game/GameClientHandler.ZeusGift.cs",
            "EXP paid by the Lucky Gods wish pool (210,000..13,490,000 per " +
            "claim, 10 wishes a day) and EXP + Talent paid by Zeus' Gift " +
            "praying stones (8,000 EXP and 1 Talent each, x2 at weekends)."),
        Multiplier(
            ServerRateKeys.PetMonsterExperience, PetCategory, 10,
            "Pet EXP from monster kills", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Progression.cs -> PostgresMonsterRewardExtrasStore",
            "EXP credited to the summoned pet for a kill (stock = the monster's " +
            "tier EXP, plus pet EXP potions). Pets level up with Upgrade from " +
            "that pool (1..120, 252,947,820 EXP in total), so 3 = levelling " +
            "three times as fast. Capped at 100,000,000 per kill."),
        Multiplier(
            ServerRateKeys.ZodiacEnergy, ZodiacCategory, 10,
            "Zodiac energy gain", 0m, 1_000_000m, // ROUND2: max
            "State/ZodiacEnergyAccrual.cs AddEnergyX100",
            "Zodiac energy from online time (stock: 10 per 5-minute tick, 20 " +
            "per tick for the first 3 hours of each realm day, 1 hour of " +
            "compensation after a missed day). Still capped by the storage " +
            "limit of the character's zodiac level."),
        Multiplier(
            ServerRateKeys.AtlantisHardPoints, InstanceCategory, 10,
            "Atlantis honour (HardPoints)", 0m, 10_000m, // ROUND2: max
            "Application/WorldInstances/AtlantisCompletionRewardContracts.cs",
            "HardPoints (shown as Honour) paid when an Atlantis run ends: 200.." +
            "2,400 by team score, 2,800 + title on completion."),
        Multiplier(
            ServerRateKeys.MedusaHonor, InstanceCategory, 20,
            "Medusa Island honour", 0m, 10_000m, // ROUND2: max
            "Application/WorldInstances/MedusaCompletionRewardPolicy.cs",
            "Honour paid when a Medusa Island run ends (database tiers " +
            "medusa_completion_reward_rules: 300..1,350 by score, 975..2,250 " +
            "by clear time). Titles are unchanged."),
        Multiplier(
            ServerRateKeys.FarmKillPoints, InstanceCategory, 30,
            "Lelantine Farm kill points", 0m, 1_000m, // ROUND2: max
            "Game/GameClientHandler.LelantineFarmKills.cs",
            "Points a Lelantine Farm kill scores (1 normal, 10 elite, 1,000 " +
            "Cerberus). Egg donations are not changed."),
        Multiplier(
            ServerRateKeys.DropChance, DropCategory, 10,
            "Drop chance", 0m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs, " +
            "Application/WorldInstances/MedusaMonsterContentSnapshot.cs",
            "Chance of every loot rule of field/dungeon monsters " +
            "(monster_loot_rules) and Medusa monsters. Capped at 100% per rule."),
        Multiplier(
            ServerRateKeys.DropQuantity, DropCategory, 20,
            "Drop quantity", 1m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs, " +
            "Application/WorldInstances/MedusaMonsterContentSnapshot.cs",
            "Quantity of each dropped item (capped at 255 per drop; pickup " +
            "splits it into stacks)."),
        Multiplier(
            ServerRateKeys.DropMaximumItems, DropCategory, 30,
            "Maximum items per kill", 1m, 1_000_000m, // ROUND2: max
            "Application/World/Content/MonsterLootContentSnapshot.cs",
            "How many different items one field-monster kill may drop " +
            "(monster_loot_tables.maximum_drops, capped at 32 and at the " +
            "monster's number of loot rules)."),
        // ATTR: random attributes, quality and grade on profile drops.
        Multiplier(
            ServerRateKeys.DropRareAttributes, DropCategory, 40,
            "Rare attribute rolls (3-4 attributes)", 0m, 100m,
            "Application/World/Content/LootRollProfiles.cs",
            "Scales the weight of 3 and 4 random attributes on gear drops " +
            "whose loot rule names a roll profile (loot_roll_profiles). " +
            "2 = twice as likely, 0 = never. Rules without a profile are " +
            "not affected."),
        Multiplier(
            ServerRateKeys.DropQualityUp, DropCategory, 50,
            "Higher quality on gear drops", 0m, 10m,
            "Application/World/Content/LootRollProfiles.cs",
            "Scales the profile's quality step (the chance of each next " +
            "quality level relative to the one below) on roll-profile gear " +
            "drops. Still capped by the profile's quality range."),
        Multiplier(
            ServerRateKeys.DropGradeUp, DropCategory, 60,
            "Higher grade on gear drops", 0m, 10m,
            "Application/World/Content/LootRollProfiles.cs",
            "Scales the profile's grade step on roll-profile gear drops. " +
            "Still capped by the profile's grade range."),
        Multiplier(
            ServerRateKeys.QuestSilver, MoneyCategory, 10,
            "Quest reward Silver", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Silver paid when a quest is handed in."),
        Multiplier(
            ServerRateKeys.QuestGold, MoneyCategory, 20,
            "Quest reward Gold", 0m, 1_000_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs quest hand-in",
            "Gold (premium currency, character_base.\"Stone\") paid by quests " +
            "that pay Gold."),
        Multiplier(
            ServerRateKeys.SellPrice, MoneyCategory, 30,
            "NPC sell price", 0m, 5m, // ROUND2: max
            "Infrastructure/Inventory/PostgresCapitalShopPurchaseStore.CapitalShopSale.cs",
            "Silver received when selling to a capital NPC shop (stock 20% of " +
            "the item's value). The client's own price tooltip still shows " +
            "the stock value."),
        Multiplier(
            ServerRateKeys.ForgeSuccess, ForgeCategory, 10,
            "Forge success chance", 0m, 100m, // ROUND2: max
            "State/EquipmentForgeCalculator.cs",
            "Success chance of Ruby / Sapphire / Emerald equipment forging " +
            "(stock base chance + material and odds-item bonuses), capped at " +
            "100%."),
        Multiplier(
            ServerRateKeys.HolyStoneSuccess, ForgeCategory, 20,
            "Holy stone upgrade success", 0m, 100m, // ROUND2: max
            "State/HolyStoneUpgradePolicy.cs",
            "Holy stone upgrade chance (stock 90% / 25% / 10% for levels " +
            "1-3 / 4-6 / 7-9, +10% with a Goddess stone or Signet), capped at " +
            "100%."),
        Absolute(
            ServerRateKeys.HolyBoxDailyLimit, HolyBoxCategory, 10,
            "Daily EXP that can be stored", "EXP", 2_000_000_000m, 0m,
            1_000_000_000_000m, 1_000_000m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs, .Quota.cs",
            "Fighter EXP one account may put into Holy Boxes per realm day " +
            "(Asia/Singapore midnight). An active battle_pass entitlement " +
            "still bypasses it. The client's counter display saturates at " +
            "214,748,364; the server enforces the exact value."),
        Absolute(
            ServerRateKeys.HolyBoxPerStoreMaximum, HolyBoxCategory, 20,
            "Maximum EXP per Store action", "EXP", 400_000_000m, 1m,
            2_147_483_647m, 1_000_000m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs",
            "Largest amount one Storing EXP request may move (typed amount or " +
            "Store Maximum)."),
        Multiplier(
            ServerRateKeys.HolyBoxCapacity, HolyBoxCategory, 30,
            "Holy Box capacity", 0.1m, 1_000_000m, // ROUND2: max
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Store.cs",
            "Capacity of every Holy Box (stock I 100,000 / II 1,000,000 / III " +
            "10,000,000 / IV 100,000,000 / V 400,000,000). 5 = Holy Box V " +
            "holds 2,000,000,000 (the item EXP field is a 32-bit integer)."),
        Absolute(
            ServerRateKeys.HolyBoxMinimumLevel, HolyBoxCategory, 40,
            "Minimum level (all 4 forger pages)", "level", 70m, 1m, 200m, 1m,
            "Infrastructure/Inventory/PostgresHolySuitCommandExecutor.Planning.cs",
            "Character level required for Storing / Transferring EXP, Ware " +
            "Consuming and Transform EXP. Gear still needs level 70."),
        // ROUND2: upgrade costs (0 = free).
        Multiplier(
            ServerRateKeys.TalentUpgradeCost, CostCategory, 10,
            "Talent upgrade cost", 0m, 1_000m, // ROUND2: max
            "Application/Talents/TalentProgression.cs CalculateUpgradeCost",
            "Talent Points one talent rank costs (stock 1..10 for ranks 1-10, " +
            "rising to several thousand near rank 100). 0.5 = half price. The " +
            "client's talent window still prints the stock price."),
        Multiplier(
            ServerRateKeys.ZodiacLevelCost, CostCategory, 20,
            "Zodiac level-up energy cost", 0m, 1_000m, // ROUND2: max
            "State/ZodiacLevelUpgrade.cs ZodiacLevelUpgradeCatalog",
            "Zodiac energy one zodiac level costs (stock 720 for level 2 up to " +
            "millions near level 30). The zodiac window prints the stock cost " +
            "from the client's own Constellation.lua."),
        Multiplier(
            ServerRateKeys.ZodiacGridCost, CostCategory, 30,
            "Zodiac grid upgrade cost", 0m, 1_000m, // ROUND2: max
            "State/ZodiacSkillGridUpgrade.cs ZodiacSkillGridUpgradeCatalog",
            "Zodiac energy AND Talent Points one zodiac skill-grid level costs. " +
            "The grid tooltip shows the stock cost."),
        // ROUND2: other EXP sources.
        Multiplier(
            ServerRateKeys.FactionCrierExperience, CharacterCategory, 60,
            "Faction Crier EXP", 0m, 1_000_000m, // ROUND2: max
            "Application/FactionCrier/FactionCrierRewardPolicy.cs",
            "EXP paid when nameplates are exchanged at the Faction Crier."),
        Multiplier(
            ServerRateKeys.FactionCrierTalent, CharacterCategory, 70,
            "Faction Crier Talent Points", 0m, 1_000_000m, // ROUND2: max
            "Application/FactionCrier/FactionCrierRewardPolicy.cs",
            "Talent Points paid when nameplates are exchanged at the Faction " +
            "Crier."),
        Multiplier(
            ServerRateKeys.ExperiencePill, CharacterCategory, 80,
            "EXP pill (item 4174)", 0.05m, 1_000_000m, // ROUND2: max
            "State/PlayerExperienceItemPolicy.cs",
            "EXP one EXP pill gives (stock 1,000,000). The old EXP scrolls " +
            "4620-4623 do nothing on this server at all (not implemented " +
            "upstream), so they have no setting."),
        Multiplier(
            ServerRateKeys.PetExperienceItems, PetCategory, 20,
            "Pet EXP items (Morning Dew)", 0.05m, 1_000_000m, // ROUND2: max
            "Infrastructure/Pets/PostgresPetDurableCommandExecutor.ExperienceItem.cs",
            "EXP a Morning Dew (10130-10134, 10140-10144) gives the summoned " +
            "pet. Capped at the pet's 4,294,967,295 EXP field."),
        // ROUND2: daily and weekly limits.
        Absolute(
            ServerRateKeys.LuckyGodsDaily, DailyCategory, 10,
            "Lucky Gods wishes per day", "wishes", 10m, 0m, 1000m, 1m,
            "Application/LuckyGods/LuckyGodsWishContracts.cs",
            "Wishes one character may make at the Lucky Gods per realm day. " +
            "The start-up step loosens the stock database CHECK (at most 10) " +
            "to 'at least 0' so a higher value can be stored."),
        Absolute(
            ServerRateKeys.WishingPoolDaily, DailyCategory, 20,
            "Wishing Pool uses per day", "uses", 3m, 0m, 1000m, 1m,
            "Infrastructure/WishingPool/PostgresWishingPoolUsageStore.cs",
            "Wishing Pool uses per character per realm day. The 1-hour wait " +
            "between uses is unchanged."),
        Absolute(
            ServerRateKeys.ZeusGiftsWeekday, DailyCategory, 30,
            "Zeus' Gift deliveries per weekday", "gifts", 10m, 0m, 12m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs GiftLimit",
            "Gift deliveries per character on Monday-Friday. 12 is the most " +
            "the gift round table has."),
        Absolute(
            ServerRateKeys.ZeusGiftsWeekend, DailyCategory, 40,
            "Zeus' Gift deliveries per weekend day", "gifts", 12m, 0m, 12m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs GiftLimit",
            "Gift deliveries per character on Saturday and Sunday."),
        Absolute(
            ServerRateKeys.ZeusExchangesPerWeek, DailyCategory, 50,
            "Zeus' Gift base exchanges per week", "exchanges", 5m, 0m, 1000m,
            1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs ExchangesForStones",
            "Dust exchanges every character gets each week before stones are " +
            "deposited (every 5 deposited stones still add one)."),
        Multiplier(
            ServerRateKeys.QuestDailyCompletions, DailyCategory, 60,
            "Repeatable quest daily limit", 1m, 1_000m, // ROUND2: max
            "Game/GameClientHandler.Quests.cs HasDailyAllowance",
            "How many times a repeatable quest may be completed per day: " +
            "each quest's own daily cap times this value. One-time quests " +
            "stay one-time."),
        Multiplier(
            ServerRateKeys.MovementSpeed, CharacterCategory, 90,
            "Movement speed, all players", 0.1m, 10m,
            "Game/GameSessionRegistry.Operator.cs ApplyOperatorModifiers",
            "How fast every player runs. Reaches players online within 15 " +
            "seconds. Multiplies with mounts, buffs and a GM's /speed."),
        // ROUND2: event windows (server time = the realm calendar).
        Absolute(
            ServerRateKeys.PindusAlwaysOpen, EventCategory, 10,
            "Pindus battlefield always open", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "1 = the Pindus Mountains battlefield can be entered at any time. " +
            "0 = stock windows (Wed/Fri 21:00, Sat/Sun 08:00 and 15:00/21:00)."),
        Absolute(
            ServerRateKeys.NiMiniAlwaysOpen, EventCategory, 20,
            "Ni Mini Valley always open", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "1 = Ni Mini Valley can be entered at any time. 0 = stock windows " +
            "(Fri 19:00, Sat 07:00, Sun 19:00)."),
        Absolute(
            ServerRateKeys.BattlefieldWindowMinutes, EventCategory, 30,
            "Battlefield entry window length", "minutes", 45m, 5m, 1440m, 5m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "How long each battlefield entry window stays open after its start " +
            "time."),
        Absolute(
            ServerRateKeys.BattlefieldShiftHours, EventCategory, 40,
            "Battlefield start time shift", "hours", 0m, -12m, 12m, 1m,
            "Application/WorldInstances/BattlefieldSchedulePolicy.cs",
            "Moves every battlefield start time by this many hours (for " +
            "example -2 makes the 21:00 Pindus window start at 19:00 server " +
            "time = 21:00 AEST). The client's event list still prints the " +
            "stock times."),
        Absolute(
            ServerRateKeys.WonderlandCloseHour, EventCategory, 50,
            "Wonderland closes at", "hour", 23m, 1m, 24m, 1m,
            "Application/WorldInstances/LegacyInstanceAdmissionPolicy.cs",
            "Wonderland can be entered every day until this hour (server " +
            "time). 24 = open all day."),
        Absolute(
            ServerRateKeys.SpyInvasion, EventCategory, 70,
            "Spy invasion waves", "0/1", 0m, 0m, 1m, 1m,
            "Application/WorldInstances/SpyInvasionPolicy.cs",
            "1 = the spies in Athens and Sparta appear only Monday, " +
            "Wednesday and Saturday at 19:00 server time, in 5-minute waves " +
            "until 19:30. 0 = spies are always there."),
        Absolute(
            ServerRateKeys.ZeusStoneAnyDay, EventCategory, 60,
            "Zeus' Gift stones any day", "0/1", 0m, 0m, 1m, 1m,
            "Application/ZeusGift/ZeusGiftPolicy.cs",
            "1 = praying stones can be handed in on any day, not only " +
            "Monday-Friday (same as the GODSWAR_ZEUS_STONE_ANY_DAY switch).")
    ];

    private static readonly FrozenDictionary<string, ServerRateDefinition>
        ByKey = Definitions.ToFrozenDictionary(
            static definition => definition.Key,
            StringComparer.Ordinal);

    public static bool TryGet(
        string key,
        out ServerRateDefinition definition) =>
        ByKey.TryGetValue(key, out definition!);

    public static ServerRateDefinition Get(string key) =>
        ByKey.TryGetValue(key, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "Unknown server rate.");

    private static ServerRateDefinition Multiplier(
        string key,
        string category,
        int sortOrder,
        string label,
        decimal minimum,
        decimal maximum,
        string appliesTo,
        string description) =>
        new(
            key,
            category,
            label,
            "x",
            ServerRateKind.Multiplier,
            1m,
            minimum,
            maximum,
            0.05m,
            sortOrder,
            appliesTo,
            description);

    private static ServerRateDefinition Absolute(
        string key,
        string category,
        int sortOrder,
        string label,
        string unit,
        decimal defaultValue,
        decimal minimum,
        decimal maximum,
        decimal step,
        string appliesTo,
        string description) =>
        new(
            key,
            category,
            label,
            unit,
            ServerRateKind.Absolute,
            defaultValue,
            minimum,
            maximum,
            step,
            sortOrder,
            appliesTo,
            description);
}

/// <summary>
/// One immutable set of rates. Values outside the compiled bounds are clamped
/// when the snapshot is built, so gameplay code never sees an unsafe value.
/// </summary>
internal sealed class ServerRatesSnapshot
{
    private readonly FrozenDictionary<string, decimal> _values;

    private ServerRatesSnapshot(
        FrozenDictionary<string, decimal> values,
        long revision,
        DateTimeOffset updatedAtUtc,
        string updatedBy,
        string source,
        DateTimeOffset loadedAtUtc)
    {
        _values = values;
        Revision = revision;
        UpdatedAtUtc = updatedAtUtc;
        UpdatedBy = updatedBy;
        Source = source;
        LoadedAtUtc = loadedAtUtc;
    }

    public static ServerRatesSnapshot Default { get; } = new(
        ServerRateCatalog.Definitions.ToFrozenDictionary(
            static definition => definition.Key,
            static definition => definition.DefaultValue,
            StringComparer.Ordinal),
        0,
        DateTimeOffset.UnixEpoch,
        "compiled-default",
        "compiled-default",
        DateTimeOffset.UnixEpoch);

    public long Revision { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public string UpdatedBy { get; }

    /// <summary><c>compiled-default</c>, <c>database</c> or <c>test</c>.</summary>
    public string Source { get; }

    public DateTimeOffset LoadedAtUtc { get; }

    public IReadOnlyDictionary<string, decimal> Values => _values;

    public IReadOnlyList<string> NonDefaultKeys =>
        ServerRateCatalog.Definitions
            .Where(definition => Get(definition.Key) != definition.DefaultValue)
            .Select(static definition => definition.Key)
            .ToArray();

    public static ServerRatesSnapshot Create(
        IEnumerable<KeyValuePair<string, decimal>> values,
        long revision,
        DateTimeOffset updatedAtUtc,
        string updatedBy,
        string source,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var merged = ServerRateCatalog.Definitions.ToDictionary(
            static definition => definition.Key,
            static definition => definition.DefaultValue,
            StringComparer.Ordinal);
        foreach (var (key, raw) in values)
        {
            if (!ServerRateCatalog.TryGet(key, out var definition))
            {
                warnings?.Add($"unknown rate '{key}' ignored");
                continue;
            }

            var value = definition.Kind == ServerRateKind.Absolute
                ? decimal.Truncate(raw)
                : raw;
            if (value < definition.MinimumValue ||
                value > definition.MaximumValue)
            {
                var clamped = Math.Clamp(
                    value,
                    definition.MinimumValue,
                    definition.MaximumValue);
                warnings?.Add(
                    $"rate '{key}' = {raw} is outside " +
                    $"{definition.MinimumValue}..{definition.MaximumValue}; " +
                    $"using {clamped}");
                value = clamped;
            }

            merged[key] = value;
        }

        return new ServerRatesSnapshot(
            merged.ToFrozenDictionary(StringComparer.Ordinal),
            Math.Max(0, revision),
            updatedAtUtc,
            string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy,
            string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            DateTimeOffset.UtcNow);
    }

    public decimal Get(string key) =>
        _values.TryGetValue(key, out var value)
            ? value
            : ServerRateCatalog.Get(key).DefaultValue;

    public bool IsDefault(string key) =>
        Get(key) == ServerRateCatalog.Get(key).DefaultValue;

    /// <summary>
    /// Scales a positive stock amount, truncating once and saturating at
    /// <paramref name="maximum"/>. A rate of exactly 1 returns the input
    /// unchanged, so stock behaviour (including stock edge cases) is kept.
    /// </summary>
    public int Multiply(string key, int value, int maximum = int.MaxValue)
    {
        var rate = Get(key);
        if (rate == 1m || value <= 0)
        {
            return value;
        }

        var scaled = decimal.Truncate(value * rate);
        if (scaled <= 0)
        {
            return 0;
        }

        return scaled >= maximum ? maximum : (int)scaled;
    }

    public long Multiply(string key, long value, long maximum = long.MaxValue)
    {
        var rate = Get(key);
        if (rate == 1m || value <= 0)
        {
            return value;
        }

        var scaled = decimal.Truncate(value * rate);
        if (scaled <= 0)
        {
            return 0;
        }

        return scaled >= maximum ? maximum : (long)scaled;
    }

    /// <summary>Scales a 0..100 success chance and keeps it in 0..100.</summary>
    public int MultiplyPercent(string key, int percent)
    {
        var rate = Get(key);
        if (rate == 1m)
        {
            return percent;
        }

        return (int)Math.Clamp(
            decimal.Truncate(Math.Max(0, percent) * rate),
            0m,
            100m);
    }

    /// <summary>Scales a 0..10000 chance in basis points.</summary>
    public int MultiplyBasisPoints(string key, int basisPoints)
    {
        var rate = Get(key);
        if (rate == 1m)
        {
            return basisPoints;
        }

        return (int)Math.Clamp(
            decimal.Truncate(Math.Max(0, basisPoints) * rate),
            0m,
            10_000m);
    }

    /// <summary>An absolute limit as a whole number.</summary>
    public long Absolute(string key) => (long)decimal.Truncate(Get(key));
}

internal static class ServerRates
{
    private static ServerRatesSnapshot _current = ServerRatesSnapshot.Default;

    public static ServerRatesSnapshot Current => Volatile.Read(ref _current);

    public static void Install(ServerRatesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    public static void ResetToDefaults() => Install(ServerRatesSnapshot.Default);

    /// <summary>
    /// Management hooks registered by the runtime that owns the database
    /// connection, so the loopback management server does not depend on
    /// Infrastructure. Both return a JSON document.
    /// </summary>
    public static Func<string>? DescribeHook { get; set; }

    public static Func<CancellationToken, Task<string>>? ReloadHook { get; set; }
}
