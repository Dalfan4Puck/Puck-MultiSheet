using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Collections;


public class PuckSpawnSync : MonoBehaviour
{
    private const string SpawnPuckMessage = "CustomLevel_SpawnPuck";
    /// <summary>Meters in front of the player body along facing (flat forward from client).</summary>
    private const float SpawnForwardMeters = 2.35f;
    /// <summary>Lateral offset toward stick side: +right for righties, -right for lefties.</summary>
    private const float SpawnHandednessOffsetMeters = 0.4f;
    private Dictionary<ulong, Puck> playerPucks = new Dictionary<ulong, Puck>();

    private void Start()
    {
        if (NetworkManager.Singleton.IsServer)
        {
            NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                SpawnPuckMessage, OnSpawnPuckMessageReceived);
            PracticeLog.Info("[CustomLevel] Registered message handlers");
        }
    }

    private void OnDestroy()
    {
        NetworkManager.Singleton?.CustomMessagingManager?
            .UnregisterNamedMessageHandler(SpawnPuckMessage);
        // Clean up any pucks we were protecting in case they weren't explicitly despawned
        foreach (var puck in playerPucks.Values)
            if (puck != null) CustomLevelPlugin.ProtectedPucks.Remove(puck);
    }

    private void OnSpawnPuckMessageReceived(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out float px); reader.ReadValueSafe(out float py); reader.ReadValueSafe(out float pz);
        reader.ReadValueSafe(out float fx); reader.ReadValueSafe(out float fy); reader.ReadValueSafe(out float fz);

        Vector3 position = new Vector3(px, py, pz);
        Vector3 forward = new Vector3(fx, fy, fz);

        Player player = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(senderClientId);

        // Despawn the player's previous puck so they never hold more than one at a time.
        // Remove from ProtectedPucks first so our own despawn call isn't blocked by the guard.
        if (playerPucks.TryGetValue(senderClientId, out Puck prev) && prev != null)
        {
            CustomLevelPlugin.ProtectedPucks.Remove(prev);
            MonoBehaviourSingleton<PuckManager>.Instance.Server_DespawnPuck(prev);
            PracticeLog.Info($"[CustomLevel] Despawned previous puck for client {senderClientId}");
        }

        // Place the puck slightly in front of the player, then raycast down to find the top surface
        // (sheet/slidable layer wins over rink ice when the player is standing on a sheet).
        Vector3 spawnPos = position + forward * SpawnForwardMeters;
        spawnPos += GetHandednessOffset(player, forward);
        spawnPos.y = position.y + 5f;
        if (!TryFindSpawnSurfaceY(spawnPos, out float surfaceY))
            surfaceY = position.y + 0.1f;
        spawnPos.y = surfaceY;

        Puck newPuck = null;
        try
        {
            // Prefer the 3-param overload if it exists; fall back to the 2-param version below
            var m = MonoBehaviourSingleton<PuckManager>.Instance.GetType()
                .GetMethod("Server_SpawnPuck", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
            if (m != null)
                newPuck = m.Invoke(MonoBehaviourSingleton<PuckManager>.Instance,
                    new object[] { spawnPos, Quaternion.identity, false }) as Puck;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CustomLevel] 3-param spawn failed: {e.Message}");
        }

        if (newPuck == null)
            newPuck = MonoBehaviourSingleton<PuckManager>.Instance.Server_SpawnPuck(spawnPos, Quaternion.identity);

        if (newPuck == null)
        {
            Debug.LogWarning($"[CustomLevel] Failed to spawn puck for client {senderClientId}");
            return;
        }

        playerPucks[senderClientId] = newPuck;

        // Guard this puck from the game's out-of-bounds / face-off reset systems
        CustomLevelPlugin.ProtectedPucks.Add(newPuck);

        // Force-register the chunk slot immediately so the very first gather tick encodes
        // chunk-local instead of leaving a corrupted overflowed short in the packet
        SynchronizedObject syncObj = newPuck.GetComponent<SynchronizedObject>();
        if (syncObj != null)
            CL_ChunkSyncServer.InitSlot(syncObj);

        // Inherit the player's velocity so the puck doesn't appear stationary when spawned mid-skate
        if (player?.PlayerBody != null)
        {
            Rigidbody prb = player.PlayerBody.GetComponent<Rigidbody>();
            Rigidbody nrb = newPuck.GetComponent<Rigidbody>();
            if (prb != null && nrb != null) nrb.linearVelocity = prb.linearVelocity;
        }

        PracticeLog.Info($"[CustomLevel] Spawned puck for client {senderClientId} at {spawnPos}");
    }

    private static Vector3 GetHandednessOffset(Player player, Vector3 forward)
    {
        float sideSign = GetHandednessSideSign(player);
        if (Mathf.Approximately(sideSign, 0f))
            return Vector3.zero;

        Vector3 right;
        if (player?.PlayerBody != null)
        {
            right = player.PlayerBody.transform.right;
            right.y = 0f;
        }
        else
        {
            Vector3 flatForward = forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.0001f)
                return Vector3.zero;
            right = Vector3.Cross(Vector3.up, flatForward.normalized);
        }

        if (right.sqrMagnitude < 0.0001f)
            return Vector3.zero;

        return right.normalized * (SpawnHandednessOffsetMeters * sideSign);
    }

    private static float GetHandednessSideSign(Player player)
    {
        if (player?.Handedness == null)
            return 1f;

        switch (player.Handedness.Value)
        {
            case PlayerHandedness.Left:
                return -1f;
            case PlayerHandedness.Right:
                return 1f;
            default:
                return 1f;
        }
    }

    private static bool TryFindSpawnSurfaceY(Vector3 rayOrigin, out float surfaceY)
    {
        surfaceY = rayOrigin.y;
        int mask = BuildSpawnSurfaceMask();
        RaycastHit[] hits = Physics.RaycastAll(
            rayOrigin,
            Vector3.down,
            30f,
            mask,
            QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0)
            return false;

        float bestY = float.NegativeInfinity;
        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].collider == null || hits[i].collider.isTrigger)
                continue;
            if (hits[i].point.y > bestY)
                bestY = hits[i].point.y;
        }

        if (bestY <= float.NegativeInfinity + 1f)
            return false;

        surfaceY = bestY + 0.05f;
        return true;
    }

    private static int BuildSpawnSurfaceMask()
    {
        int mask = 0;
        int iceLayer = LayerMask.NameToLayer("Ice");
        if (iceLayer >= 0)
            mask |= 1 << iceLayer;

        int slidableLayer = CollisionHelper.GetSlidablePropLayerIndex();
        if (slidableLayer >= 0)
            mask |= 1 << slidableLayer;

        return mask != 0 ? mask : Physics.DefaultRaycastLayers;
    }

    private void Update()
    {
        if (UnityEngine.InputSystem.Keyboard.current == null) return;
        if (!UnityEngine.InputSystem.Keyboard.current.rKey.wasPressedThisFrame) return;
        if (!NetworkManager.Singleton.IsClient) return;

        // Only MultiSheet servers understand the spawn request; stay silent elsewhere.
        if (!NetworkManager.Singleton.IsServer && !PHLPracticeModPack.PracticeFlowClient.IsOnPracticeServer) return;

        // Suppress spawn while the chat input box is open. The game's chat is built with
        // Unity UI Toolkit (VisualElement) so InputField/TMP scans don't see it.
        // CustomLevelPlugin patches UIChat.StartInput/StopInput to flip this flag instead.
        if (CustomLevelPlugin.ChatInputActive) return;

        Player player = MonoBehaviourSingleton<PlayerManager>.Instance
            .GetPlayerByClientId(NetworkManager.Singleton.LocalClientId);
        if (player?.PlayerBody == null) return;

        Transform t = player.PlayerBody.transform;
        PHLPracticeModPack.StockPuckHider.NotifyLocalSpawnRequest(t.position, t.forward);
        using (var writer = new FastBufferWriter(sizeof(float) * 6, Allocator.Temp))
        {
            writer.WriteValueSafe(t.position.x); writer.WriteValueSafe(t.position.y); writer.WriteValueSafe(t.position.z);
            writer.WriteValueSafe(t.forward.x); writer.WriteValueSafe(t.forward.y); writer.WriteValueSafe(t.forward.z);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
                SpawnPuckMessage, NetworkManager.ServerClientId, writer);
        }
    }
}
