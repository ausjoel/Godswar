using System.Collections.Frozen;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal readonly record struct StatusExperienceBoostDefinition(
    uint ItemId,
    int SkillId,
    int StatusId,
    int Kind,
    int BonusBasisPoints,
    long OnlineTicks);

/// <summary>
/// Experience boost items the client lists under StatusMagic: the talent elixirs and Passion Rose.
/// Pairs and amounts are Magic.ini Status= and Status.ini Values/Time. Online time, as the other potion families.
/// </summary>
internal static class StatusExperienceBoostPolicy
{
    private const long MaximumOnlineTicks = 24 * TimeSpan.TicksPerHour;

    private static readonly FrozenDictionary<uint, StatusExperienceBoostDefinition> ByItemId =
        new StatusExperienceBoostDefinition[]
        {
            new(4523, 4741, 580, ExperienceBoostKinds.Talent, 5_000, TimeSpan.TicksPerHour),
            new(4524, 4742, 581, ExperienceBoostKinds.Talent, 10_000, TimeSpan.TicksPerHour),
            new(4525, 4743, 582, ExperienceBoostKinds.Talent, 20_000, TimeSpan.TicksPerHour),
            new(4526, 4744, 583, ExperienceBoostKinds.Talent, 30_000, TimeSpan.TicksPerHour),
            new(4527, 4745, 584, ExperienceBoostKinds.Talent, 40_000, TimeSpan.TicksPerHour),
            new(4528, 4748, 509, ExperienceBoostKinds.Talent, 10_000, 8 * TimeSpan.TicksPerHour),
            new(4536, 4761, 587, ExperienceBoostKinds.Talent, 5_000, 8 * TimeSpan.TicksPerHour),
            new(4537, 4762, 588, ExperienceBoostKinds.Talent, 20_000, 8 * TimeSpan.TicksPerHour),
            new(4538, 4763, 589, ExperienceBoostKinds.Talent, 30_000, 8 * TimeSpan.TicksPerHour),
            new(4504, 4812, 570, ExperienceBoostKinds.Consumable, 10_000, 2 * TimeSpan.TicksPerHour),
            new(4507, 3128, 512, ExperienceBoostKinds.Consumable, 1_000, 30 * TimeSpan.TicksPerMinute)
        }.ToFrozenDictionary(definition => definition.ItemId);

    public static bool TryResolveItem(IItemTemplateCatalog templates, uint itemId,
        out StatusExperienceBoostDefinition definition) =>
        ByItemId.TryGetValue(itemId, out definition) &&
        BagConsumableItemStats.TryResolveSkillId(templates, itemId, out var skill) && skill == definition.SkillId;

    public static bool IsReviewedItem(uint itemId) => ByItemId.ContainsKey(itemId);

    public static ExperienceBoostPotionGrant ResolveGrant(int activeBonusBasisPoints, long activeOnlineTicks,
        in StatusExperienceBoostDefinition incoming) =>
        ExperienceBoostTierRule.Resolve(activeBonusBasisPoints, activeOnlineTicks, incoming.StatusId,
            incoming.SkillId, incoming.BonusBasisPoints, incoming.OnlineTicks, MaximumOnlineTicks);
}
