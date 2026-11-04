using System.Data;
using Godswar.Server.Application.Mentor;
using Npgsql;

namespace Godswar.Server.Infrastructure.Mentor;

/// <summary>
/// ECONOMY mentor: Master and Apprentice relations, the 24-hour lockouts, graduations and the rewards they owe.
/// </summary>
/// <remarks>
/// Like server_rates, these tables are NOT registered schema migrations: idempotent DDL runs on first use, so a binary
/// without this feature still starts against a database this build has used. Every write that touches two characters
/// takes one transaction-scoped advisory lock, so two invitations can never race past the six-apprentice limit.
/// Silver and B-Gold are credited in the same transaction that marks a reward paid; EXP and Talent follow through the
/// game store's progression path (the caller), which owns level-ups.
/// </remarks>
internal sealed class PostgresMentorStore(NpgsqlDataSource dataSource) : IMentorStore
{
    internal const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.mentor_relations (
            apprentice_id integer PRIMARY KEY REFERENCES public.character_base(id) ON DELETE CASCADE,
            master_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            started_at timestamptz NOT NULL DEFAULT now(),
            CHECK (apprentice_id <> master_id)
        );
        CREATE INDEX IF NOT EXISTS mentor_relations_master_idx ON public.mentor_relations (master_id);
        CREATE TABLE IF NOT EXISTS public.mentor_lockouts (
            character_id integer PRIMARY KEY REFERENCES public.character_base(id) ON DELETE CASCADE,
            locked_until timestamptz NOT NULL
        );
        CREATE TABLE IF NOT EXISTS public.mentor_graduations (
            apprentice_id integer PRIMARY KEY REFERENCES public.character_base(id) ON DELETE CASCADE,
            master_id integer NOT NULL,
            graduated_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS public.mentor_rewards (
            id bigserial PRIMARY KEY,
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            role text NOT NULL CHECK (role IN ('apprentice', 'master')),
            partner_id integer NOT NULL,
            partner_name text NOT NULL,
            experience bigint NOT NULL CHECK (experience >= 0),
            talent_points integer NOT NULL CHECK (talent_points >= 0),
            silver integer NOT NULL CHECK (silver >= 0),
            binding_gold integer NOT NULL CHECK (binding_gold >= 0),
            created_at timestamptz NOT NULL DEFAULT now(),
            paid_at timestamptz NULL,
            UNIQUE (character_id, role, partner_id)
        );
        COMMENT ON TABLE public.mentor_relations IS 'ECONOMY mentor: active Master and Apprentice pairs (IGG guide igg-acid=111).';
        COMMENT ON TABLE public.mentor_rewards IS 'ECONOMY mentor: graduation rewards; paid_at NULL = owed, paid at the character''s next packet.';
        """;

    private const long AdvisoryLockKey = 0x4D454E544F52; // "MENTOR"
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private int _ensured;

    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        if (Volatile.Read(ref _ensured) == 0)
        {
            // Two sessions logging in together must not run the DDL at the same time (concurrent CREATE ... IF NOT
            // EXISTS can deadlock): one at a time in this process, and an advisory lock across processes.
            await SchemaGate.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref _ensured) == 0)
                {
                    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                    await LockAsync(connection, transaction, cancellationToken);
                    await using (var ddl = new NpgsqlCommand(SchemaSql, connection, transaction))
                        await ddl.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    Volatile.Write(ref _ensured, 1);
                }
            }
            finally
            {
                SchemaGate.Release();
            }
        }

        return connection;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", connection, transaction);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<MentorView> ReadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        int? masterId = null;
        await using (var command = new NpgsqlCommand(
            "SELECT master_id FROM public.mentor_relations WHERE apprentice_id = @c;", connection))
        {
            command.Parameters.AddWithValue("c", characterId);
            if (await command.ExecuteScalarAsync(cancellationToken) is int id) masterId = id;
        }

        var teacher = masterId ?? characterId;
        var rows = new List<MentorMember>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT c.id, c.name, c.fighter_job_lv, c."Map", false AS is_master
            FROM public.mentor_relations r JOIN public.character_base c ON c.id = r.apprentice_id
            WHERE r.master_id = @m AND c.lifecycle_state = 'active'
            UNION ALL
            SELECT c.id, c.name, c.fighter_job_lv, c."Map", true
            FROM public.character_base c WHERE c.id = @m
            ORDER BY 5 DESC, 1;
            """, connection))
        {
            command.Parameters.AddWithValue("m", teacher);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2),
                    Convert.ToInt32(reader.GetValue(3)), reader.GetBoolean(4)));
        }

        var master = rows.FirstOrDefault(row => row.IsMaster);
        var apprentices = rows.Where(row => !row.IsMaster).ToArray();
        DateTimeOffset? lockedUntil = null;
        await using (var command = new NpgsqlCommand(
            "SELECT locked_until FROM public.mentor_lockouts WHERE character_id = @c AND locked_until > now();", connection))
        {
            command.Parameters.AddWithValue("c", characterId);
            if (await command.ExecuteScalarAsync(cancellationToken) is DateTime until)
                lockedUntil = new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc));
        }

        return new MentorView(characterId, masterId, apprentices.Length == 0 && masterId is null ? null : master,
            apprentices, lockedUntil);
    }

    public async Task<MentorCreateStatus> CreateAsync(int masterId, int apprenticeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        async Task<long> Count(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("m", masterId);
            command.Parameters.AddWithValue("a", apprenticeId);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        if (await Count("SELECT count(*) FROM public.mentor_relations WHERE apprentice_id = @a OR apprentice_id = @m;") > 0)
            return MentorCreateStatus.AlreadyHasMaster;
        if (await Count("SELECT count(*) FROM public.mentor_relations WHERE master_id = @a;") > 0)
            return MentorCreateStatus.Ineligible;
        if (await Count("SELECT count(*) FROM public.mentor_graduations WHERE apprentice_id = @a;") > 0)
            return MentorCreateStatus.Ineligible;
        if (await Count("SELECT count(*) FROM public.mentor_relations WHERE master_id = @m;") >= MentorPolicy.MaximumApprentices)
            return MentorCreateStatus.MasterFull;
        if (await Count("SELECT count(*) FROM public.mentor_lockouts WHERE character_id IN (@m, @a) AND locked_until > now();") > 0)
            return MentorCreateStatus.Locked;
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO public.mentor_relations (apprentice_id, master_id) VALUES (@a, @m);", connection, transaction))
        {
            insert.Parameters.AddWithValue("m", masterId);
            insert.Parameters.AddWithValue("a", apprenticeId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return MentorCreateStatus.Created;
    }

    public async Task<int?> EndAsync(int apprenticeId, int? requiredMasterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        int? masterId = null;
        await using (var delete = new NpgsqlCommand(
            """
            DELETE FROM public.mentor_relations
            WHERE apprentice_id = @a AND (@m::integer IS NULL OR master_id = @m)
            RETURNING master_id;
            """, connection, transaction))
        {
            delete.Parameters.AddWithValue("a", apprenticeId);
            delete.Parameters.Add(new NpgsqlParameter("m", NpgsqlTypes.NpgsqlDbType.Integer)
                { Value = (object?)requiredMasterId ?? DBNull.Value });
            if (await delete.ExecuteScalarAsync(cancellationToken) is int id) masterId = id;
        }

        if (masterId is { } master)
        {
            // Guide: neither side may start a new Master and Apprentice relationship for 24 hours.
            await using var lockout = new NpgsqlCommand(
                """
                INSERT INTO public.mentor_lockouts (character_id, locked_until)
                VALUES (@a, now() + @span), (@m, now() + @span)
                ON CONFLICT (character_id) DO UPDATE SET locked_until = EXCLUDED.locked_until;
                """, connection, transaction);
            lockout.Parameters.AddWithValue("a", apprenticeId);
            lockout.Parameters.AddWithValue("m", master);
            lockout.Parameters.AddWithValue("span", MentorPolicy.Lockout);
            await lockout.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return masterId;
    }

    public async Task<int?> GraduateAsync(int apprenticeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        int masterId;
        string apprenticeName, masterName;
        await using (var read = new NpgsqlCommand(
            """
            SELECT r.master_id, a.name, m.name
            FROM public.mentor_relations r
            JOIN public.character_base a ON a.id = r.apprentice_id
            JOIN public.character_base m ON m.id = r.master_id
            WHERE r.apprentice_id = @a AND a.fighter_job_lv >= @level;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("a", apprenticeId);
            read.Parameters.AddWithValue("level", MentorPolicy.GraduationLevel);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            masterId = reader.GetInt32(0);
            apprenticeName = reader.GetString(1);
            masterName = reader.GetString(2);
        }

        await using (var graduate = new NpgsqlCommand(
            """
            INSERT INTO public.mentor_graduations (apprentice_id, master_id) VALUES (@a, @m) ON CONFLICT DO NOTHING;
            DELETE FROM public.mentor_relations WHERE apprentice_id = @a;
            INSERT INTO public.mentor_rewards
                (character_id, role, partner_id, partner_name, experience, talent_points, silver, binding_gold)
            VALUES (@a, 'apprentice', @m, @mn, @ae, @at, @as, @ab),
                   (@m, 'master', @a, @an, @me, @mt, @ms, 0)
            ON CONFLICT (character_id, role, partner_id) DO NOTHING;
            """, connection, transaction))
        {
            graduate.Parameters.AddWithValue("a", apprenticeId);
            graduate.Parameters.AddWithValue("m", masterId);
            graduate.Parameters.AddWithValue("an", apprenticeName);
            graduate.Parameters.AddWithValue("mn", masterName);
            graduate.Parameters.AddWithValue("ae", MentorPolicy.ApprenticeReward.Experience);
            graduate.Parameters.AddWithValue("at", MentorPolicy.ApprenticeReward.TalentPoints);
            graduate.Parameters.AddWithValue("as", MentorPolicy.ApprenticeReward.Silver);
            graduate.Parameters.AddWithValue("ab", MentorPolicy.ApprenticeReward.BindingGold);
            graduate.Parameters.AddWithValue("me", MentorPolicy.MasterReward.Experience);
            graduate.Parameters.AddWithValue("mt", MentorPolicy.MasterReward.TalentPoints);
            graduate.Parameters.AddWithValue("ms", MentorPolicy.MasterReward.Silver);
            await graduate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return masterId;
    }

    public async Task<MentorClaim?> ClaimRewardsAsync(int characterId, int accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var rewards = new List<MentorReward>();
        await using (var read = new NpgsqlCommand(
            """
            SELECT id, role, partner_name, experience, talent_points, silver, binding_gold
            FROM public.mentor_rewards WHERE character_id = @c AND paid_at IS NULL ORDER BY id FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("c", characterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rewards.Add(new(reader.GetInt64(0), reader.GetString(1) == "master", reader.GetString(2), reader.GetInt64(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        }

        if (rewards.Count == 0) return null;
        int silverAfter, bindingGoldAfter, talentPointsAfter;
        await using (var credit = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET "Money" = LEAST("Money"::bigint + @silver, 2147483647)::integer,
                "BindingGold" = LEAST("BindingGold"::bigint + @bgold, 2147483647)::integer,
                "SkillPoint" = LEAST("SkillPoint"::bigint + @tp, 2147483647)::integer,
                wallet_revision = wallet_revision + 1,
                progression_reward_revision = progression_reward_revision + 1
            WHERE id = @c AND account_id = @account AND lifecycle_state = 'active'
            RETURNING "Money", "BindingGold", "SkillPoint";
            """, connection, transaction))
        {
            credit.Parameters.AddWithValue("silver", rewards.Sum(reward => (long)reward.Silver));
            credit.Parameters.AddWithValue("bgold", rewards.Sum(reward => (long)reward.BindingGold));
            // Talent Points are whole points (the guide and the client's "Obtain Skill Points" line), credited like a
            // Talent Point item (ProgressItemKind.TalentPoints), not as talent experience.
            credit.Parameters.AddWithValue("tp", rewards.Sum(reward => (long)reward.TalentPoints));
            credit.Parameters.AddWithValue("c", characterId);
            credit.Parameters.AddWithValue("account", accountId);
            await using var reader = await credit.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            silverAfter = reader.GetInt32(0);
            bindingGoldAfter = reader.GetInt32(1);
            talentPointsAfter = reader.GetInt32(2);
        }

        await using (var paid = new NpgsqlCommand(
            "UPDATE public.mentor_rewards SET paid_at = now() WHERE id = ANY(@ids);", connection, transaction))
        {
            paid.Parameters.AddWithValue("ids", rewards.Select(reward => reward.Id).ToArray());
            await paid.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MentorClaim(rewards, silverAfter, bindingGoldAfter, talentPointsAfter);
    }
}
