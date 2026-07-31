using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Deferred chat replies — same pattern as AIGoalies ChatOutbound.
    /// </summary>
    internal static class ChatOutbound
    {
        private static readonly Queue<Action> Pending = new Queue<Action>();

        internal static void Enqueue(Action action)
        {
            if (action == null) return;
            Pending.Enqueue(action);
        }

        internal static void Tick()
        {
            while (Pending.Count > 0)
            {
                Action action = Pending.Dequeue();
                try { action(); }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Deferred chat send failed: " + ex.Message);
                }
            }
        }

        internal static void Clear() => Pending.Clear();
    }

    [HarmonyPatch(typeof(ChatManager), "Client_SendChatMessageRpc")]
    internal static class RinkCommands
    {
        [HarmonyPrefix]
        private static bool Prefix(
            ChatManager __instance,
            string content,
            bool isQuickChat,
            bool isTeamChat,
            RpcParams rpcParams)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || isQuickChat || string.IsNullOrWhiteSpace(content))
                    return true;

                string trimmed = content.Trim();
                string command = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0]
                    .ToLowerInvariant();
                ulong clientId = rpcParams.Receive.SenderClientId;

                if (command == "/rinks" || command == "/rink")
                {
                    QueuePrivate(clientId, MultiRinkService.BuildHelpText());
                    return false;
                }

                if (command == "/sp" || command == "/stretchpass")
                {
                    string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string styleArg = parts.Length > 1 ? parts[1] : "normal";
                    if (StretchPassPractice.TryHandleChat(clientId, styleArg, out string spReply)
                        && !string.IsNullOrEmpty(spReply))
                    {
                        QueuePrivate(clientId, spReply);
                    }
                    return false;
                }

                if (EmptyPucksCommand.TryHandle(command, clientId, out string epMessage))
                {
                    if (!string.IsNullOrEmpty(epMessage))
                        QueueBroadcast(epMessage);
                    return false;
                }

                // Matches never start here — swallow the start/warmup vote commands
                // before the game mode can create the votes.
                if (PracticeFlow.ServerActive &&
                    (command == "/vs" || command == "/votestart" ||
                     command == "/vw" || command == "/votewarmup"))
                {
                    QueuePrivate(clientId, "This is a practice server — matches never start, so there is nothing to vote on.");
                    return false;
                }

                if (ArenaRenderCatalog.TryHandleDumpCommand(command, true, out string dumpReply))
                {
                    QueuePrivate(clientId, dumpReply);
                    return false;
                }

                if (string.Equals(command, "/multirink-diag", StringComparison.OrdinalIgnoreCase))
                {
                    string path = CloneDiagnostics.WriteCloneHealthReport("manual");
                    QueuePrivate(clientId, "[PHLPractice] Wrote clone health report to " + path);
                    return false;
                }

                RinkSlot slot = MultiRinkConfig.Current.FindByCommand(command);
                if (slot == null) return true;

                if (MultiRinkService.TryAssignRink(clientId, slot, out string message))
                {
                    if (!string.IsNullOrEmpty(message))
                        QueuePrivate(clientId, message);
                }
                else
                    QueuePrivate(clientId, message ?? "Could not switch rink.");

                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Rink command error: " + ex.Message);
                return true;
            }
        }

        private static void QueuePrivate(ulong clientId, string message)
        {
            string payload = $"<size=70%><color=#FFFFFF>{message}</color></size>";
            ulong id = clientId;
            ChatOutbound.Enqueue(() =>
            {
                try
                {
                    ChatManager chat = NetworkBehaviourSingleton<ChatManager>.Instance
                        ?? UnityEngine.Object.FindFirstObjectByType<ChatManager>();
                    chat?.Server_SendChatMessage(payload, null, new[] { id });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Private chat failed: " + ex.Message);
                }
            });
        }

        private static void QueueBroadcast(string message)
        {
            string payload = $"<size=70%><color=#FFFFFF>{message}</color></size>";
            ChatOutbound.Enqueue(() =>
            {
                try
                {
                    ChatManager chat = NetworkBehaviourSingleton<ChatManager>.Instance
                        ?? UnityEngine.Object.FindFirstObjectByType<ChatManager>();
                    chat?.Server_BroadcastChatMessage(payload);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Broadcast chat failed: " + ex.Message);
                }
            });
        }
    }
}
