using HarmonyLib;

namespace PHLPracticeModPack
{
    /// <summary>Block /v on rink-strip votes from players not standing on the target rink.</summary>
    [HarmonyPatch(typeof(Vote), nameof(Vote.CastVote))]
    internal static class RinkStripVoteCastVotePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Vote __instance, string steamId, bool inFavour)
        {
            if (!inFavour) return true;
            return RinkStripVote.TryAllowStripVoteCast(steamId, __instance);
        }
    }
}
