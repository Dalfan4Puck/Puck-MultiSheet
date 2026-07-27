using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Client-side chat hook for radio commands. Pure clients request track changes through
/// TrainingSync instead of relying only on the server chat event pipeline.
/// </summary>
[HarmonyPatch(typeof(ChatManagerController), "Event_OnChatSubmitMessage")]
public static class TrainingClientChat
{
    [HarmonyPrefix]
    private static bool Prefix(Dictionary<string, object> message)
    {
        if (message == null || !message.ContainsKey("content"))
            return true;

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient)
                return true;

            string content = (message["content"] as string ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(content) || content[0] != '/')
                return true;

            string command;
            string[] parts = content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            command = parts[0].ToLowerInvariant();

            byte radioCmd = 0;
            switch (command)
            {
                case "/nextsong":
                case "/radioskip":
                    radioCmd = RadioController.CmdNext;
                    break;
                case "/prevsong":
                case "/radioprev":
                    radioCmd = RadioController.CmdPrev;
                    break;
                default:
                    return true;
            }

            // Host/server still gets Event_Server_OnChatCommand; pure clients use the network request.
            if (nm.IsServer)
                return true;

            TrainingSync.Instance?.RequestRadioCommand(radioCmd);
            Debug.Log("[FlamiePrac] Client requested radio command: " + command);
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] TrainingClientChat failed: " + ex.Message);
            return true;
        }
    }
}
