using System.Text.Json.Nodes;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record OperatorApplyBuffAction(
    string ActorName,
    SkillStatusEffectDefinition Definition) : OperatorSessionAction(ActorName);

internal sealed partial class GameSessionRegistry
{
    internal static string BuildOperatorBuffCatalogueJson()
    {
        var rows = new JsonArray();
        foreach (var definition in SkillStatusEffectCatalog.All.OrderBy(value => value.SkillId))
        {
            rows.Add(new JsonObject
            {
                ["skillId"] = definition.SkillId,
                ["statusId"] = definition.StatusId,
                ["kind"] = definition.Kind,
                ["priority"] = definition.Priority,
                ["beneficial"] = definition.Beneficial,
                ["durationSeconds"] = checked((long)definition.Duration.TotalSeconds),
                ["cooldownSeconds"] = checked((long)definition.Cooldown.TotalSeconds),
                ["effects"] = new JsonObject
                {
                    ["hit"] = definition.HitBonus,
                    ["criticalAppend"] = definition.CriticalAppendBonus,
                    ["physicalDamageReduction"] = definition.PhysicalDamageReduction,
                    ["magicDamageReduction"] = definition.MagicDamageReduction,
                    ["physicalDefense"] = definition.PhysicalDefenseBonus,
                    ["magicDefense"] = definition.MagicDefenseBonus,
                    ["dodge"] = definition.DodgeBonus,
                    ["criticalResistance"] = definition.CriticalResistanceBonus
                }
            });
        }
        return new JsonObject
        {
            ["version"] = 1,
            ["persistence"] = "session",
            ["stacking"] = "One status per kind; a higher-priority active status blocks a weaker one.",
            ["maxDurationSeconds"] = uint.MaxValue,
            ["buffs"] = rows
        }.ToJsonString();
    }

    internal static bool TryReadOperatorBuff(
        JsonObject body,
        out SkillStatusEffectDefinition definition,
        out string error)
    {
        definition = default;
        error = "skillId must identify an authored server buff";
        if (Int(body, "skillId") is not { } id ||
            !SkillStatusEffectCatalog.TryGet(id, out definition) || !definition.Beneficial)
        {
            return false;
        }
        if (body.ContainsKey("durationSeconds"))
        {
            if (Long(body, "durationSeconds") is not { } seconds || seconds < 1 || seconds > uint.MaxValue)
            {
                error = "durationSeconds must be a whole number from1 to4294967295 (native uint32 seconds)";
                return false;
            }
            definition = definition with { Duration = TimeSpan.FromSeconds(seconds) };
        }
        error = string.Empty;
        return true;
    }
}
