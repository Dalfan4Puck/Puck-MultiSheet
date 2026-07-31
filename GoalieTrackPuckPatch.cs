using HarmonyLib;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Track-puck normally follows GetPlayerPuck (last touched).
    /// Pass-practice rinks: skaters and goalies follow the active / queued feed puck.
    /// Goalie-practice rinks: goalies follow the threatening practice shot.
    /// </summary>
    [HarmonyPatch(typeof(PuckManager), nameof(PuckManager.GetPlayerPuck))]
    internal static class GoalieTrackPuckPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ulong clientId, ref Puck __result)
        {
            try
            {
                PlayerManager playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (playerManager == null)
                    return true;

                Player player = playerManager.GetPlayerByClientId(clientId);
                if (player == null)
                    return true;

                if (GoalieThreatPuckSelector.TryGetPracticeTrackPuck(player, out Puck practice))
                {
                    __result = practice;
                    return false;
                }

                if (player.Role != PlayerRole.Goalie)
                    return true;

                Puck threat = GoalieThreatPuckSelector.SelectForPlayer(player);
                if (threat == null)
                    return true;

                __result = threat;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
