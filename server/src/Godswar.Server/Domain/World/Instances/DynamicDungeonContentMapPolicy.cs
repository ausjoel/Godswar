namespace Godswar.Server.Domain.World.Instances;

/// <summary>
/// Identifies legacy content maps that may only be hosted by an exact,
/// authoritative dungeon instance. These maps must never fall back to a
/// map-only/default open-world runtime.
/// </summary>
internal static class DynamicDungeonContentMapPolicy
{
    public const byte MedusaIslandMapId = 200;
    public const byte MedusaIslandAlternateMapId = 204;
    public const byte AtlantisPortalMapId = 205;
    public const byte WonderlandMapId = 207;
    public const byte HarborAttackFirstMapId = 208;
    public const byte HarborAttackSecondMapId = 209;

    public static bool IsDynamicDungeonMap(int mapId) =>
        mapId is
            MedusaIslandMapId or
            MedusaIslandAlternateMapId or
            AtlantisPortalMapId or
            WonderlandMapId or
            HarborAttackFirstMapId or
            HarborAttackSecondMapId;

    public static bool IsHarborAttackMap(int mapId) =>
        mapId is HarborAttackFirstMapId or HarborAttackSecondMapId;

    public static bool IsMedusaMap(int mapId) =>
        mapId is MedusaIslandMapId or MedusaIslandAlternateMapId;
}
