using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Events;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Rates;

// Like server_rates, these are NOT registered schema migrations: idempotent DDL at start-up, so a binary without this
// feature can still start against a database this build has used. A failure keeps the last good snapshot.
internal static class NpcContentRuntime
{
    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS public.npc_shop_overrides (
            service text NOT NULL,
            position integer NOT NULL,
            item_id integer NOT NULL CHECK (item_id > 0),
            price integer NOT NULL CHECK (price > 0),
            category smallint NOT NULL CHECK (category BETWEEN 0 AND 255),
            quality smallint NOT NULL DEFAULT 1 CHECK (quality BETWEEN 1 AND 20),
            grade smallint NOT NULL DEFAULT 1 CHECK (grade BETWEEN 1 AND 25),
            bound smallint NOT NULL DEFAULT 0 CHECK (bound BETWEEN 0 AND 1),
            stack_marker smallint NOT NULL DEFAULT 1 CHECK (stack_marker BETWEEN 1 AND 255),
            PRIMARY KEY (service, position)
        );

        CREATE TABLE IF NOT EXISTS public.npc_teleport_overrides (
            npc_key text NOT NULL,
            sub_id integer NOT NULL,
            target_map_id smallint NOT NULL CHECK (target_map_id BETWEEN 0 AND 255),
            pos_x real NOT NULL CHECK (pos_x >= -256 AND pos_x < 256),
            pos_z real NOT NULL CHECK (pos_z > -256 AND pos_z <= 256),
            minimum_level integer NOT NULL DEFAULT 1 CHECK (minimum_level BETWEEN 1 AND 255),
            PRIMARY KEY (npc_key, sub_id)
        );

        CREATE TABLE IF NOT EXISTS public.item_activators (
            item_id integer PRIMARY KEY CHECK (item_id > 0),
            skill_id integer NOT NULL CHECK (skill_id > 0),
            cooldown_seconds integer NOT NULL CHECK (cooldown_seconds >= 0)
        );

        CREATE TABLE IF NOT EXISTS public.event_schedule_overrides (
            activity text NOT NULL,
            day_of_week smallint NOT NULL CHECK (day_of_week BETWEEN -1 AND 6),
            start_minute smallint NOT NULL CHECK (start_minute BETWEEN 0 AND 1439),
            duration_minutes smallint NOT NULL CHECK (duration_minutes BETWEEN 1 AND 1440),
            PRIMARY KEY (activity, day_of_week, start_minute)
        );

        CREATE TABLE IF NOT EXISTS public.server_notices (
            id integer PRIMARY KEY CHECK (id > 0),
            message text NOT NULL CHECK (char_length(message) BETWEEN 1 AND 106),
            delivery text NOT NULL CHECK (delivery IN ('banner', 'log')),
            day_mask smallint NOT NULL CHECK (day_mask BETWEEN 1 AND 127),
            start_minute smallint NOT NULL CHECK (start_minute BETWEEN 0 AND 1439),
            end_minute smallint NOT NULL CHECK (end_minute BETWEEN 0 AND 1439),
            repeat_minutes smallint NOT NULL CHECK (repeat_minutes BETWEEN 0 AND 1439),
            first_day date,
            last_day date,
            enabled boolean NOT NULL DEFAULT true
        );

