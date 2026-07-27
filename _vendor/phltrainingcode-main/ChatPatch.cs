using HarmonyLib;

[HarmonyPatch(typeof(UIChat), "Server_ProcessPlayerChatMessage")]
public static class ChatPatch
{

}