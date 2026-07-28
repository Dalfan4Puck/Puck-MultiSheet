using System.Collections.Generic;
using Object = UnityEngine.Object;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Press R to spawn a puck in front of the player — same placement as MultiSheet PuckSpawnSync
/// (2 m ahead, raycast down to ice). Server-authoritative.
/// </summary>
public class FlamiePracTestPuckSpawn : MonoBehaviour
{
    private float lastRequestTime = -1f;
    private const float CooldownSeconds = 0.35f;

    private static readonly Dictionary<ulong, Puck> PlayerPucks = new Dictionary<ulong, Puck>();

    private void Update()
    {
        if (Application.isBatchMode)
            return;

        // Pure clients never host TrainingObjectManager — gate on plugin + sync instead.
        if (MyMod.Class1.Instance == null || TrainingSync.Instance == null)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.rKey.wasPressedThisFrame)
            return;

        if (Time.unscaledTime < lastRequestTime + CooldownSeconds)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsClient)
            return;

        Player player = MonoBehaviourSingleton<PlayerManager>.Instance?
            .GetPlayerByClientId(nm.LocalClientId);
        if (player?.PlayerBody == null)
            return;

        lastRequestTime = Time.unscaledTime;

        Transform body = player.PlayerBody.transform;
        TrainingSync.Instance?.RequestTestPuckSpawn(body.position, body.forward);
    }

    public static bool TrySpawnForClient(ulong clientId, Vector3 bodyPosition, Vector3 bodyForward)
    {
        if (!NetworkManager.Singleton.IsServer)
            return false;

        PuckManager puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
        PlayerManager playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
        if (puckManager == null || playerManager == null)
        {
            FlamieLog.Warn("[FlamiePrac] Test puck spawn failed: managers not ready.");
            return false;
        }

        Player player = playerManager.GetPlayerByClientId(clientId);
        if (player == null || !IsCharacterSpawned(player))
        {
            FlamieLog.Warn("[FlamiePrac] Test puck spawn failed: player not ready for client " + clientId);
            return false;
        }

        DespawnPreviousPuck(clientId, puckManager);

        Vector3 spawnPos = ComputeSpawnPosition(bodyPosition, bodyForward);

        Puck puck = puckManager.Server_SpawnPuck(spawnPos, Quaternion.identity, false);
        if (puck == null)
        {
            FlamieLog.Warn("[FlamiePrac] Test puck spawn failed: Server_SpawnPuck returned null.");
            return false;
        }

        PlayerPucks[clientId] = puck;

        Rigidbody puckRb = puck.Rigidbody;
        if (puckRb != null && player.PlayerBody != null)
        {
            Rigidbody bodyRb = player.PlayerBody.GetComponent<Rigidbody>();
            if (bodyRb != null)
                puckRb.linearVelocity = bodyRb.linearVelocity;
            puckRb.angularVelocity = Vector3.zero;
        }

        FlamieLog.Info("[FlamiePrac] Test puck spawned for client " + clientId + " at " + spawnPos);
        return true;
    }

    /// <summary>Same placement as MultiSheet PuckSpawnSync (2 m ahead + ice raycast).</summary>
    public static Vector3 ComputeSpawnPosition(Vector3 bodyPosition, Vector3 bodyForward)
    {
        Vector3 spawnPos = bodyPosition + bodyForward * 2f;
        spawnPos.y = bodyPosition.y + 5f;

        int iceLayer = LayerMask.NameToLayer("Ice");
        int mask = iceLayer >= 0 ? (1 << iceLayer) : Physics.DefaultRaycastLayers;

        if (Physics.Raycast(spawnPos, Vector3.down, out RaycastHit hit, 30f, mask, QueryTriggerInteraction.Ignore))
            spawnPos.y = hit.point.y + 0.05f;
        else if (Physics.Raycast(spawnPos, Vector3.down, out hit, 30f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            spawnPos.y = hit.point.y + 0.05f;
        else
            spawnPos.y = bodyPosition.y + 0.1f;

        return spawnPos;
    }

    private static void DespawnPreviousPuck(ulong clientId, PuckManager puckManager)
    {
        if (!PlayerPucks.TryGetValue(clientId, out Puck prev) || prev == null)
            return;

        PlayerPucks.Remove(clientId);
        try
        {
            puckManager.Server_DespawnPuck(prev);
        }
        catch
        {
            Object.Destroy(prev.gameObject);
        }
    }

    public static void ClearTrackedPucks()
    {
        PlayerPucks.Clear();
    }

    private static bool IsCharacterSpawned(Player player)
    {
        if (player == null)
            return false;

        try
        {
            return player.IsCharacterSpawned;
        }
        catch
        {
            return player.PlayerBody != null;
        }
    }
}
