namespace Godswar.Server.Application.WorldInstances;

internal static class AtlantisPetSpawnPolicy
{
    public const uint MermaidObjectId = 42_245;
    public const uint SirenObjectId = 42_246;
    public const uint MermanEggItemId = 10_158;
    public const string MermaidTemplateKey = "B_normal_fish_famale_001";
    public const string SirenTemplateKey = "B_normal_fish_male_001";

    public static bool Matches(uint objectId, string templateKey) => objectId switch
    {
        MermaidObjectId => templateKey == MermaidTemplateKey,
        SirenObjectId => templateKey == SirenTemplateKey,
        _ => false
    };

    public static IReadOnlyList<(uint ObjectId, AtlantisMonsterSpawnIdentity Placement)> Spawns { get; } =
        Array.AsReadOnly(new (uint, AtlantisMonsterSpawnIdentity)[]
        {
            (MermaidObjectId, new(MermaidTemplateKey, AtlantisMonsterRank.Normal, -59f, 0f, 88f)),
            (SirenObjectId, new(SirenTemplateKey, AtlantisMonsterRank.Normal, -54f, 0f, -7f))
        });
}
