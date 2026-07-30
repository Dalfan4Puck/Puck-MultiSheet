using HarmonyLib;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Human goalies holding track-puck normally follow GetPlayerPuck (last touched).
    /// Override for goalies so the camera tracks whichever puck is closing on them fastest.
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
                if (player == null || player.Role != PlayerRole.Goalie)
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
