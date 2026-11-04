using Godswar.Server.Application.Rates; // ROUND2
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

internal enum LegacyInstanceScheduleStatus : byte
{
    Open = 1,
    WrongDay = 2,
    DailyCutoffPassed = 3
}

internal static class LegacyInstanceAdmissionPolicy
{
    private static readonly TimeOnly WonderlandCutoff =
        new(23, 0);

    public static LegacyInstanceScheduleStatus CheckSchedule(
        InstanceCallerEntryKind kind,
        RealmCalendar calendar,
        DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (kind != InstanceCallerEntryKind.Wonderland)
        {
            return LegacyInstanceScheduleStatus.Open;
        }

        var local = calendar.ToRealmTime(instant);

        // The reference only opened 飘渺幻境 on Saturday and Sunday. This server
        // runs it every day by the operator's decision (2026-09-28), so only the
        // daily cut-off remains - the part the client's own text states: the
        // instance is free to enter before 23:00 server time (Asia/Manila).
        // ROUND2: event.wonderland_close_hour (stock 23; 24 = no cut-off).
        var closeHour = ServerRates.Current.Absolute(
            ServerRateKeys.WonderlandCloseHour);
        if (closeHour >= 24)
        {
            return LegacyInstanceScheduleStatus.Open;
        }

        var cutoff = closeHour == WonderlandCutoff.Hour
            ? WonderlandCutoff
            : new TimeOnly((int)closeHour, 0);
        return TimeOnly.FromDateTime(local.DateTime) < cutoff
            ? LegacyInstanceScheduleStatus.Open
            : LegacyInstanceScheduleStatus.DailyCutoffPassed;
    }
}
