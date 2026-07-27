using MaxPractice;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Drives GoalieAIManager phase hooks on the server (replaces MaxPractice PracticeManager tick).
/// </summary>
public class FlamiePracGoalieBootstrap : MonoBehaviour
{
    private void Update()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return;

        try
        {
            GameManager gm = NetworkBehaviourSingleton<GameManager>.Instance;
            if (gm != null)
                GoalieAIManager.NotifyPhase(gm.Phase);

            GoalieAIManager.Tick();
        }
        catch { }
    }

    // GoalieDecor unused — ignore/strip coroutine retired.
    // public void BeginIgnoreAiWithCreaseDummy(GameObject trainingRoot, PlayerTeam team) { ... }
}
