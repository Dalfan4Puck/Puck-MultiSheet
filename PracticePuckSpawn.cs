using MaxPractice;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Multi-rink practice drill spawns: register chunk slots and protect from face-off
    /// reset the same way R-key and mirrored pucks do (PuckSpawnSync / MultiRinkPuckSpawner).
    /// </summary>
    internal static class PracticePuckSpawn
    {
        internal static Puck SpawnAt(Vector3 worldPosition, Quaternion rotation, Vector3 velocity)
        {
            // Skip MaxPractice global puck purge — multiple practice rinks can run at once and
            // SpawnPuckWithCleanup would delete protected pucks on other sheets at the threshold.
            PuckManager puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
            if (puckManager == null)
                return null;

            Puck puck = puckManager.Server_SpawnPuck(worldPosition, rotation, false);
            if (puck == null)
                return null;

            if (puck.Rigidbody != null)
                puck.Rigidbody.linearVelocity = velocity;

            PracticeHelpers.ValidateAndRepairPuckVisuals(puck);
            RegisterSpawnedPuck(puck, worldPosition);
            return puck;
        }

        internal static void RegisterSpawnedPuck(Puck puck, Vector3 worldPosition)
        {
            if (puck == null)
                return;

            puck.transform.position = worldPosition;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return;

            CustomLevelPlugin.ProtectedPucks.Add(puck);

            if (!LargeLevelHost.IsEnabled)
                return;

            SynchronizedObject sync = puck.GetComponent<SynchronizedObject>();
            if (sync != null)
                CL_ChunkSyncServer.InitSlot(sync);
        }
    }
}
