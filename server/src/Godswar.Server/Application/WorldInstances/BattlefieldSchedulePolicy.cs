using Godswar.Server.Application.Events;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates; // ROUND2
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Realm-calendar admission windows advertised by the current event panel.
/// A window is start-inclusive and end-exclusive.
/// </summary>
internal static class BattlefieldSchedulePolicy
{
    private static readonly TimeSpan EventDuration =
        TimeSpan.FromMinutes(45);

    public static bool IsOpen(
        BattlefieldDestinationKind destination,
        RealmCalendar calendar,
        DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (destination is BattlefieldDestinationKind.DuelArena or
            BattlefieldDestinationKind.LelantineFarm)
        {
            // The Duel Arena is always available, and the Lelantine Farm is
            // deliberately held open too: its published window is a single
            // Friday hour, which makes the activity untestable on any other
            // day. Every other destination keeps its published window.
            return true;
        }

        // ROUND2: always-open switches, window length and a start-time shift
        // from server_rates (stock: windows below, 45 minutes, no shift).
        var rates = ServerRates.Current;
        if (destination == BattlefieldDestinationKind.Pindus &&
                rates.Absolute(ServerRateKeys.PindusAlwaysOpen) == 1 ||
            destination is BattlefieldDestinationKind.NiMiniLower or
                BattlefieldDestinationKind.NiMiniUpper &&
                rates.Absolute(ServerRateKeys.NiMiniAlwaysOpen) == 1)
        {
            return true;
        }

        // Editor-written windows replace the compiled ones and carry their own length; the rates' length and shift do not apply.
        var activity = destination == BattlefieldDestinationKind.Pindus ? EventContent.Pindus : EventContent.NiMini;
        if (NpcContentOverrides.Current.Events.Windows.TryGetValue(activity, out var edited))
        {
            return EventSchedulePolicy.IsOpen(edited, calendar.ToRealmTime(instant).DateTime);
        }

        var duration = TimeSpan.FromMinutes(
            rates.Absolute(ServerRateKeys.BattlefieldWindowMinutes));
        var shift = TimeSpan.FromHours(
            rates.Absolute(ServerRateKeys.BattlefieldShiftHours));
        if (duration == EventDuration && shift == TimeSpan.Zero)
        {
            var local = calendar.ToRealmTime(instant);
            var time = TimeOnly.FromDateTime(local.DateTime);
            return Starts(destination, local.DayOfWeek)
                .Any(start => IsWithin(time, start, EventDuration));
        }

        // A shifted or longer window may cross midnight: test the windows of
        // the previous, current and next day as absolute instants.
        var realmNow = calendar.ToRealmTime(instant).DateTime;
        for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            var day = realmNow.Date.AddDays(dayOffset);
            foreach (var start in Starts(destination, day.DayOfWeek))
            {
                var opens = day + start.ToTimeSpan() + shift;
                if (realmNow >= opens && realmNow < opens + duration)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static IEnumerable<object> DescribeCompiled(BattlefieldDestinationKind destination) =>
        Enum.GetValues<DayOfWeek>().SelectMany(day => Starts(destination, day).Select(start => (object)new
        {
            day = (int)day,
            start_minute = start.Hour * 60 + start.Minute,
            duration_minutes = (int)EventDuration.TotalMinutes
        }));

    private static IEnumerable<TimeOnly> Starts(
        BattlefieldDestinationKind destination,
        DayOfWeek day) =>
        destination switch
        {
            BattlefieldDestinationKind.Pindus => day switch
            {
                DayOfWeek.Wednesday => [new TimeOnly(21, 0)],
                DayOfWeek.Friday => [new TimeOnly(21, 0)],
                DayOfWeek.Saturday =>
                    [new TimeOnly(8, 0), new TimeOnly(15, 0)],
                DayOfWeek.Sunday =>
                    [new TimeOnly(8, 0), new TimeOnly(21, 0)],
                _ => []
            },
            BattlefieldDestinationKind.NiMiniLower or
            BattlefieldDestinationKind.NiMiniUpper => day switch
            {
                DayOfWeek.Friday => [new TimeOnly(19, 0)],
                DayOfWeek.Saturday => [new TimeOnly(7, 0)],
                DayOfWeek.Sunday => [new TimeOnly(19, 0)],
                _ => []
            },
            _ => []
        };

    private static bool IsWithin(
        TimeOnly current,
        TimeOnly start,
        TimeSpan duration)
    {
        var elapsed = current.ToTimeSpan() - start.ToTimeSpan();
        return elapsed >= TimeSpan.Zero && elapsed < duration;
    }
}
