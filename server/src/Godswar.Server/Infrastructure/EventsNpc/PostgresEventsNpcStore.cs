using Npgsql;

namespace Godswar.Server.Infrastructure.EventsNpc;

// EVENTSNPC: durable state for the Lost Book, Marathon and Random Quest Manager events. Like server_rates and
// NpcContentRuntime this is NOT a registered schema migration: the tables are created idempotently on first
// use (and by Development/build/eventsnpc/migration.sql), so an older binary still starts against a database
// this build has used. No foreign key to character_base (a REFERENCES lock on character_base stalls
// character writes on a live server).
internal static class EventsNpcRuntime
{
    public static PostgresEventsNpcStore? Store { get; private set; }

    public static void Initialize(string connectionString)
    {
        try
        {
            Store = new PostgresEventsNpcStore(NpgsqlDataSource.Create(connectionString));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[eventsnpc] start-up failed; the events stay menu-only: {error.Message}");
        }
    }
}

/// <summary>One character's event counters. Day counters belong to <see cref="Day"/> only.</summary>
internal sealed record EventsNpcState(
    DateOnly Day,
    int ElderScrolls,
    int IncompleteWishes,
    int CompleteWishes,
    int Races,
    DateTimeOffset? RaceStartedAt,
    int ChainStep,
    long ChainQuestId,
    bool SackPending = false,
    int RewardStep = 0)
{
    // SackPending: one Quest Sack was handed out for ChainStep and has not been opened yet. Only that one open
    // pays; any other sack (a stashed or re-issued copy) opens to nothing. RewardStep: a chain hand-in whose
    // payout failed (bag full) owes the reward for that step; the manager pays it on the next claim.
    public static EventsNpcState Fresh(DateOnly day) => new(day, 0, 0, 0, 0, null, 0, 0);
}

internal sealed class PostgresEventsNpcStore
{
    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.eventsnpc_character_state (
            character_id integer PRIMARY KEY,
            day date NOT NULL,
            elder_scrolls smallint NOT NULL DEFAULT 0,
            incomplete_wishes smallint NOT NULL DEFAULT 0,
            complete_wishes smallint NOT NULL DEFAULT 0,
            races smallint NOT NULL DEFAULT 0,
            race_started_at timestamptz NULL,
            chain_step smallint NOT NULL DEFAULT 0,
            chain_quest_id bigint NOT NULL DEFAULT 0,
            updated_at timestamptz NOT NULL DEFAULT now()
        );

        -- Review fix (2026-11-03): one-shot Quest Sack and the owed chain reward.
        ALTER TABLE public.eventsnpc_character_state
            ADD COLUMN IF NOT EXISTS sack_pending boolean NOT NULL DEFAULT false,
            ADD COLUMN IF NOT EXISTS reward_step smallint NOT NULL DEFAULT 0;

        CREATE TABLE IF NOT EXISTS public.eventsnpc_marathon_finishes (
            id bigserial PRIMARY KEY,
            character_id integer NOT NULL,
            finished_at timestamptz NOT NULL,
            milliseconds bigint NOT NULL CHECK (milliseconds >= 0),
            binding_gold integer NOT NULL
        );

        CREATE INDEX IF NOT EXISTS eventsnpc_marathon_finishes_time
            ON public.eventsnpc_marathon_finishes (finished_at);
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _ensured;

    public PostgresEventsNpcStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Runs <paramref name="body"/> in one transaction with the character row locked, so two clicks cannot
    /// spend the same scroll or the same daily allowance. The body commits by returning; an exception rolls
    /// everything back.
    /// </summary>
    public async Task<T> RunAsync<T>(int characterId, DateOnly day, Func<EventsNpcTransaction, Task<T>> body,
        CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockRow = new NpgsqlCommand(
            "SELECT true FROM public.character_base WHERE id = @c FOR UPDATE;", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("c", characterId);
            if (await lockRow.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new InvalidOperationException($"Character {characterId} does not exist.");
            }
        }

        var tx = new EventsNpcTransaction(connection, transaction, characterId, day, cancellationToken);
        await tx.LoadStateAsync();
        var result = await body(tx);
        if (tx.RolledBack)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await tx.SaveStateAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>The fastest race finished since <paramref name="since"/>, in milliseconds.</summary>
    public async Task<long?> BestRaceSinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT min(milliseconds) FROM public.eventsnpc_marathon_finishes WHERE finished_at >= @s;", connection);
        command.Parameters.AddWithValue("s", since);
        return await command.ExecuteScalarAsync(cancellationToken) is long value ? value : null;
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_ensured)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (!_ensured)
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                await using var ddl = new NpgsqlCommand(SchemaSql, connection);
                await ddl.ExecuteNonQueryAsync(cancellationToken);
                _ensured = true;
            }
        }
        finally
        {
            _schemaGate.Release();
        }
    }
}
