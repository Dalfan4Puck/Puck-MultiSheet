using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MaxPractice;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Hide MaxPractice fake players (DummyRed/DummyBlue, traffic, passers) from the Tab
    /// scoreboard. PlayerManager list filters alone are not enough — UIScoreboard.AddPlayer
    /// is called when bots spawn and builds rows independently of GetPlayers().
    /// </summary>
    internal static class PracticeScoreboardFakePlayerFilter
    {
        internal static bool ShouldHide(Player player)
        {
            if (player == null)
                return false;
            if (GoalieAIManager.IsManipulatingFakePlayers || GoalieAIManager.bypassFilter)
                return false;
            return FakePlayerDetector.ShouldExcludeFromPopulation(player);
        }

        internal static void FilterList(List<Player> players)
        {
            if (players == null || players.Count == 0)
                return;
            if (GoalieAIManager.IsManipulatingFakePlayers || GoalieAIManager.bypassFilter)
                return;

            players.RemoveAll(p => p == null || FakePlayerDetector.ShouldExcludeFromPopulation(p));
        }
    }

    internal static class PracticeScoreboardFakePlayerRows
    {
        private static readonly FieldInfo PlayerRowMapField =
            typeof(UIScoreboard).GetField("playerVisualElementMap", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void Purge(UIScoreboard scoreboard)
        {
            if (scoreboard == null || PlayerRowMapField == null)
                return;

            try
            {
                var map = PlayerRowMapField.GetValue(scoreboard) as Dictionary<Player, VisualElement>;
                if (map == null || map.Count == 0)
                    return;

                var fakes = map.Keys.Where(PracticeScoreboardFakePlayerFilter.ShouldHide).ToList();
                for (int i = 0; i < fakes.Count; i++)
                {
                    Player fake = fakes[i];
                    if (fake == null)
                        continue;

                    try { scoreboard.RemovePlayer(fake); }
                    catch { map.Remove(fake); }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.GetPlayers), new[] { typeof(bool) })]
    internal static class PracticeScoreboardFilterGetPlayersBoolPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref List<Player> __result) =>
            PracticeScoreboardFakePlayerFilter.FilterList(__result);
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.GetSpawnedPlayers), new[] { typeof(bool) })]
    internal static class PracticeScoreboardFilterGetSpawnedPlayersBoolPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref List<Player> __result) =>
            PracticeScoreboardFakePlayerFilter.FilterList(__result);
    }

    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.AddPlayer))]
    internal static class PracticeScoreboardBlockFakeAddPlayerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player) =>
            !PracticeScoreboardFakePlayerFilter.ShouldHide(player);
    }

    [HarmonyPatch(typeof(UIScoreboard), "UpdatePlayer", new[] { typeof(Player) })]
    internal static class PracticeScoreboardBlockFakeUpdatePlayerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player) =>
            !PracticeScoreboardFakePlayerFilter.ShouldHide(player);
    }

    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.Show))]
    internal static class PracticeScoreboardPurgeFakePlayersOnShowPatch
    {
        [HarmonyPostfix]
        private static void Postfix(UIScoreboard __instance) =>
            PracticeScoreboardFakePlayerRows.Purge(__instance);
    }

    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.StyleServer))]
    internal static class PracticeScoreboardHumanCountPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref int playerCount) =>
            playerCount = FakePlayerDetector.CountRealPopulationPlayers();
    }
}
