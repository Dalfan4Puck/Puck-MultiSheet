using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using MaxPractice;

/// <summary>
/// Static fields and helpers ported from MaxPracticePlugin — required by GoalieAIManager and PracticePatches.
/// </summary>
public static class MaxPracticePlugin
{
    public static Player RedTeamDummy;
    public static Player BlueTeamDummy;
    public static readonly HashSet<Player> FakePlayers = new HashSet<Player>();
    public static readonly HashSet<ulong> InfiniteStaminaPlayers = new HashSet<ulong>();
    public static readonly HashSet<ulong> YoyoPlayers = new HashSet<ulong>();
    public static readonly Dictionary<ulong, List<Puck>> HandlePucks = new Dictionary<ulong, List<Puck>>();

    private static bool nullRefHandlerRegistered;
    private static NullRefSuppressingLogHandler logHandler;

    public static void RegisterNullRefSuppression()
    {
        if (nullRefHandlerRegistered)
            return;

        try
        {
            ILogHandler defaultHandler = Debug.unityLogger.logHandler;
            logHandler = new NullRefSuppressingLogHandler(defaultHandler);
            Debug.unityLogger.logHandler = logHandler;
            nullRefHandlerRegistered = true;
        }
        catch { }
    }

    public static void SuppressNullRefsFor(int frames)
    {
        if (logHandler != null)
            logHandler.SuppressFrameCount = frames;
    }

    public static void RemoveFakeClientFromNetworkManager(ulong clientId)
    {
        if (!FakePlayerDetector.IsAnyFakeClientId(clientId))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
            return;

        try
        {
            FieldInfo connMgrField = typeof(NetworkManager).GetField(
                "ConnectionManager",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (connMgrField == null)
                return;

            object connMgr = connMgrField.GetValue(nm);
            if (connMgr == null)
                return;

            Type connMgrType = connMgr.GetType();
            bool removedAny = false;
            object entry = null;

            FieldInfo ccField = connMgrType.GetField(
                "ConnectedClients",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (ccField?.GetValue(connMgr) is System.Collections.IDictionary cc && cc.Contains(clientId))
            {
                entry = cc[clientId];
                cc.Remove(clientId);
                removedAny = true;
            }

            FieldInfo ccListField = connMgrType.GetField(
                "ConnectedClientsList",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (ccListField?.GetValue(connMgr) is System.Collections.IList ccList && entry != null)
                ccList.Remove(entry);

            FieldInfo ccIdsField = connMgrType.GetField(
                "ConnectedClientIds",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (ccIdsField?.GetValue(connMgr) is System.Collections.IList ccIds)
            {
                int idx = -1;
                for (int i = 0; i < ccIds.Count; i++)
                {
                    if (ccIds[i] is ulong id && id == clientId)
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx >= 0)
                {
                    ccIds.RemoveAt(idx);
                    removedAny = true;
                }
            }

            if (removedAny)
                ConfigManager.Dbg("[FlamiePrac] Removed fake client " + clientId + " from NetworkManager state");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] Failed to remove fake client " + clientId + ": " + ex.Message);
        }
    }

    private class NullRefSuppressingLogHandler : ILogHandler
    {
        private readonly ILogHandler defaultHandler;
        public int SuppressFrameCount;

        public NullRefSuppressingLogHandler(ILogHandler handler)
        {
            defaultHandler = handler;
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            defaultHandler.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (exception is NullReferenceException)
            {
                string stackTrace = exception.StackTrace ?? string.Empty;
                if (SuppressFrameCount > 0)
                    SuppressFrameCount--;

                if (SuppressFrameCount > 0)
                    return;

                if (stackTrace.Contains("StickPositioner") || stackTrace.Contains("PlayerInput") ||
                    stackTrace.Contains("PlayerBody") || stackTrace.Contains("Movement") ||
                    stackTrace.Contains("Stick.") || stackTrace.Contains("GoalieAI") ||
                    stackTrace.Contains("NetworkVariable") || stackTrace.Contains("ServerValue"))
                    return;
            }

            defaultHandler.LogException(exception, context);
        }
    }
}

/// <summary>No-op stub so PracticePatches puck collision hooks compile without the full Yoyo feature set.</summary>
public class YoyoManager
{
    public static YoyoManager Instance => null;

    public static void TrackLastTouchedPuckForPlayer(Puck puck, Player player) { }

    public void OnPuckFired(Puck puck, ulong steamId) { }

    public void OnPuckTouchedByOther(Puck puck, ulong steamId) { }
}
