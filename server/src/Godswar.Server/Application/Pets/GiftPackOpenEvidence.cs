using System.Globalization;

namespace Godswar.Server.Application.Pets;

// Contents is "item:quantity:bound;..." so the record keeps value equality through storage and replay.
internal sealed record GiftPackOpenEvidence(long ItemInstanceId, uint PackTemplateId, int KitBagSlot, string Contents)
{
    public bool IsValid => ItemInstanceId > 0 && PackTemplateId > 0 && KitBagSlot is >= 0 and < 96 &&
        TryParse(Contents, out var rewards) && rewards.Count > 0;

    public IReadOnlyList<(uint ItemId, int Quantity, short Bound)> Rewards =>
        TryParse(Contents, out var rewards) ? rewards : throw new InvalidDataException("Invalid gift pack contents.");

    public static string Format(IEnumerable<(uint ItemId, int Quantity, short Bound)> rewards) =>
        string.Join(';', rewards.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.ItemId}:{r.Quantity}:{r.Bound}")));

    private static bool TryParse(string? contents, out List<(uint ItemId, int Quantity, short Bound)> rewards)
    {
        rewards = [];
        if (string.IsNullOrEmpty(contents)) return false;
        foreach (var part in contents.Split(';'))
        {
            var fields = part.Split(':');
            if (fields.Length != 3 || !uint.TryParse(fields[0], CultureInfo.InvariantCulture, out var item) || item == 0 ||
                !int.TryParse(fields[1], CultureInfo.InvariantCulture, out var quantity) || quantity <= 0 ||
                !short.TryParse(fields[2], CultureInfo.InvariantCulture, out var bound) || bound is < 0 or > 1) return false;
            rewards.Add((item, quantity, bound));
        }
        return true;
    }
}
