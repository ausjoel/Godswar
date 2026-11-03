namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// The capturable dogs on the Lelantine Farm. The templates are the map's own
/// (<c>Monster.ini</c> <c>A_normal{A,B,C,D}_{male,famale}_001</c>, published with is_pet = true,
/// named Simple / Confused / Clever / Wise Dog). GENERATED mapping from kind to egg aptitude, since no
/// reference capture of a farm capture exists: Simple -> 1, Confused and Clever -> 5, Wise -> 8,
/// the only aptitudes the farm donation scores (<see cref="LelantineFarmPointsPolicy"/>).
/// </summary>
internal static class LelantineFarmDogPolicy
{
    public const uint EggItemId = 10159;

    public static IEnumerable<string> TemplateKeys { get; } =
        new[] { "A", "B", "C", "D" }.SelectMany(kind => new[] { "male", "famale" }, (kind, sex) => $"A_normal{kind}_{sex}_001").ToArray();

    /// <summary>
    /// GENERATED capture chance per tool (the admin: wooden about 90%). The item texts only rank the tools
    /// (slightly / fair / quite a bit / substantially higher, Tuck Net nearly 100%), so the other
    /// figures are an even ladder above the wooden one. A miss uses the tool up and the dog runs off.
    /// </summary>
    public static double CaptureChance(int netItemId) => netItemId switch
    {
        10080 => 0.90,
        10081 => 0.93,
        10082 => 0.96,
        10083 => 0.98,
        _ => 1.0
    };

    public static bool TryGetTier(string templateKey, out MedusaEncounterDifficulty tier)
    {
        tier = default;
        const string prefix = "A_normal";
        if (!templateKey.StartsWith(prefix, StringComparison.Ordinal) || templateKey.Length <= prefix.Length ||
            !(templateKey.EndsWith("_male_001", StringComparison.Ordinal) ||
              templateKey.EndsWith("_famale_001", StringComparison.Ordinal)))
        {
            return false;
        }
        switch (templateKey[prefix.Length])
        {
            case 'A': tier = MedusaEncounterDifficulty.Normal; return true;
            case 'B':
            case 'C': tier = MedusaEncounterDifficulty.Enhanced; return true;
            case 'D': tier = MedusaEncounterDifficulty.Mythic; return true;
            default: return false;
        }
    }
}
