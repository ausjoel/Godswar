using System.Collections.Frozen;
using Godswar.Server.Application.Events;

namespace Godswar.Server.Application.Npc;

// Editor-written NPC shop lists and teleporter destinations. Empty = the compiled content, so a missing or unreadable
// table leaves the stock behaviour in place.
internal sealed record NpcShopOverrideRow(
    uint ItemId,
    int Price,
    byte Category,
    byte Quality,
    byte Grade,
    byte Bound,
    byte StackMarker);

internal sealed record NpcTeleportOverride(
    string NpcKey,
    int SubId,
    short TargetMapId,
    float X,
    float Z,
    int MinimumLevel);

internal readonly record struct ItemActivator(int SkillId, int CooldownSeconds);

internal sealed class NpcContentSnapshot
{
    public static NpcContentSnapshot Empty { get; } = new(
        0,
        new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>>(),
        [],
        []);

    public NpcContentSnapshot(
        long revision,
        IReadOnlyDictionary<string, IReadOnlyList<NpcShopOverrideRow>> shops,
        IEnumerable<NpcTeleportOverride> teleports,
        IEnumerable<int>? editorAdminAccounts = null,
        IReadOnlyDictionary<uint, ItemActivator>? activators = null,
        EventContent? events = null)
    {
        Events = events ?? EventContent.Empty;
        Activators = (activators ?? new Dictionary<uint, ItemActivator>()).ToFrozenDictionary();
        EditorAdminAccounts = (editorAdminAccounts ?? []).ToFrozenSet();
        Revision = revision;
        Shops = shops.ToFrozenDictionary(StringComparer.Ordinal);
        Teleports = teleports.ToFrozenDictionary(static t => (t.NpcKey, t.SubId));
    }

    public long Revision { get; }

    public EventContent Events { get; }

    public FrozenSet<int> EditorAdminAccounts { get; }

    public FrozenDictionary<uint, ItemActivator> Activators { get; }

    public FrozenDictionary<string, IReadOnlyList<NpcShopOverrideRow>> Shops { get; }

    public FrozenDictionary<(string NpcKey, int SubId), NpcTeleportOverride> Teleports { get; }
}

internal static class NpcContentOverrides
{
    private static NpcContentSnapshot _current = NpcContentSnapshot.Empty;

    public static NpcContentSnapshot Current => Volatile.Read(ref _current);

    public static void Install(NpcContentSnapshot snapshot) =>
        Volatile.Write(ref _current, snapshot);

    public static Func<CancellationToken, Task<string>>? DescribeHook { get; set; }

    public static Func<CancellationToken, Task<string>>? ReloadHook { get; set; }

    public static Func<string, CancellationToken, Task<string>>? DescribeShopHook { get; set; }

    public static Func<CancellationToken, Task<string>>? DescribeEventsHook { get; set; }
}
