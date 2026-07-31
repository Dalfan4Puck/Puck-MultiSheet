using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MaxPractice;
using PuckChasers;
using UnityEngine.UIElements;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Hide practice / Puck Chasers fake players from the Tab scoreboard without touching
    /// PlayerManager lists — filtering GetSpawnedPlayers breaks AI simulation.
    /// UIScoreboardController calls AddPlayer/StylePlayer/UpdatePlayerPing on spawn and
    /// stat changes; block fakes at those entry points and purge stragglers on Show/refresh.
    /// </summary>
    internal static class PracticeScoreboardFakePlayerFilter
    {
        internal static bool ShouldHide(Player player)
        {
            if (player == null)
                return false;

            // Puck Chasers — always hide regardless of MaxPractice spawn/replay bypass flags.
            if (StandaloneFakePlayerDetector.IsAnyFakePlayer(player))
                return true;
            if (PuckChasers.GoalieAIManager.IsAIGoalie(player))
                return true;
            if (SkaterAIManager.IsAISkater(player))
                return true;

            return FakePlayerDetector.ShouldExcludeFromPopulation(player);
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

    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.AddPlayer))]
    internal static class PracticeScoreboardBlockFakeAddPlayerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player) =>
            !PracticeScoreboardFakePlayerFilter.ShouldHide(player);
    }

    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.StylePlayer))]
    internal static class PracticeScoreboardBlockFakeStylePlayerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player) =>
            !PracticeScoreboardFakePlayerFilter.ShouldHide(player);
    }

    /// <summary>Positionless practice — POS column stays blank (no RW/C from stray claims).</summary>
    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.StylePlayer))]
    internal static class PracticeScoreboardHidePositionPatch
    {
        private static readonly FieldInfo PlayerRowMapField =
            typeof(UIScoreboard).GetField("playerVisualElementMap", BindingFlags.Instance | BindingFlags.NonPublic);

        [HarmonyPostfix]
        private static void Postfix(UIScoreboard __instance, Player player)
        {
            if (!PracticeFlowClient.IsOnPracticeServer || player == null) return;
            if (PracticeScoreboardFakePlayerFilter.ShouldHide(player)) return;
            if (PlayerRowMapField == null) return;

            try
            {
                var map = PlayerRowMapField.GetValue(__instance) as Dictionary<Player, VisualElement>;
                if (map == null || !map.TryGetValue(player, out VisualElement row) || row == null) return;

                VisualElement playerEl = row.Q<VisualElement>("Player");
                Label posLabel = playerEl?.Q<Label>("PositionLabel");
                if (posLabel != null) posLabel.text = string.Empty;
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.UpdatePlayerPing))]
    internal static class PracticeScoreboardBlockFakeUpdatePlayerPingPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player player) =>
            !PracticeScoreboardFakePlayerFilter.ShouldHide(player);
    }

    /// <summary>Show is declared on <see cref="UIView"/>; UIScoreboard inherits it.</summary>
    [HarmonyPatch(typeof(UIView), nameof(UIView.Show))]
    internal static class PracticeScoreboardPurgeFakePlayersOnShowPatch
    {
        [HarmonyPostfix]
        private static void Postfix(UIView __instance)
        {
            if (__instance is UIScoreboard scoreboard)
                PracticeScoreboardFakePlayerRows.Purge(scoreboard);
        }
    }

    /// <summary>StyleServer refreshes header + player rows — purge bots that slipped in mid-game.</summary>
    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.StyleServer))]
    internal static class PracticeScoreboardPurgeFakePlayersOnStyleServerPatch
    {
        [HarmonyPostfix]
        private static void Postfix(UIScoreboard __instance)
        {
            PracticeScoreboardFakePlayerRows.Purge(__instance);
        }
    }
}
