using MaxPractice;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Spawns a real MaxPractice AI goalie player at the training hive net (server only).
/// </summary>
public static class FlamiePracTrainingGoalie
{
    public static void SpawnForHive(GameObject trainingRoot)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return;

        if (!FlamiePracGoaliePlacement.ConfigureFromTrainingHive(trainingRoot, out PlayerTeam team))
        {
            FlamieLog.Warn("[FlamiePrac] Could not resolve training net for MaxPractice AI goalie.");
            return;
        }

        MaxPracticePlugin.RegisterNullRefSuppression();
        MaxPracticePlugin.SuppressNullRefsFor(120);

        bool ok = GoalieAIManager.SpawnAIGoalie(team);
        FlamieLog.Info("[FlamiePrac] MaxPractice AI goalie spawn team=" + team + " success=" + ok);

    }

    public static void Despawn()
    {
        FlamiePracGoaliePlacement.Clear();
        GoalieAIManager.DespawnAll();
    }
}
