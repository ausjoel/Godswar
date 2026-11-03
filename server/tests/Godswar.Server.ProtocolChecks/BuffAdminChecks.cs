using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Godswar.Server.Operations;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Operators;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class BuffAdminChecks
{
    public const string CheckName = "Editor buff catalogue and native duration validation preserve authored gameplay";
    public static async Task RunAsync()
    {
        var registry = new GameSessionRegistry();
        var response = await registry.HandleOperatorAdminAsync(
            new OperatorAdminRequest("GET", "/admin/buffs/catalog", []), null!, CancellationToken.None);
        Check.Equal(200, response.StatusCode, "catalogue GET needs no character or write store");
        using var doc = JsonDocument.Parse(response.Json);
        Check.Equal("session", doc.RootElement.GetProperty("persistence").GetString()!, "runtime buffs do not imply offline persistence");
        var rows = doc.RootElement.GetProperty("buffs").EnumerateArray().ToArray();
        Check.Equal(25, rows.Length, "all five authored buff families and ranks exposed");
        Check.Equal(rows.Length, rows.Select(row => row.GetProperty("skillId").GetInt32()).Distinct().Count(), "catalogue identities unique");
        Check.Equal(uint.MaxValue, doc.RootElement.GetProperty("maxDurationSeconds").GetUInt32(), "duration bound comes from native wire encoding");
        foreach (var row in rows)
        {
            var id = row.GetProperty("skillId").GetInt32();
            Check.True(SkillStatusEffectCatalog.TryGet(id, out var authored) &&
                GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["skillId"] = id }, out var parsed, out _) &&
                authored == parsed, "default request preserves authored rank, effects and duration");
            Check.Equal(authored.StatusId, row.GetProperty("statusId").GetUInt32(), "native status identity retained");
        }
        Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["skillId"] = 774, ["durationSeconds"] = (long)uint.MaxValue }, out var longBuff, out _) &&
            longBuff.Duration == TimeSpan.FromSeconds(uint.MaxValue) && longBuff.DodgeBonus == 3_000 &&
            longBuff.CriticalResistanceBonus == 1_000,
            "long GM duration preserves actual Gaia attributes without an arbitrary one-day cap");
        foreach (var invalid in new JsonObject[]
        {
            new() { ["skillId"] = 999999 },
            new() { ["skillId"] = 774, ["durationSeconds"] = 0 },
            new() { ["skillId"] = 774, ["durationSeconds"] = -1 },
            new() { ["skillId"] = 774, ["durationSeconds"] = (long)uint.MaxValue + 1 },
            new() { ["skillId"] = 774, ["durationSeconds"] = 1.5 },
            new() { ["skillId"] = 774, ["durationSeconds"] = null }
        })
            Check.True(!GameSessionRegistry.TryReadOperatorBuff(invalid, out _, out _), "malformed, unknown or unencodable buffs refused");
        var now = DateTimeOffset.UtcNow;
        Check.Equal(TimeSpan.Zero, GameSessionRegistry.GetRuntimeStatusExpiryDelay(now, now), "elapsed buff has no timer delay");
        Check.Equal(TimeSpan.FromSeconds(5), GameSessionRegistry.GetRuntimeStatusExpiryDelay(now.AddSeconds(5), now), "normal expiry keeps exact authored interval");
        Check.Equal(TimeSpan.FromDays(1), GameSessionRegistry.GetRuntimeStatusExpiryDelay(now.AddSeconds(uint.MaxValue), now), "long native duration uses bounded cancellable timer chunks");
        await CheckManagementRouteAsync(registry);

    }
    private static async Task CheckManagementRouteAsync(GameSessionRegistry registry)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var state = new ServerOperationalState(ServerReadinessDependency.None);
        state.TryMarkRunning();
        var previous = OperatorAdminApi.Handler;
        OperatorAdminApi.Handler = (request, token) => registry.HandleOperatorAdminAsync(request, null!, token);
        var server = new ManagementHttpServer(new ManagementOptions { Port = port }, state.GetSnapshot,
            _ => ValueTask.FromResult(new ManagementPayload(ManagementContentType.Json, "{}"u8.ToArray())),
            _ => ValueTask.FromResult(new ManagementPayload(ManagementContentType.Json, "{}"u8.ToArray())),
            _ => false, _ => ValueTask.FromResult(ManagementDrainResult.AlreadyDraining));
        using var stop = new CancellationTokenSource();
        var task = server.RunAsync(stop.Token);
        try
        {
            var endpoint = await server.WaitUntilStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(endpoint.Address.Equals(IPAddress.Loopback), "buff management catalogue remains loopback-only");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var json = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/admin/buffs/catalog"));
            Check.Equal(25, json.RootElement.GetProperty("buffs").GetArrayLength(), "actual HTTP catalogue route returns complete authored content");
        }
        finally
        {
            stop.Cancel();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            OperatorAdminApi.Handler = previous;
        }
    }

}