        CREATE TABLE IF NOT EXISTS public.notice_settings (
            name text PRIMARY KEY,
            value integer NOT NULL
        );

        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'gweditor') THEN
                GRANT SELECT, INSERT, UPDATE, DELETE ON public.npc_shop_overrides, public.npc_teleport_overrides, public.item_activators, public.event_schedule_overrides, public.server_notices, public.notice_settings TO gweditor;
            END IF;
        END $$;
        """;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static NpgsqlDataSource? _dataSource;
    private static bool _ensured;
    private static long _revision;
    private static string _loaded = string.Empty;

    public static RealmCalendar? Calendar { get; set; }

    public static async Task InitializeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        NpcContentOverrides.DescribeHook = DescribeAsync;
        NpcContentOverrides.ReloadHook = ReloadForManagementAsync;
        NpcContentOverrides.DescribeShopHook = DescribeShopAsync;
        NpcContentOverrides.DescribeEventsHook = DescribeEventsAsync;
        try
        {
            _dataSource ??= NpgsqlDataSource.Create(connectionString);
            await ReloadAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[npc-content] start-up load failed; using compiled content: {error.GetType().Name}: {error.Message}");
        }
    }

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken);
                await ReloadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Console.WriteLine($"[npc-content] reload failed; keeping revision {_revision}: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    private static async Task<string> ReloadForManagementAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken);
        return JsonSerializer.Serialize(new { revision = NpcContentOverrides.Current.Revision });
    }

    private static async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var dataSource = _dataSource ?? throw new InvalidOperationException("The NPC content runtime is not initialised.");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            if (!_ensured)
            {
                await using var ddl = new NpgsqlCommand(SchemaSql, connection);
                await ddl.ExecuteNonQueryAsync(cancellationToken);
                _ensured = true;
            }

            var shops = new Dictionary<string, List<NpcShopOverrideRow>>(StringComparer.Ordinal);
            var fingerprint = new System.Text.StringBuilder();
            await using (var command = new NpgsqlCommand(
                "SELECT service, item_id, price, category, quality, grade, bound, stack_marker FROM public.npc_shop_overrides ORDER BY service, position",
                connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var service = reader.GetString(0);
                    var row = new NpcShopOverrideRow(
                        checked((uint)reader.GetInt32(1)),
                        reader.GetInt32(2),
                        checked((byte)reader.GetInt16(3)),
                        checked((byte)reader.GetInt16(4)),
                        checked((byte)reader.GetInt16(5)),
                        checked((byte)reader.GetInt16(6)),
                        checked((byte)reader.GetInt16(7)));
                    if (!shops.TryGetValue(service, out var list))
                    {
                        shops[service] = list = [];
                    }

                    list.Add(row);
                    fingerprint.Append(service).Append(row).Append('\n');
                }
            }

            var teleports = new List<NpcTeleportOverride>();
            await using (var command = new NpgsqlCommand(
                "SELECT npc_key, sub_id, target_map_id, pos_x, pos_z, minimum_level FROM public.npc_teleport_overrides",
                connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var teleport = new NpcTeleportOverride(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        reader.GetInt16(2),
                        reader.GetFloat(3),
                        reader.GetFloat(4),
                        reader.GetInt32(5));
                    teleports.Add(teleport);
                    fingerprint.Append(teleport).Append('\n');
                }
            }

            var editorAdmins = new List<int>();
            await using (var command = new NpgsqlCommand("SELECT id FROM public.accounts WHERE editor_admin", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    editorAdmins.Add(reader.GetInt32(0));
                    fingerprint.Append("admin").Append(reader.GetInt32(0)).Append('\n');
                }
            }

            var activators = new Dictionary<uint, ItemActivator>();
            await using (var command = new NpgsqlCommand("SELECT item_id, skill_id, cooldown_seconds FROM public.item_activators", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var activator = new ItemActivator(reader.GetInt32(1), reader.GetInt32(2));
                    activators[checked((uint)reader.GetInt32(0))] = activator;
                    fingerprint.Append(reader.GetInt32(0)).Append(activator).Append('\n');
                }
            }

            var windows = new Dictionary<string, List<EventWindow>>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand("SELECT activity, day_of_week, start_minute, duration_minutes FROM public.event_schedule_overrides ORDER BY 1, 2, 3", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var activity = reader.GetString(0);
                    var window = new EventWindow(reader.GetInt16(1), reader.GetInt16(2), reader.GetInt16(3));
                    if (!windows.TryGetValue(activity, out var list))
                    {
                        windows[activity] = list = [];
                    }

                    list.Add(window);
                    fingerprint.Append(activity).Append(window).Append('\n');
                }
            }

            var notices = new List<ServerNotice>();
            await using (var command = new NpgsqlCommand("SELECT id, message, delivery, day_mask, start_minute, end_minute, repeat_minutes, first_day, last_day FROM public.server_notices WHERE enabled ORDER BY id", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var notice = new ServerNotice(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2) == "banner",
                        reader.GetInt16(3),
                        reader.GetInt16(4),
                        reader.GetInt16(5),
                        reader.GetInt16(6),
                        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7),
                        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8));
                    notices.Add(notice);
                    fingerprint.Append(notice).Append('\n');
                }
            }

            var populationMinutes = EventContent.DefaultPopulationMinutes;
            await using (var command = new NpgsqlCommand("SELECT value FROM public.notice_settings WHERE name = 'population_minutes'", connection))
            {
                if (await command.ExecuteScalarAsync(cancellationToken) is int stored)
                {
                    populationMinutes = stored;
                }
            }

            fingerprint.Append("population").Append(populationMinutes).Append('\n');

            var loaded = fingerprint.ToString();
            if (loaded == _loaded)
            {
                return;
            }

            _loaded = loaded;
            _revision++;
            NpcContentOverrides.Install(new NpcContentSnapshot(
                _revision,
                shops.ToDictionary(static p => p.Key, static p => (IReadOnlyList<NpcShopOverrideRow>)p.Value, StringComparer.Ordinal),
                teleports,
                editorAdmins,
                activators,
                new EventContent(
                    windows.ToDictionary(static p => p.Key, static p => (IReadOnlyList<EventWindow>)p.Value, StringComparer.Ordinal),
                    notices,
                    populationMinutes)));
            Console.WriteLine($"[npc-content] loaded revision {_revision}: {shops.Count} shops, {teleports.Count} teleports");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Task<string> DescribeEventsAsync(CancellationToken cancellationToken)
    {
        var rates = ServerRates.Current;
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            revision = NpcContentOverrides.Current.Revision,
            realm_time = Calendar?.ToRealmTime(DateTimeOffset.UtcNow).ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            time_zone = Calendar?.TimeZoneId,
            population_default = EventContent.DefaultPopulationMinutes,
            window_minutes = rates.Absolute(ServerRateKeys.BattlefieldWindowMinutes),
            shift_hours = rates.Absolute(ServerRateKeys.BattlefieldShiftHours),
            activities = new object[]
            {
                new
                {
                    key = EventContent.Pindus,
                    name = "Pindus Mountains battlefield",
                    always_open = rates.Absolute(ServerRateKeys.PindusAlwaysOpen) == 1,
                    compiled = BattlefieldSchedulePolicy.DescribeCompiled(BattlefieldDestinationKind.Pindus).ToArray()
                },
                new
                {
                    key = EventContent.NiMini,
                    name = "Ni Mini Valley",
                    always_open = rates.Absolute(ServerRateKeys.NiMiniAlwaysOpen) == 1,
                    compiled = BattlefieldSchedulePolicy.DescribeCompiled(BattlefieldDestinationKind.NiMiniLower).ToArray()
                }
            }
        }));
    }

    // One shop at a time: the management port limits a response to 64 KiB.
    private static Task<string> DescribeShopAsync(string name, CancellationToken cancellationToken)
    {
        IReadOnlyList<object> rows = [];
        if (name is PacketBuilder.MallAthensService or PacketBuilder.MallSpartaService)
            return Task.FromResult(JsonSerializer.Serialize(PacketBuilder.DescribeCompiledMall(
                name == PacketBuilder.MallAthensService ? GameDefaults.AthensCamp : GameDefaults.SpartaCamp)));
        try
        {
            if (Enum.TryParse<CapitalNpcServiceKind>(name, out var service) && CapitalNpcServiceProtocol.IsShop(service))
            {
                rows = PacketBuilder.DescribeCompiledCapitalShop(service);
            }
        }
        catch (ArgumentException)
        {
            // A malformed captured stream has nothing to edit.
        }

        return Task.FromResult(JsonSerializer.Serialize(rows));
    }

    // Everything the editor needs, from the same code the game uses: compiled shop lists, teleporter buttons, which NPC runs which shop.
    private static async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var dataSource = _dataSource ?? throw new InvalidOperationException("The NPC content runtime is not initialised.");
        var shops = Enum.GetValues<CapitalNpcServiceKind>()
            .Where(static service => CapitalNpcServiceProtocol.IsShop(service))
            .Select(static service => service.ToString())
            .ToArray().Concat([PacketBuilder.MallAthensService, PacketBuilder.MallSpartaService, PacketBuilder.MallAdminService]);

        var npcs = new List<object>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(
            """
            SELECT d.map_id, d.npc_key, d.template_key, d.object_id, d.interaction_id, d.pos_x, d.pos_z
            FROM public.npc_spawn_definitions d
            JOIN public.npc_content_publication p ON p.family = 'npcs' AND p.revision = d.revision
            ORDER BY d.map_id, d.npc_key
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(1);
                var interaction = Convert.ToUInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
                // Published interaction ids can sit one away from the captured ids the protocol names.
                var isService = CapitalNpcServiceProtocol.TryResolve(key, interaction, out var service) ||
                    CapitalNpcServiceProtocol.TryResolve(key, interaction - 1, out service) ||
                    CapitalNpcServiceProtocol.TryResolve(key, interaction + 1, out service);
                npcs.Add(new
                {
                    map = (int)reader.GetInt16(0),
                    key,
                    template = reader.GetString(2),
                    x = reader.GetFloat(5),
                    z = reader.GetFloat(6),
                    service = isService ? service.ToString() : null,
                    shop = isService && CapitalNpcServiceProtocol.IsShop(service),
                    transporter = TransporterProtocol.Destinations.Any(d => d.NpcKey == key) ||
                        BattlefieldTransporterProtocol.IsEditableNpc(key) ||
                        key is DuelArenaCapturedLayout.GatekeeperNpcKey or DuelArenaCapturedLayout.WardNpcKey
                });
            }
        }

        var snapshot = NpcContentOverrides.Current;
        return JsonSerializer.Serialize(new
        {
            revision = snapshot.Revision,
            shops,
            npcs,
            teleports = TransporterProtocol.Destinations.Select(static d => new
            {
                npc = d.NpcKey,
                sub = d.SubId,
                name = d.DisplayName,
                source_map = (int)d.SourceMapId,
                target_map = (int)d.TargetMapId,
                level = d.MinimumLevel,
                x = (float?)null,
                z = (float?)null
            }).Concat(BattlefieldTransporterProtocol.EditableButtons().Select(static b => new
            {
                npc = b.NpcKey,
                sub = b.SubId,
                name = b.Destination.DisplayName,
                source_map = b.Destination.SourceMapId,
                target_map = b.Destination.TargetMapId,
                level = b.Destination.MinimumLevel,
                x = (float?)b.Destination.TargetX,
                z = (float?)b.Destination.TargetZ
            })).Concat(new[]
            {
                new
                {
                    npc = DuelArenaCapturedLayout.GatekeeperNpcKey,
                    sub = DuelArenaCapturedTransportProtocol.TravelSubId,
                    name = "Into the arena (normally a random entry point)",
                    source_map = (int)DuelArenaCapturedLayout.MapId,
                    target_map = (int)DuelArenaCapturedLayout.MapId,
                    level = 1,
                    x = (float?)null,
                    z = (float?)null
                },
                new
                {
                    npc = DuelArenaCapturedLayout.WardNpcKey,
                    sub = DuelArenaCapturedTransportProtocol.TravelSubId,
                    name = "Back to the arena lobby",
                    source_map = (int)DuelArenaCapturedLayout.MapId,
                    target_map = (int)DuelArenaCapturedLayout.MapId,
                    level = 1,
                    x = (float?)DuelArenaCapturedLayout.UpperArrivalX,
                    z = (float?)DuelArenaCapturedLayout.UpperArrivalZ
                }
            })
        });
    }
}
