using System.Text.Json.Nodes;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<string> ApplyOperatorBuffForAdminAsync(
        SkillStatusEffectDefinition definition,
        CancellationToken cancellationToken)
    {
        var applied = await _registry.ApplyRuntimeStatusAndPublishAsync(
            _session, definition, DateTimeOffset.UtcNow, "editor-buff", cancellationToken);
        return new JsonObject
        {
            ["ok"] = true,
            ["character"] = _character!.Name,
            ["applied"] = applied,
            ["skillId"] = definition.SkillId,
            ["statusId"] = definition.StatusId,
            ["requestedDurationSeconds"] = checked((long)definition.Duration.TotalSeconds),
            ["persistence"] = "session"
        }.ToJsonString();
    }
}
