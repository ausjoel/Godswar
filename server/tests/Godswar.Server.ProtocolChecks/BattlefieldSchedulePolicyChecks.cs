using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static class BattlefieldSchedulePolicyChecks
{
    public const string CheckName =
        "Realm-calendar Battlefield admission windows";

    private static readonly RealmCalendar Manila =
        RealmCalendar.CreateForTesting(RealmId.Tempest, "Asia/Manila");

    public static Task RunAsync()
    {
        CheckPindusWindows();
        CheckNiMiniWindows();
        CheckDuelAndUnknownPolicies();
        return Task.CompletedTask;
    }

    private static void CheckPindusWindows()
    {
        DateTimeOffset[] starts =
        [
            ManilaTime(2026, 9, 2, 21, 0),
            ManilaTime(2026, 9, 4, 21, 0),
            ManilaTime(2026, 9, 5, 8, 0),
            ManilaTime(2026, 9, 5, 15, 0),
            ManilaTime(2026, 9, 6, 8, 0),
            ManilaTime(2026, 9, 6, 21, 0)
        ];
        CheckWindows(BattlefieldDestinationKind.Pindus, starts);
        Check.True(
            !IsOpen(
                BattlefieldDestinationKind.Pindus,
                ManilaTime(2026, 9, 1, 21, 0)) &&
            !IsOpen(
                BattlefieldDestinationKind.Pindus,
                ManilaTime(2026, 9, 3, 21, 0)) &&
            !IsOpen(
                BattlefieldDestinationKind.Pindus,
                ManilaTime(2026, 9, 5, 9, 0)) &&
            !IsOpen(
                BattlefieldDestinationKind.Pindus,
                ManilaTime(2026, 9, 6, 9, 0)),
            "Pindus stays closed outside its advertised realm windows");
    }

    private static void CheckNiMiniWindows()
    {
        DateTimeOffset[] starts =
        [
            ManilaTime(2026, 9, 4, 19, 0),
            ManilaTime(2026, 9, 5, 7, 0),
            ManilaTime(2026, 9, 6, 19, 0)
        ];
        CheckWindows(BattlefieldDestinationKind.NiMiniLower, starts);
        CheckWindows(BattlefieldDestinationKind.NiMiniUpper, starts);
        Check.True(
            !IsOpen(
                BattlefieldDestinationKind.NiMiniUpper,
                ManilaTime(2026, 9, 2, 19, 0)) &&
            !IsOpen(
                BattlefieldDestinationKind.NiMiniUpper,
                ManilaTime(2026, 9, 5, 8, 0)) &&
            !IsOpen(
                BattlefieldDestinationKind.NiMiniUpper,
                ManilaTime(2026, 9, 6, 20, 0)),
            "Ni Mini stays closed outside Friday, Saturday, and Sunday windows");
    }

    private static void CheckDuelAndUnknownPolicies()
    {
        Check.True(
            IsOpen(
                BattlefieldDestinationKind.DuelArena,
                ManilaTime(2026, 9, 1, 0, 0)) &&
            IsOpen(
                BattlefieldDestinationKind.DuelArena,
                ManilaTime(2026, 9, 6, 23, 59)) &&
            !IsOpen(
                (BattlefieldDestinationKind)byte.MaxValue,
                ManilaTime(2026, 9, 4, 19, 0)),
            "Duel is always open while unknown destinations fail closed");
        Check.Throws<ArgumentNullException>(
            () => BattlefieldSchedulePolicy.IsOpen(
                BattlefieldDestinationKind.Pindus,
                null!,
                ManilaTime(2026, 9, 2, 21, 0)),
            "schedule evaluation requires a realm calendar");
    }

    private static void CheckWindows(
        BattlefieldDestinationKind destination,
        IEnumerable<DateTimeOffset> starts)
    {
        foreach (var start in starts)
        {
            var end = start.AddMinutes(45);
            Check.True(
                !IsOpen(destination, start.AddTicks(-1)) &&
                IsOpen(destination, start) &&
                IsOpen(destination, end.AddTicks(-1)) &&
                !IsOpen(destination, end),
                $"{destination} uses a start-inclusive 45-minute realm window at {start:O}");
        }
    }

    private static bool IsOpen(
        BattlefieldDestinationKind destination,
        DateTimeOffset instant) =>
        BattlefieldSchedulePolicy.IsOpen(destination, Manila, instant);

    private static DateTimeOffset ManilaTime(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(
            year,
            month,
            day,
            hour,
            minute,
            second: 0,
            TimeSpan.FromHours(8));
}
