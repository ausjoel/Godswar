using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Progression;

internal enum WeekendExperienceClaimStatus
{
    CharacterNotFound,
    Granted,
    AlreadyClaimed
}

internal interface IWeekendExperienceClaimStore
{
    Task<WeekendExperienceClaimStatus> ClaimWeekendExperienceAsync(
        int accountId,
        int characterId,
        RealmId realmId,
        DateOnly claimDay,
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken = default);
}

internal static class WeekendExperienceClaimRules
{
    public const int BonusBasisPoints = 20_000;
    public static readonly TimeSpan Duration = TimeSpan.FromHours(8);

    public static bool IsWeekend(DateOnly day) =>
        day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
