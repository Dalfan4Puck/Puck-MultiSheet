using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server-only: polls phlstats premium members every 5 minutes, thanks random batches
    /// in chat, and announces new subscribers when the list grows.
    /// </summary>
    internal sealed class PremiumMembersAnnouncer : MonoBehaviour
    {
        private const string MembersApiUrl = "https://phlstats.com/api/premium/members";
        private const float RefreshIntervalSeconds = 300f;
        private const float StartupDelaySeconds = 60f;
        private const int NamesPerAnnouncement = 5;
        private const string IntroTextColor = "#E8E8F0";
        private const string DefaultNameColor = "#FFD700";
        private const string NewSubscriberAccentColor = "#7EE787";

        private static PremiumMembersAnnouncer instance;

        private readonly HashSet<string> knownSteamIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<PremiumMember> allMembers = new List<PremiumMember>();
        private readonly List<PremiumMember> announceQueue = new List<PremiumMember>();

        private bool hasBaseline;
        private bool fetchRunning;
        private Coroutine loopCoroutine;

        [Serializable]
        private sealed class PremiumMembersResponse
        {
            public int count;
            public PremiumMemberDto[] members;
            public bool ok;
        }

        [Serializable]
        private sealed class PremiumMemberDto
        {
            public string mainName;
            public string steamId;
            public string colorHex;
            public string color;
            public string nameColor;
            public string chatColor;
            public string hexColor;
        }

        private struct PremiumMember
        {
            internal string MainName;
            internal string SteamId;
            internal string Color;
        }

        internal static void Install(GameObject host)
        {
            if (host == null)
                return;

            if (instance != null)
                return;

            instance = host.AddComponent<PremiumMembersAnnouncer>();
        }

        internal static void Teardown()
        {
            if (instance == null)
                return;

            if (instance.loopCoroutine != null)
                instance.StopCoroutine(instance.loopCoroutine);

            Destroy(instance);
            instance = null;
        }

        private void OnEnable()
        {
            if (loopCoroutine != null)
                StopCoroutine(loopCoroutine);

            loopCoroutine = StartCoroutine(RefreshLoop());
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(instance, this))
                instance = null;
        }

        private IEnumerator RefreshLoop()
        {
            yield return new WaitForSecondsRealtime(StartupDelaySeconds);

            while (enabled)
            {
                if (ShouldRunOnServer())
                    yield return RefreshOnce();

                yield return new WaitForSecondsRealtime(RefreshIntervalSeconds);
            }
        }

        private static bool ShouldRunOnServer()
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsServer;
            }
            catch
            {
                return false;
            }
        }

        private IEnumerator RefreshOnce()
        {
            if (fetchRunning)
                yield break;

            fetchRunning = true;
            string json = null;
            string error = null;

            using (UnityWebRequest request = UnityWebRequest.Get(MembersApiUrl))
            {
                RadioApiUtil.ConfigureRequest(request);
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success
                    && !string.IsNullOrWhiteSpace(request.downloadHandler?.text))
                {
                    json = request.downloadHandler.text;
                }
                else
                {
                    error = request.error;
                }
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                if (RadioApiUtil.TryDownloadString(MembersApiUrl, out string fallbackBody, out string fallbackError))
                    json = fallbackBody;
                else
                    error = string.IsNullOrEmpty(error) ? fallbackError : error + " | " + fallbackError;
            }

            fetchRunning = false;

            if (string.IsNullOrWhiteSpace(json))
            {
                PracticeLog.Info("[PHLPractice] Premium members fetch failed: " + (error ?? "empty response"));
                yield break;
            }

            if (!TryParseMembers(json, out List<PremiumMember> members))
            {
                PracticeLog.Info("[PHLPractice] Premium members parse failed.");
                yield break;
            }

            ProcessMemberSnapshot(members);
        }

        private void ProcessMemberSnapshot(List<PremiumMember> members)
        {
            if (members == null || members.Count == 0)
                return;

            var freshMembers = new List<PremiumMember>();
            if (hasBaseline)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    PremiumMember member = members[i];
                    if (string.IsNullOrEmpty(member.SteamId))
                        continue;

                    if (!knownSteamIds.Contains(member.SteamId))
                        freshMembers.Add(member);
                }
            }

            knownSteamIds.Clear();
            for (int i = 0; i < members.Count; i++)
            {
                if (!string.IsNullOrEmpty(members[i].SteamId))
                    knownSteamIds.Add(members[i].SteamId);
            }

            SyncMemberRoster(members);

            if (!hasBaseline)
            {
                hasBaseline = true;
                PracticeLog.Info("[PHLPractice] Premium members baseline loaded (" + allMembers.Count + ").");
            }
            else
            {
                for (int i = 0; i < freshMembers.Count; i++)
                    BroadcastChat(BuildNewSubscriberMessage(freshMembers[i]));
            }

            EnsureAnnounceQueueReady();

            List<PremiumMember> batch = TakeNextBatch(NamesPerAnnouncement);
            if (batch.Count > 0)
                BroadcastChat(BuildThankYouMessage(batch));
        }

        private void SyncMemberRoster(List<PremiumMember> members)
        {
            allMembers.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < members.Count; i++)
            {
                PremiumMember member = members[i];
                if (string.IsNullOrWhiteSpace(member.MainName))
                    continue;

                string key = MemberKey(member);
                if (!seen.Add(key))
                    continue;

                allMembers.Add(member);
            }

            var liveKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < allMembers.Count; i++)
                liveKeys.Add(MemberKey(allMembers[i]));

            for (int i = announceQueue.Count - 1; i >= 0; i--)
            {
                if (!liveKeys.Contains(MemberKey(announceQueue[i])))
                    announceQueue.RemoveAt(i);
            }
        }

        private static string MemberKey(PremiumMember member)
        {
            if (!string.IsNullOrEmpty(member.SteamId))
                return member.SteamId;
            return member.MainName ?? string.Empty;
        }

        private void EnsureAnnounceQueueReady()
        {
            if (announceQueue.Count > 0 || allMembers.Count == 0)
                return;

            announceQueue.AddRange(allMembers);
            Shuffle(announceQueue);
        }

        private List<PremiumMember> TakeNextBatch(int count)
        {
            var batch = new List<PremiumMember>();
            if (count <= 0 || allMembers.Count == 0)
                return batch;

            if (announceQueue.Count == 0)
            {
                announceQueue.AddRange(allMembers);
                Shuffle(announceQueue);
            }

            int take = Mathf.Min(count, announceQueue.Count);
            for (int i = 0; i < take; i++)
            {
                batch.Add(announceQueue[0]);
                announceQueue.RemoveAt(0);
            }

            return batch;
        }

        private static bool TryParseMembers(string json, out List<PremiumMember> members)
        {
            members = new List<PremiumMember>();
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                PremiumMembersResponse parsed = JsonUtility.FromJson<PremiumMembersResponse>(json);
                if (parsed?.members != null && parsed.members.Length > 0)
                {
                    for (int i = 0; i < parsed.members.Length; i++)
                    {
                        PremiumMemberDto dto = parsed.members[i];
                        if (dto == null || string.IsNullOrWhiteSpace(dto.mainName))
                            continue;

                        members.Add(new PremiumMember
                        {
                            MainName = dto.mainName.Trim(),
                            SteamId = dto.steamId ?? string.Empty,
                            Color = ResolveMemberColor(dto)
                        });
                    }

                    if (members.Count > 0)
                        return true;
                }
            }
            catch (Exception ex)
            {
                PracticeLog.Info("[PHLPractice] Premium members JsonUtility parse: " + ex.Message);
            }

            return TryParseMembersRegex(json, members);
        }

        private static bool TryParseMembersRegex(string json, List<PremiumMember> members)
        {
            if (members == null)
                return false;

            var nameMatches = Regex.Matches(
                json,
                "\\\"mainName\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"",
                RegexOptions.CultureInvariant);
            var idMatches = Regex.Matches(
                json,
                "\\\"steamId\\\"\\s*:\\s*\\\"([^\\\"\\\\]+)\\\"",
                RegexOptions.CultureInvariant);
            var colorMatches = Regex.Matches(
                json,
                "\\\"colorHex\\\"\\s*:\\s*\\\"([^\\\"\\\\]+)\\\"",
                RegexOptions.CultureInvariant);

            int count = Mathf.Min(nameMatches.Count, idMatches.Count);
            for (int i = 0; i < count; i++)
            {
                string name = UnescapeJson(nameMatches[i].Groups[1].Value).Trim();
                if (string.IsNullOrEmpty(name))
                    continue;

                string color = DefaultNameColor;
                if (i < colorMatches.Count)
                    color = NormalizeColor(colorMatches[i].Groups[1].Value, DefaultNameColor);

                members.Add(new PremiumMember
                {
                    MainName = name,
                    SteamId = idMatches[i].Groups[1].Value.Trim(),
                    Color = color
                });
            }

            return members.Count > 0;
        }

        private static string ResolveMemberColor(PremiumMemberDto dto)
        {
            if (dto == null)
                return DefaultNameColor;

            if (!string.IsNullOrWhiteSpace(dto.colorHex))
                return NormalizeColor(dto.colorHex, DefaultNameColor);
            if (!string.IsNullOrWhiteSpace(dto.hexColor))
                return NormalizeColor(dto.hexColor, DefaultNameColor);
            if (!string.IsNullOrWhiteSpace(dto.color))
                return NormalizeColor(dto.color, DefaultNameColor);
            if (!string.IsNullOrWhiteSpace(dto.nameColor))
                return NormalizeColor(dto.nameColor, DefaultNameColor);
            if (!string.IsNullOrWhiteSpace(dto.chatColor))
                return NormalizeColor(dto.chatColor, DefaultNameColor);

            return DefaultNameColor;
        }

        private static string NormalizeColor(string raw, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            raw = raw.Trim();
            if (raw.StartsWith("#", StringComparison.Ordinal))
            {
                if (raw.Length == 4)
                {
                    return "#"
                        + raw[1] + raw[1]
                        + raw[2] + raw[2]
                        + raw[3] + raw[3];
                }

                if (raw.Length == 7 || raw.Length == 9)
                    return raw.ToUpperInvariant();
            }
            else if (raw.Length == 6 || raw.Length == 8)
            {
                return "#" + raw.ToUpperInvariant();
            }

            return fallback;
        }

        private static string BuildThankYouMessage(List<PremiumMember> batch)
        {
            var sb = new StringBuilder();
            sb.Append("<color=").Append(IntroTextColor).Append(">");
            sb.Append("Thank you to some of our PHL Premium members who made this rink possible:");
            sb.Append("</color>");
            sb.Append('\n');

            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0)
                    sb.Append("<color=").Append(IntroTextColor).Append(">, </color>");

                sb.Append(FormatColoredName(batch[i]));
            }

            return WrapChatSize(sb.ToString());
        }

        private static string BuildNewSubscriberMessage(PremiumMember member)
        {
            var sb = new StringBuilder();
            sb.Append(FormatColoredName(member));
            sb.Append("<color=").Append(NewSubscriberAccentColor).Append(">");
            sb.Append(" just subscribed to PHL Premium!");
            sb.Append("</color>");
            return WrapChatSize(sb.ToString());
        }

        private static string FormatColoredName(PremiumMember member)
        {
            string color = NormalizeColor(member.Color, DefaultNameColor);
            string name = EscapeRichText(member.MainName);
            return "<color=" + color + ">" + name + "</color>";
        }

        private static string WrapChatSize(string richText)
        {
            return "<size=70%>" + richText + "</size>";
        }

        private static string EscapeRichText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("<", string.Empty)
                .Replace(">", string.Empty);
        }

        private static string UnescapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\\", "\\");
        }

        private static void Shuffle(List<PremiumMember> list)
        {
            if (list == null || list.Count <= 1)
                return;

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                PremiumMember temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }

        private static void BroadcastChat(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer)
                    return;

                ChatManager chat = NetworkBehaviourSingleton<ChatManager>.Instance;
                if (chat == null)
                    chat = UnityEngine.Object.FindFirstObjectByType<ChatManager>();

                chat?.Server_BroadcastChatMessage(message);
            }
            catch (Exception ex)
            {
                PracticeLog.Info("[PHLPractice] Premium chat broadcast failed: " + ex.Message);
            }
        }
    }
}
