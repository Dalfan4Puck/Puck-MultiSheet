using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server-side: mirrors the practice server's default rink-1 pucks onto each cloned
    /// rink. Waits for the game to spawn its own pucks (count stable across two polls),
    /// then spawns copies at the same relative positions offset to each rink. Mirrored
    /// pucks are added to ProtectedPucks so the game's out-of-bounds / face-off reset
    /// logic never yanks them back to rink 1.
    /// </summary>
    internal sealed class MultiRinkPuckSpawner : MonoBehaviour
    {
        private const float PollSeconds = 2f;
        private const float MaxWaitSeconds = 90f;
        /// <summary>Pucks left on each sheet. The game lays out more than practice needs;
        /// the extras furthest from center ice are despawned before mirroring.</summary>
        private const int PucksPerRink = 5;

        private static MultiRinkPuckSpawner instance;

        private List<RinkSlot> rinks;
        private Vector3 primaryOrigin;

        internal static void Begin(List<RinkSlot> rinks)
        {
            if (rinks == null || rinks.Count == 0) return;

            if (instance != null) Destroy(instance.gameObject);

            var go = new GameObject("PHL_MultiRinkPuckSpawner");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<MultiRinkPuckSpawner>();
            instance.rinks = rinks;
            instance.primaryOrigin = rinks[0]?.Origin ?? Vector3.zero;
            instance.StartCoroutine(instance.MirrorWhenReady());
        }

        internal static void Stop()
        {
            if (instance == null)
                return;

            Destroy(instance.gameObject);
            instance = null;
        }

        private IEnumerator MirrorWhenReady()
        {
            float waited = 0f;
            int lastCount = -1;

            while (waited < MaxWaitSeconds)
            {
                yield return new WaitForSeconds(PollSeconds);
                waited += PollSeconds;

                if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                    continue;

                List<Puck> rink1Pucks = FindRink1Pucks();

                // Mirror once the game's initial spawn has settled (same non-zero
                // count on two consecutive polls).
                if (rink1Pucks.Count > 0 && rink1Pucks.Count == lastCount)
                {
                    Mirror(TrimToInnermost(rink1Pucks));
                    Destroy(gameObject);
                    yield break;
                }

                lastCount = rink1Pucks.Count;
            }

            PracticeLog.Info("[PHLPractice] Puck mirror: no rink-1 pucks appeared within " +
                      MaxWaitSeconds + "s; skipping.");
            Destroy(gameObject);
        }

        private List<Puck> FindRink1Pucks()
        {
            var result = new List<Puck>();
            foreach (Puck puck in FindObjectsByType<Puck>(FindObjectsSortMode.None))
            {
                if (puck == null) continue;
                // Skip pucks we own (player R-key pucks, prior mirrors).
                if (CustomLevelPlugin.ProtectedPucks.Contains(puck)) continue;

                Vector3 p = puck.transform.position - primaryOrigin;
                if (Mathf.Abs(p.x) > 30f || Mathf.Abs(p.z) > 55f) continue;
                result.Add(puck);
            }
            return result;
        }

        /// <summary>
        /// Keeps the pucks nearest center ice and despawns the rest on rink 1, so every
        /// sheet ends up with the same trimmed set.
        /// </summary>
        private List<Puck> TrimToInnermost(List<Puck> pucks)
        {
            if (pucks.Count <= PucksPerRink) return pucks;

            pucks.Sort((a, b) => CenterDistanceSqr(a).CompareTo(CenterDistanceSqr(b)));
            List<Puck> keep = pucks.GetRange(0, PucksPerRink);

            PuckManager manager = MonoBehaviourSingleton<PuckManager>.Instance;
            for (int i = PucksPerRink; i < pucks.Count; i++)
            {
                Puck extra = pucks[i];
                if (extra == null || manager == null) continue;
                try { manager.Server_DespawnPuck(extra); }
                catch (Exception e)
                {
                    Debug.LogWarning("[PHLPractice] Puck trim: despawn failed: " + e.Message);
                }
            }

            PracticeLog.Info("[PHLPractice] Puck trim: rink1 " + pucks.Count + " → " + keep.Count +
                      " (outermost despawned).");
            return keep;
        }

        private float CenterDistanceSqr(Puck puck)
        {
            if (puck == null) return float.MaxValue;
            Vector3 p = puck.transform.position - primaryOrigin;
            p.y = 0f;
            return p.sqrMagnitude;
        }

        private void Mirror(List<Puck> rink1Pucks)
        {
            PuckManager manager = MonoBehaviourSingleton<PuckManager>.Instance;
            if (manager == null)
            {
                Debug.LogWarning("[PHLPractice] Puck mirror: PuckManager not found.");
                return;
            }

            int spawned = 0;
            for (int i = 1; i < rinks.Count; i++)
            {
                RinkSlot slot = rinks[i];
                if (slot == null) continue;

                Vector3 offset = slot.Origin - primaryOrigin;
                if (offset.sqrMagnitude < 0.01f) continue;

                for (int p = 0; p < rink1Pucks.Count; p++)
                {
                    Puck src = rink1Pucks[p];
                    if (src == null) continue;

                    Puck clone = SpawnPuck(manager, src.transform.position + offset, src.transform.rotation);
                    if (clone == null) continue;

                    CustomLevelPlugin.ProtectedPucks.Add(clone);

                    // Register the chunk slot immediately so the first gather tick
                    // encodes chunk-local instead of overflowing the position shorts.
                    SynchronizedObject sync = clone.GetComponent<SynchronizedObject>();
                    if (sync != null) CL_ChunkSyncServer.InitSlot(sync);

                    spawned++;
                }
            }

            PracticeLog.Info("[PHLPractice] Puck mirror: rink1 pucks=" + rink1Pucks.Count +
                      " → spawned " + spawned + " clone puck(s) across " + (rinks.Count - 1) + " rink(s).");
        }

        private static Puck SpawnPuck(PuckManager manager, Vector3 position, Quaternion rotation)
        {
            // Same overload preference as vendor PuckSpawnSync: 3-param when available.
            try
            {
                var m = manager.GetType().GetMethod(
                    "Server_SpawnPuck", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
                if (m != null)
                    return m.Invoke(manager, new object[] { position, rotation, false }) as Puck;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PHLPractice] Puck mirror: 3-param spawn failed: " + e.Message);
            }

            return manager.Server_SpawnPuck(position, rotation);
        }
    }
}
