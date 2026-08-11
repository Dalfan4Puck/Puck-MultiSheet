using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// MultiSheet and CTU both spawn a practice puck on R. Server-side window ensures one puck
    /// when both handlers receive a request for the same client within the same press.
    /// </summary>
    internal static class RSpawnPuckDebounce
    {
        internal const float WindowSeconds = 0.35f;

        private static readonly Dictionary<ulong, float> LastServerSpawnByClient = new Dictionary<ulong, float>();
        private static float _lastLocalClientSend = -1f;

        internal static void Reset()
        {
            LastServerSpawnByClient.Clear();
            _lastLocalClientSend = -1f;
        }

        /// <summary>Call on the server before spawning from any R-spawn handler.</summary>
        internal static bool TryAcceptServerSpawn(ulong clientId)
        {
            float now = Time.unscaledTime;
            if (LastServerSpawnByClient.TryGetValue(clientId, out float last)
                && now < last + WindowSeconds)
            {
                return false;
            }

            LastServerSpawnByClient[clientId] = now;
            return true;
        }

        /// <summary>Call on the client before sending a MultiSheet R-spawn network message.</summary>
        internal static bool TryAcceptLocalClientSend()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient)
                return false;

            float now = Time.unscaledTime;
            if (now < _lastLocalClientSend + WindowSeconds)
                return false;

            _lastLocalClientSend = now;
            return true;
        }

        [HarmonyPatch]
        private static class CtuRSpawnDebouncePatch
        {
            private const string CtuRSpawnTypeName = "CompetitivePuckTweaks.src.Qol.CtuRSpawnPuck";

            static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(CtuRSpawnTypeName), "OnSpawnMessage");

            static bool Prefix(ulong senderClientId)
            {
                return TryAcceptServerSpawn(senderClientId);
            }
        }
    }
}
