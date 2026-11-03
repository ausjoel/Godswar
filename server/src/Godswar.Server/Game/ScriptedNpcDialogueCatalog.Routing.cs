namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The scripted dialogues an NPC answers with, keyed by the client function number
    /// each one owns, or <see langword="null"/> when the NPC is not one of them.
    /// </summary>
    /// <remarks>
    /// Keyed, because one NPC can advertise several functions: the open packet's
    /// <c>10067</c> field at <c>+12</c> is a base-1000 packed list of function numbers,
    /// and the client later echoes the chosen one back as the <c>10069</c> dialog index.
    /// A single-script NPC therefore holds one entry; an NPC such as the title expert,
    /// which owns both 称谓鉴定 and 称谓奖励, holds one entry per function and needs
    /// no further wiring. An index absent from the map is left unanswered.
    ///
    /// Split out from <c>ScriptedNpcDialogueCatalog.cs</c>, which had reached the
    /// repository's 20,000-byte file limit once this table grew. This is the only place
    /// an NPC is bound to a dialogue, and both the open packet and the click traffic
    /// route through it, so a dialogue registered here needs no handler change.
    /// </remarks>
    private static Dictionary<int, ScriptedNpcDialogue>? ResolveScriptedNpcDialogues(
        NpcSpawnDefinition npc) => npc.NpcKey switch
        {
            "Athens_083" or "Sparta_083" => ByFunction(MysteriousElderDialogue),
            "Athens_120" or "Sparta_120" => ByFunction(ProfessionMentorDialogue),
            "Athens_139" or "Sparta_139" => ByFunction(PersonalHelperDialogue),
            "Athens_072" or "Sparta_072" => ByFunction(EventTransporterDialogue),
            "Athens_059" or "Sparta_059" => ByFunction(WeddingPriestDialogue),
            "Athens_058" or "Sparta_058" => ByFunction(CupidDialogue),
            "Athens_044" or "Sparta_044" => ByFunction(ClassShifterDialogue),
            "Athens_075" or "Sparta_075" => ByFunction(FortuneTellerDialogue),
            "Athens_091" or "Sparta_091" => ByFunction(CupidsShadowDialogue),
            "Athens_109" or "Sparta_109" => ByFunction(ApothecaryDialogue),
            "Athens_138" or "Sparta_138" => ByFunction(PlasticSurgeonDialogue),
            "Athens_140" or "Sparta_140" => ByFunction(MysteryMerchantDialogue),
            "Athens_053" or "Sparta_053" => ByFunction(HalloweenEnvoyDialogue),
            "Athens_084" or "Sparta_084" => ByFunction(FestivalEnvoyDialogue),
            "Athens_116" or "Sparta_116" => ByFunction(MountFeederDialogue),
            "Athens_118" or "Sparta_118" => ByFunction(RandomQuestManagerDialogue),
            "Athens_092" or "Sparta_092" => ByFunction(SpringFestivalEnvoyDialogue),
            "Athens_038" or "Sparta_039" => ByFunction(GuildQuestSupervisorDialogue),
            "Athens_050" or "Sparta_050" => ByFunction(GuildAltarDialogue),
            "Athens_051" or "Sparta_051" => ByFunction(GuildMemberAdvisorDialogue),
            "Athens_076" or "Sparta_076" => ByFunction(WarSupplierDialogue),
            "Sparta_026" => ByFunction(LunarPriestDialogue),
            "Athens_128" or "Sparta_128" => ByFunction(EasterEnvoyDialogue),
            "Athens_119" or "Sparta_119" => ByFunction(IggAnniversaryEnvoyDialogue),
            "Athens_141" or "Sparta_141" => ByFunction(FactionChangerDialogue),
            "Athens_129" or "Sparta_129" => ByFunction(RichManEnvoyDialogue),
            "Athens_130" or "Sparta_130" => ByFunction(SacredSealerDialogue),
            // Both title functions belong to this one NPC, so the open packet advertises
            // 12 and 13 and the click is routed by the echoed dialog index.
            "Athens_071" or "Sparta_071" =>
                ByFunction(TitleIdentificationDialogue, TitleRewardDialogue),
            "Athens_078" or "Sparta_078" => ByFunction(FitnessTrainerDialogue),
            "Athens_079" or "Sparta_079" => ByFunction(FitnessTrainerDialogue),
            "Athens_080" or "Sparta_080" => ByFunction(FitnessTrainerDialogue),
            "Athens_081" or "Sparta_081" => ByFunction(FitnessTrainerDialogue),
            "Athens_082" or "Sparta_082" => ByFunction(FitnessTrainerDialogue),
            "Athens_125" or "Sparta_125" => ByFunction(TrojanWarCoordinatorDialogue),
            // Captured on 2026-09-28. Sparta_115 owns function 66 and Sparta_124
            // function 67; the latter shares that number with Sparta_125 above,
            // which is exactly why each NPC keeps its own entry - the routing
            // table is keyed by the npc, so the two windows cannot borrow each
            // other's buttons.
            "Sparta_115" => ByFunction(SummerGiftEnvoyDialogue),
            "Sparta_124" => ByFunction(TroyEventAwarderDialogue),
            // The battlefield awarder. Function 3 is NPC_FLAG_SYS_WAR in the
            // client's own table; the reference advertised exactly that number on
            // 2026-09-28, and the quest page cannot take the click first because
            // the gate only fires for a carried starter-chain quest this npc
            // responds to.
            //
            // Athens_073 is a different actor with its own appearance and id, but
            // it is the same endpoint: both capitals' awarders are driven by
            // NpcFunWar.lua, so they share this one dialogue and each keeps its
            // own identity through the routing table's npc key.
            "Athens_073" or "Sparta_073" =>
                ByFunction(BattlefieldAwarderDialogue),
            "Labyrinth_006" or "Labyrint2_006" => ByFunction(ZeusEnvoyDialogue),
            "WarField_003" or "WarField_008" => ByFunction(BattlefieldCrierDialogue),
            _ => null
        };

    /// <summary>
    /// Indexes scripted dialogues by the client function number they own, so an NPC with
    /// several functions adds arguments here and nothing else.
    /// </summary>
    private static Dictionary<int, ScriptedNpcDialogue> ByFunction(
        params ScriptedNpcDialogue[] dialogues) =>
        dialogues.ToDictionary(dialogue => dialogue.FunctionNumber);
}
