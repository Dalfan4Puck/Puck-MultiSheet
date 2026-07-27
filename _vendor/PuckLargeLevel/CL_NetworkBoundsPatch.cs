using System;
using UnityEngine;
using HarmonyLib;

public static class CL_NetworkBoundsPatch
{
    private static Harmony _harmony;
    private static bool _patched;

    // Safe to call early (before chunks activate); patches are installed once and stay in place
    public static void EnsurePatched()
    {
        if (_patched) return;
        try
        {
            _harmony = new Harmony("customlevel.networkbounds");

            // Postfix rewrites the already-encoded X/Z from world-space to chunk-local-space
            var gather = AccessTools.Method(typeof(SynchronizedObjectManager),
                "Server_GatherSynchronizedObjectData");
            if (gather != null)
            {
                _harmony.Patch(gather, null,
                    new HarmonyMethod(typeof(CL_NetworkBoundsPatch), nameof(GatherPostfix)));
                PracticeLog.Info("[CustomLevel] Patched Server_GatherSynchronizedObjectData");
            }
            else
                Debug.LogWarning("[CustomLevel] Server_GatherSynchronizedObjectData not found");

            // Client-side chunk expansion is handled in CL_ChunkSyncClient.FilterAndReplace,
            // which runs in OnClientTick/OnClientSmoothTick before vanilla applies the position.
            // Patching Client_SynchronizeObjects here would re-encode world coords back into
            // shorts, overflowing them again beyond ~50m — so we skip that patch entirely.

            _patched = true;
            PracticeLog.Info("[CustomLevel] NetworkBoundsPatch installed.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CustomLevel] NetworkBoundsPatch failed: " + e.Message);
        }
    }

    public static void EnableChunks()
    {
        CL_ChunkRegistry.ChunksActive = true;
        CL_ChunkRegistry.CurrentEncodeTickId = 0;
        CL_ChunkRegistry.CurrentDecodeTickId = 0;
        // Role split: the server encodes, pure clients decode. A host reads true world
        // positions in-process, so it needs no client-side decode patches either.
        var nm = Unity.Netcode.NetworkManager.Singleton;
        bool isServer = nm != null && nm.IsServer;
        if (isServer || nm == null) CL_ChunkSyncServer.Enable();
        if (!isServer) CL_ChunkSyncClient.Enable();
        PracticeLog.Info("[CustomLevel] Chunk sync ACTIVE (server=" + isServer + ").");
    }

    public static void Disable()
    {
        CL_ChunkSyncServer.Disable();
        CL_ChunkSyncClient.Disable();
        CL_ChunkRegistry.ChunksActive = false;
        CL_ChunkRegistry.Clear();
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _patched = false;
        PracticeLog.Info("[CustomLevel] NetworkBoundsPatch disabled.");
    }

    // Vanilla has already written (short)(worldX * precision) into __result, which overflows beyond
    // ~50m. We bypass those corrupted shorts entirely by reading the true world position from the
    // cache that GatherPrefix populated, then encoding chunk-local directly.
    public static void GatherPostfix(ref SynchronizedObjectData[] __result)
    {
        try
        {
            if (CL_ChunkRegistry.ChunksActive && __result != null)
            {
                ushort tick = CL_ChunkRegistry.CurrentEncodeTickId;
                for (int i = 0; i < __result.Length; i++)
                {
                    ushort id = __result[i].NetworkObjectId;
                    if (!CL_ChunkRegistry.TryGet(id, out ChunkSlot slot)) continue;
                    if (!CL_ChunkSyncServer.PositionCache.TryGetValue(id, out Vector3 worldPos)) continue;
                    ChunkCoord c = slot.ResolveAt(tick);
                    float ox = c.X * CL_ChunkRegistry.ChunkSize;
                    float oz = c.Z * CL_ChunkRegistry.ChunkSize;
                    // Chunk-local offset is always ≤ 16m, so (short)(localX * 655) never overflows
                    __result[i].X = (short)((worldPos.x - ox) * CL_ChunkRegistry.Precision);
                    __result[i].Z = (short)((worldPos.z - oz) * CL_ChunkRegistry.Precision);
                }
            }
        }
        finally
        {
            // Clear late-join "send every resting object" after this gather completes.
            CL_ChunkSyncServer.EndForceSendAll();
        }
    }

}
