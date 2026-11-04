using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // CLASSCHANGE: time the "Class shifted!" line stays on screen before the connection is
    // closed so the client re-reads the character, skill list and talent window at login.
    private const int ClassChangeDisconnectDelayMilliseconds = 2000;

    private bool _classChangeCommitted;

    /// <summary>
    /// CLASSCHANGE: the Class Shifter's four "Become a [class]" buttons (page two of
    /// <c>NpcFunWork.lua</c>, 110-113 under entry 100). Returns true when the click was one
    /// of them and the feature is on; otherwise the dialogue's old menu answers run unchanged.
    /// </summary>
    private async Task<bool> TryHandleClassChangeAsync(
        NpcSpawnDefinition npc,
        ScriptedNpcDialogue dialogue,
        int dialogIndex,
        int selection,
        int pageSelection,
        CancellationToken cancellationToken)
    {
        if (dialogue.FunctionNumber != ClassChangePolicy.FunctionNumber ||
            _character is null ||
            _account is null)
        {
            return false;
        }

        var click = ClassChangePolicy.ResolveClassClick(selection, pageSelection);
        if (click < 0 ||
            !ClassChangePolicy.TryResolveTargetProfession(click, out var target) ||
            !ClassChangePolicy.IsEnabled())
        {
            return false;
        }

        if (_classChangeCommitted)
        {
            // The change is in; the connection is about to close.
            return true;
        }

        var store = CharacterClassChangeControls.Store;
        if (store is null || !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            Console.WriteLine($"[class-change] unavailable npc={npc.InteractionId} character={_character.Name}");
            return true;
        }

        var freeLevel = ClassChangePolicy.FreeLevel(ServerRates.Current);
        CharacterClassChangeResult result;
        try
        {
            result = await store.ChangeClassAsync(
                new CommandSubject(_account.Id, _character.Id),
                ownership,
                _character.Profession,
                target,
                freeLevel,
                cancellationToken);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return true;
        }

        Console.WriteLine(
            $"[class-change] character={_character.Name} click={click} profession={_character.Profession}->{target} " +
            $"level={_character.Level} free-level={freeLevel} status={result.Status} receipt={result.ReceiptId} " +
            $"permit={result.PermitUsed} talent-points={result.TalentPoints} refunded={result.TalentPointsRefunded} " +
            $"sealed={result.SealedSkills} unsealed={result.UnsealedSkills} granted={result.GrantedSkills}");

        int? line = result.Status switch
        {
            CharacterClassChangeStatus.Done => ClassChangePolicy.ShiftedLine,
            CharacterClassChangeStatus.SameClass => ClassChangePolicy.SameClassLine,
            CharacterClassChangeStatus.EquipmentWorn => ClassChangePolicy.EquipmentWornLine,
            CharacterClassChangeStatus.PermitMissing => ClassChangePolicy.PermitMissingLine,
            // REVIEW FIX: the class changed underneath the click (GM/editor) or the
            // character is gone: close the dialog with "Please make a choice".
            CharacterClassChangeStatus.Stale => ClassChangePolicy.StaleLine,
            CharacterClassChangeStatus.CharacterNotFound => ClassChangePolicy.StaleLine,
            _ => null
        };
        if (result.Status != CharacterClassChangeStatus.Done)
        {
            if (line is { } refusal)
            {
                await SendScriptedNpcDialogueReplyAsync(
                    npc, dialogue, dialogIndex, click, [refusal], cancellationToken);
            }

            return true;
        }

        // Committed. Bring the live character in line so nothing written from memory
        // contradicts the row, show the script's own success line, then close the
        // connection: the next login reloads profession, skills and talents from the
        // database (the same refresh the plastic surgeon and faction changer use).
        _classChangeCommitted = true;
        _character.Profession = result.Profession;
        _character.TalentPoints = result.TalentPoints;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        await SendScriptedNpcDialogueReplyAsync(
            npc, dialogue, dialogIndex, click, [ClassChangePolicy.ShiftedLine], cancellationToken);
        try
        {
            await Task.Delay(ClassChangeDisconnectDelayMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        _session.Disconnect();
        return true;
    }
}
