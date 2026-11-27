namespace CoppAddresd.Application.Features.Wellness365;

public sealed record ProtocolRewardDay(DateOnly LocalDate, IReadOnlyDictionary<string, int> Steps, int Bonus, bool Closed);
public sealed record ProtocolRewardReceipt(string Step, int Amount, bool Created);
public sealed record LegacyRewardEntry(Guid Id, int Amount, int BalanceAfter, DateTime AwardedAt, string Reason);
public sealed record ProtocolRewardsSnapshot(ProtocolRewardDay Today, int Balance, int LegacyBalance,
    int Level, string LevelName, int NextLevelAt, int Streak, int LongestStreak,
    IReadOnlyList<ProtocolRewardDay> History)
{
    public ProtocolRewardReceipt? Receipt { get; init; }
}

public interface IProtocolRewards
{
    Task<ProtocolRewardsSnapshot> ReadAsync(Guid patientId, CancellationToken ct);
    Task<IReadOnlyList<LegacyRewardEntry>> ReadLegacyHistoryAsync(Guid patientId, int page, CancellationToken ct);
    Task<ProtocolRewardsSnapshot> CompleteAsync(Guid patientId, DateOnly date, string step, CancellationToken ct);
    Task<ProtocolRewardsSnapshot> CloseAsync(Guid patientId, DateOnly date, CancellationToken ct);

    // Se llama en la misma transacción que persiste un cierre de Motor 365.
    Task CloseMotorDayAsync(Guid patientId, int planDay, DateOnly date, string timezone, CancellationToken ct);
}
