using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Role-scoped Harmony install — replaces blanket PatchAll.
    /// </summary>
    internal static class ModPatchInstaller
    {
        private static int installedCount;

        internal static int InstalledCount => installedCount;

        internal static void InstallAll(Harmony harmony)
        {
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));

            installedCount = 0;
            ModRuntimeContext.Initialize();

            PatchTypes(harmony, SharedPatchTypes);

            // Server sim patches: dedicated always; joinable clients keep them for host / no-op prefixes.
            if (ModRuntimeContext.IsDedicatedGameServer || ModRuntimeContext.ShouldInstallClientPatches())
                PatchServerManual(harmony);

            if (ModRuntimeContext.ShouldInstallClientPatches())
            {
                PatchTypes(harmony, ClientPatchTypes);
                InstallClientPresentation(harmony);
            }

            PracticeLog.Info("[PHLPractice] Patch install: role=" + ModRuntimeContext.RoleLabel +
                      " dedicated=" + ModRuntimeContext.IsDedicatedGameServer +
                      " count=" + installedCount);
        }

        internal static void InstallClientPresentation(Harmony harmony)
        {
            if (!ModRuntimeContext.ShouldInstallClientPresentation()) return;

            if (!MultiSheetClientSettings.SkipMinimap)
                MinimapRinkView.InstallPatch(harmony);

            PatchNestedTypes(harmony, typeof(MinimapSessionOverride), "BlockShowWhileSuppressedPatch");

            if (MultiSheetClientSettings.AllowRinkChanges)
                TrlReskinBridge.SetHarmony(harmony);
        }

        private static void PatchServerManual(Harmony harmony)
        {
            PracticeFlowServer.InstallSpawnPatch(harmony);
            PatchTypes(harmony, ServerPatchTypes);
            PatchNestedTypes(harmony, typeof(CptSpawnCompat), "MovementStartClaimPrefixPatch", "MovementStartCompatPatch", "MovementStartNreFinalizerPatch");
        }

        private static readonly Type[] SharedPatchTypes =
        {
            typeof(RinkCommands),
        };

        private static readonly Type[] ServerPatchTypes =
        {
            typeof(PracticePhaseLockPatch),
            typeof(PracticeWarmupTimerPatch),
            typeof(PracticePositionSelectTogglePatch),
            typeof(PracticeBlockPositionRolePatch),
            typeof(RinkStripVoteCastVotePatch),
            typeof(NapSleepSync_ToggleNapPatch),
            typeof(NapSleepSync_StandUpPatch),
            typeof(NapSleepSync_ClearClientPatch),
            typeof(NapIdleService_StandUpPatch),
            typeof(NapIdleService_ClearClientPatch),
        };

        private static readonly Type[] ClientPatchTypes =
        {
            typeof(GoalieTrackPuckPatch),
            typeof(PracticeSelectScreenShowPatch),
            typeof(PracticeTeamSelectHidePatch),
            typeof(PracticeManualTeamSelectPatch),
            typeof(PracticePauseSelectTeamPatch),
            typeof(PracticePauseSelectPositionPatch),
            typeof(PracticeClockPatch),
            typeof(PracticePhaseLabelPatch),
            typeof(LocalBodyShadowHidePatch),
            typeof(LocalBodyShadowShowPatch),
            typeof(PracticeScoreboardBlockFakeAddPlayerPatch),
            typeof(PracticeScoreboardBlockFakeStylePlayerPatch),
            typeof(PracticeScoreboardHidePositionPatch),
            typeof(PracticeScoreboardBlockFakeUpdatePlayerPingPatch),
            typeof(PracticeScoreboardPurgeFakePlayersOnShowPatch),
            typeof(RinkScoreboardTabInitializePatch),
            typeof(RinkScoreboardTabShowPatch),
            typeof(RinkScoreboardTabHidePatch),
            typeof(PracticeScoreboardTabTogglePatch),
            typeof(PracticeScoreboardTabCancelPatch),
            typeof(RinkMotdPausePatch),
        };

        private static void PatchTypes(Harmony harmony, Type[] types)
        {
            if (types == null) return;
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == null) continue;
                try
                {
                    harmony.CreateClassProcessor(types[i]).Patch();
                    installedCount++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Patch failed for " + types[i].Name + ": " + ex.Message);
                }
            }
        }

        private static void PatchNestedTypes(Harmony harmony, Type outer, params string[] nestedNames)
        {
            if (outer == null || nestedNames == null) return;
            for (int i = 0; i < nestedNames.Length; i++)
            {
                Type nested = outer.GetNestedType(nestedNames[i], BindingFlags.NonPublic);
                if (nested == null)
                {
                    Debug.LogWarning("[PHLPractice] Nested patch type not found: " + outer.Name + "+" + nestedNames[i]);
                    continue;
                }
                try
                {
                    harmony.CreateClassProcessor(nested).Patch();
                    installedCount++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Nested patch failed for " + nestedNames[i] + ": " + ex.Message);
                }
            }
        }
    }
}
