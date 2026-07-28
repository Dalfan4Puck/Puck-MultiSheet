using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server-wide votes to toggle PHL training tools per rink (VoteManager majority rules).
    /// Defaults: rink 1 = PHL Tools, rinks 2–6 = Empty.
    /// </summary>
    internal static class RinkStripVote
    {
        internal const string VoteName = "rink-strip";
        private const string Channel = "multisheet-stripvote-v1";
        private const string ProgressChannel = "multisheet-stripvote-progress-v1";
        private const float VoteTimeoutSeconds = 60f;
        private const float VoteStartCooldownSeconds = 5f;
        private const int MaxKeyBytes = 64;

        private static NetworkManager registeredManager;
        private static CustomMessagingManager registeredMessaging;
        private static bool listeningVotes;
        private static float voteOpenedAt = -1f;
        private static float lastVoteStartedAt = -999f;
        private static bool suppressExpireAnnounce;
        private static bool defaultsApplied;
        private static float nextDefaultRetry;

        private static readonly List<RinkStripMode> serverModes = new List<RinkStripMode>();

        internal static RinkStripVoteProgress CurrentProgress = RinkStripVoteProgress.None;

        internal static void Initialize()
        {
            EnsureVoteListener();
            Tick();
        }

        internal static void Tick()
        {
            EnsureVoteListener();
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != registeredManager)
            {
                DetachNetwork();
                if (manager != null) AttachNetwork(manager);
            }
            if (manager != null) TryRegisterMessaging(manager);
            EnforceVoteTimeout();
            TryApplyServerDefaults();
        }

        internal static void Teardown()
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                {
                    VoteManager votes = MonoBehaviourSingleton<VoteManager>.Instance;
                    if (votes != null) CancelOpenVote(votes, announce: false);
                }
            }
            catch { }

            DetachNetwork();
            if (listeningVotes)
            {
                try { EventManager.RemoveEventListener("Event_Server_OnVoteRemoved", OnVoteRemoved); } catch { }
                try { EventManager.RemoveEventListener("Event_Server_OnVoteAdded", OnVoteAdded); } catch { }
                try { EventManager.RemoveEventListener("Event_Server_OnVoteProgressed", OnVoteProgressed); } catch { }
                listeningVotes = false;
            }

            CurrentProgress = RinkStripVoteProgress.None;
            voteOpenedAt = -1f;
            lastVoteStartedAt = -999f;
            suppressExpireAnnounce = false;
            defaultsApplied = false;
            nextDefaultRetry = 0f;
            serverModes.Clear();
        }

        internal static RinkStripMode GetServerMode(int rinkIndex)
        {
            if (rinkIndex < 0 || rinkIndex >= serverModes.Count)
                return rinkIndex == 0 ? RinkStripMode.PhlTools : RinkStripMode.Empty;
            return serverModes[rinkIndex];
        }

        internal static void CopyModesTo(List<RinkStripMode> into)
        {
            if (into == null) return;
            into.Clear();
            for (int i = 0; i < serverModes.Count; i++)
                into.Add(serverModes[i]);
        }

        /// <summary>Client: start or cast a strip vote for the given rink + mode.</summary>
        internal static void ClientRequestVote(int rinkIndex, RinkStripMode mode)
        {
            try
            {
                NetworkManager manager = NetworkManager.Singleton;
                if (manager == null || !manager.IsConnectedClient) return;
                CustomMessagingManager messaging = manager.CustomMessagingManager;
                if (messaging == null) return;

                string key = RinkStripModeUtil.ToVoteKey(rinkIndex, mode);
                byte[] bytes = Encoding.UTF8.GetBytes(key);
                using (FastBufferWriter writer = new FastBufferWriter(4 + bytes.Length, Allocator.Temp))
                {
                    writer.WriteValueSafe((uint)bytes.Length);
                    writer.WriteBytesSafe(bytes);
                    messaging.SendNamedMessage(
                        Channel,
                        NetworkManager.ServerClientId,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Strip vote request failed: " + ex.Message);
            }
        }

        internal static void ServerHandleVoteRequest(Player player, int rinkIndex, RinkStripMode mode)
        {
            if (player == null) return;
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (FakePlayerDetector.IsAnyFakePlayer(player)) return;

            EnsureModeListSize(rinkIndex + 1);
            if (GetServerMode(rinkIndex) == mode)
            {
                QueuePrivate(player.OwnerClientId,
                    "Rink " + (rinkIndex + 1) + " already uses " + RinkStripModeUtil.DisplayName(mode) + ".");
                return;
            }

            VoteManager votes = MonoBehaviourSingleton<VoteManager>.Instance;
            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (votes == null || pm == null)
            {
                QueuePrivate(player.OwnerClientId, "Voting is unavailable right now.");
                return;
            }

            string steamId = player.SteamId.Value.ToString();
            if (string.IsNullOrEmpty(steamId))
            {
                QueuePrivate(player.OwnerClientId, "Missing Steam ID; cannot vote.");
                return;
            }

            string voteKey = RinkStripModeUtil.ToVoteKey(rinkIndex, mode);
            Vote existing = votes.Server_GetVoteByName(VoteName);
            if (existing != null)
            {
                string existingKey = existing.Data as string;
                if (string.Equals(existingKey, voteKey, StringComparison.OrdinalIgnoreCase))
                {
                    int before = existing.InFavourVotes;
                    existing.CastVote(steamId, inFavour: true);
                    if (existing.InFavourVotes == before)
                    {
                        QueuePrivate(
                            player.OwnerClientId,
                            "You already voted for " + RinkStripModeUtil.DisplayName(mode) +
                            " on Rink " + (rinkIndex + 1) + ". " + FormatSecondsLeft() +
                            " left (or pick another option to replace).");
                    }
                    if (!existing.Passed && votes.Server_GetVoteByName(VoteName) == existing)
                        PublishProgressFromVote(existing);
                    return;
                }

                if (!TryConsumeStartCooldown(player.OwnerClientId))
                    return;

                CancelOpenVote(votes, announce: false);
            }
            else if (!TryConsumeStartCooldown(player.OwnerClientId))
            {
                return;
            }

            StartNewVote(votes, pm, player, steamId, rinkIndex, mode, voteKey);
        }

        private static void TryApplyServerDefaults()
        {
            if (defaultsApplied) return;
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (Time.unscaledTime < nextDefaultRetry) return;
            nextDefaultRetry = Time.unscaledTime + 1f;

            if (TrainingObjectManager.Instance == null) return;
            if (!MultiRinkConfig.Current.EnableMultiRink) return;

            int count = MultiRinkConfig.Current.Rinks?.Count ?? 0;
            if (count <= 0) count = 6;

            EnsureModeListSize(count);
            for (int i = 0; i < count; i++)
            {
                RinkStripMode mode = i == 0 ? RinkStripMode.PhlTools : RinkStripMode.Empty;
                serverModes[i] = mode;
                ApplyStripMode(i, mode, announce: false);
            }

            defaultsApplied = true;
            PracticeLog.Info("[PHLPractice] Strip defaults applied (Rink 1 = PHL Tools, others Empty).");
            RinkMotdService.BroadcastStatus();
        }

        private static void ApplyStripMode(int rinkIndex, RinkStripMode mode, bool announce)
        {
            EnsureModeListSize(rinkIndex + 1);
            serverModes[rinkIndex] = mode;

            TrainingObjectManager manager = TrainingObjectManager.Instance;
            if (manager == null) return;

            bool enable = mode == RinkStripMode.PhlTools;
            manager.SetRinkToolsEnabled(rinkIndex, enable);

            if (announce)
            {
                BroadcastChat(
                    "Rink " + (rinkIndex + 1) + " tools changed to " + RinkStripModeUtil.DisplayName(mode) + ".",
                    "#e67e22");
            }
        }

        private static void EnsureModeListSize(int count)
        {
            while (serverModes.Count < count)
                serverModes.Add(RinkStripMode.Empty);
        }

        private static bool TryConsumeStartCooldown(ulong clientId)
        {
            float elapsed = Time.realtimeSinceStartup - lastVoteStartedAt;
            if (elapsed < VoteStartCooldownSeconds)
            {
                int left = Mathf.CeilToInt(VoteStartCooldownSeconds - elapsed);
                if (left < 1) left = 1;
                QueuePrivate(clientId, "Wait " + left + "s before starting another tools vote.");
                return false;
            }
            return true;
        }

        private static void StartNewVote(
            VoteManager votes,
            PlayerManager pm,
            Player player,
            string steamId,
            int rinkIndex,
            RinkStripMode mode,
            string voteKey)
        {
            PlayerTeam[] teams = { PlayerTeam.Blue, PlayerTeam.Red };
            int eligible = CountHumanVoters(pm);
            if (eligible < 1) eligible = 1;

            int required = Utils.GetVoteMajority(eligible);
            string title = "Rink " + (rinkIndex + 1) + ": " + RinkStripModeUtil.DisplayName(mode);
            votes.Server_AddVote(
                VoteName,
                title,
                "Rinks tab or /v · 60s",
                teams,
                VoteTimeoutSeconds,
                steamId,
                required,
                voteKey);
            voteOpenedAt = Time.realtimeSinceStartup;
            lastVoteStartedAt = voteOpenedAt;

            Vote created = votes.Server_GetVoteByName(VoteName);
            if (created != null) PublishProgressFromVote(created);

            if (required <= 1) return;
            int yes = created != null ? created.InFavourVotes : 1;
            BroadcastChat(
                FormatVoterLabel(player) + " voted for " + RinkStripModeUtil.DisplayName(mode) +
                " on Rink " + (rinkIndex + 1) + ". Open Rinks tab or type /v (" + yes + "/" + required + ").",
                "#e67e22");
        }

        private static string FormatVoterLabel(Player player)
        {
            string name = "Someone";
            try
            {
                string n = player.Username.Value.ToString();
                if (!string.IsNullOrWhiteSpace(n)) name = n.Trim();
            }
            catch { }

            try
            {
                int number = player.Number.Value;
                if (number > 0) return "#" + number + " " + name;
            }
            catch { }
            return name;
        }

        private static string FormatSecondsLeft()
        {
            if (voteOpenedAt < 0f) return "under 60s";
            float left = VoteTimeoutSeconds - (Time.realtimeSinceStartup - voteOpenedAt);
            if (left < 1f) left = 1f;
            return Mathf.CeilToInt(left) + "s";
        }

        private static void CancelOpenVote(VoteManager votes, bool announce)
        {
            if (votes == null) return;
            Vote existing = votes.Server_GetVoteByName(VoteName);
            if (existing == null)
            {
                voteOpenedAt = -1f;
                return;
            }

            suppressExpireAnnounce = !announce;
            try { votes.Server_RemoveVote(existing); }
            finally
            {
                suppressExpireAnnounce = false;
                voteOpenedAt = -1f;
            }
        }

        private static void EnforceVoteTimeout()
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer) return;

                VoteManager votes = MonoBehaviourSingleton<VoteManager>.Instance;
                if (votes == null) return;

                Vote existing = votes.Server_GetVoteByName(VoteName);
                if (existing == null)
                {
                    voteOpenedAt = -1f;
                    return;
                }

                if (voteOpenedAt < 0f)
                {
                    CancelOpenVote(votes, announce: true);
                    return;
                }

                if (Time.realtimeSinceStartup - voteOpenedAt < VoteTimeoutSeconds)
                    return;

                CancelOpenVote(votes, announce: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Strip vote timeout failed: " + ex.Message);
            }
        }

        private static int CountHumanVoters(PlayerManager pm)
        {
            int count = 0;
            try
            {
                foreach (Player p in pm.GetPlayers())
                {
                    if (p == null) continue;
                    if (FakePlayerDetector.IsAnyFakePlayer(p)) continue;
                    try
                    {
                        if (FakePlayerDetector.IsAnyFakeClientId(p.OwnerClientId)) continue;
                    }
                    catch { }
                    try { if (p.IsReplay != null && p.IsReplay.Value) continue; } catch { }
                    count++;
                }
            }
            catch { }
            return count;
        }

        private static void EnsureVoteListener()
        {
            if (listeningVotes) return;
            try
            {
                EventManager.AddEventListener("Event_Server_OnVoteRemoved", OnVoteRemoved);
                EventManager.AddEventListener("Event_Server_OnVoteAdded", OnVoteAdded);
                EventManager.AddEventListener("Event_Server_OnVoteProgressed", OnVoteProgressed);
                listeningVotes = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Could not listen for strip votes: " + ex.Message);
            }
        }

        private static void OnVoteAdded(Dictionary<string, object> message)
        {
            try
            {
                if (message == null || !message.TryGetValue("vote", out object raw) || !(raw is Vote vote))
                    return;
                if (vote.Name != VoteName) return;
                PublishProgressFromVote(vote);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Strip vote add sync failed: " + ex.Message);
            }
        }

        private static void OnVoteProgressed(Dictionary<string, object> message)
        {
            try
            {
                if (message == null || !message.TryGetValue("vote", out object raw) || !(raw is Vote vote))
                    return;
                if (vote.Name != VoteName) return;
                PublishProgressFromVote(vote);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Strip vote progress sync failed: " + ex.Message);
            }
        }

        private static void PublishProgressFromVote(Vote vote)
        {
            if (vote == null) return;
            if (!RinkStripModeUtil.TryParseVoteKey(vote.Data as string, out int rinkIndex, out RinkStripMode mode))
                return;

            RinkStripVoteProgress progress = new RinkStripVoteProgress
            {
                Active = true,
                RinkIndex = rinkIndex,
                Mode = mode,
                InFavour = vote.InFavourVotes,
                Required = vote.RequiredVotes
            };
            ApplyAndBroadcastProgress(progress);
        }

        private static void ClearProgress()
        {
            ApplyAndBroadcastProgress(RinkStripVoteProgress.None);
        }

        private static void ApplyAndBroadcastProgress(RinkStripVoteProgress progress)
        {
            CurrentProgress = progress;
            RinkMotdUI.ApplyStripVoteProgress(progress);
            RinkScoreboardTab.ApplyStripVoteProgress(progress);

            NetworkManager manager = NetworkManager.Singleton;
            CustomMessagingManager messaging = manager?.CustomMessagingManager;
            if (manager == null || !manager.IsServer || messaging == null) return;
            if (manager.ConnectedClientsIds == null) return;

            string key = progress.Active
                ? RinkStripModeUtil.ToVoteKey(progress.RinkIndex, progress.Mode)
                : "";
            byte[] keyBytes = Encoding.UTF8.GetBytes(key ?? "");
            if (keyBytes.Length > MaxKeyBytes) return;

            foreach (ulong clientId in manager.ConnectedClientsIds)
            {
                if (FakePlayerDetector.IsAnyFakeClientId(clientId)) continue;
                if (clientId == manager.LocalClientId) continue;
                try
                {
                    using (FastBufferWriter writer = new FastBufferWriter(
                        1 + 4 + 4 + 4 + keyBytes.Length, Allocator.Temp))
                    {
                        writer.WriteValueSafe(progress.Active ? (byte)1 : (byte)0);
                        writer.WriteValueSafe(progress.RinkIndex);
                        writer.WriteValueSafe(progress.InFavour);
                        writer.WriteValueSafe(progress.Required);
                        writer.WriteValueSafe((uint)keyBytes.Length);
                        if (keyBytes.Length > 0) writer.WriteBytesSafe(keyBytes);
                        messaging.SendNamedMessage(
                            ProgressChannel,
                            clientId,
                            writer,
                            NetworkDelivery.Reliable);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Strip progress sync failed for " + clientId + ": " + ex.Message);
                }
            }
        }

        private static void OnVoteRemoved(Dictionary<string, object> message)
        {
            try
            {
                if (message == null || !message.TryGetValue("vote", out object raw) || !(raw is Vote vote))
                    return;
                if (vote.Name != VoteName) return;

                voteOpenedAt = -1f;
                ClearProgress();

                if (!vote.Passed)
                {
                    if (!suppressExpireAnnounce)
                        BroadcastChat("Tools strip vote ended (timed out or failed).", "#95a5a6");
                    return;
                }

                if (!RinkStripModeUtil.TryParseVoteKey(vote.Data as string, out int rinkIndex, out RinkStripMode mode))
                    return;

                ApplyStripMode(rinkIndex, mode, announce: true);
                RinkMotdService.BroadcastStatus();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Strip vote apply failed: " + ex.Message);
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
                    Debug.LogWarning("[PHLPractice] Strip vote private chat failed: " + ex.Message);
                }
            });
        }

        private static void BroadcastChat(string message, string color)
        {
            ChatOutbound.Enqueue(() =>
            {
                try
                {
                    ChatManager chat = NetworkBehaviourSingleton<ChatManager>.Instance
                        ?? UnityEngine.Object.FindFirstObjectByType<ChatManager>();
                    chat?.Server_BroadcastChatMessage(message, color);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PHLPractice] Strip vote chat failed: " + ex.Message);
                }
            });
        }

        private static void AttachNetwork(NetworkManager manager)
        {
            registeredManager = manager;
            TryRegisterMessaging(manager);
        }

        private static void TryRegisterMessaging(NetworkManager manager)
        {
            CustomMessagingManager messaging = manager?.CustomMessagingManager;
            if (messaging == null || ReferenceEquals(messaging, registeredMessaging)) return;
            try { messaging.UnregisterNamedMessageHandler(Channel); } catch { }
            try { messaging.UnregisterNamedMessageHandler(ProgressChannel); } catch { }
            messaging.RegisterNamedMessageHandler(Channel, OnClientMessage);
            messaging.RegisterNamedMessageHandler(ProgressChannel, OnProgressSyncReceived);
            registeredMessaging = messaging;
        }

        private static void DetachNetwork()
        {
            CustomMessagingManager messaging = registeredMessaging;
            registeredMessaging = null;
            registeredManager = null;
            try
            {
                messaging?.UnregisterNamedMessageHandler(Channel);
                messaging?.UnregisterNamedMessageHandler(ProgressChannel);
            }
            catch { }
        }

        private static void OnClientMessage(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer) return;
                if (FakePlayerDetector.IsAnyFakeClientId(senderClientId)) return;

                reader.ReadValueSafe(out uint length);
                if (length == 0 || length > MaxKeyBytes) return;
                byte[] bytes = new byte[(int)length];
                reader.ReadBytesSafe(ref bytes, (int)length);
                string key = Encoding.UTF8.GetString(bytes);
                if (!RinkStripModeUtil.TryParseVoteKey(key, out int rinkIndex, out RinkStripMode mode))
                    return;

                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                Player player = pm != null ? pm.GetPlayerByClientId(senderClientId) : null;
                if (player == null) return;
                ServerHandleVoteRequest(player, rinkIndex, mode);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Bad strip vote message: " + ex.Message);
            }
        }

        private static void OnProgressSyncReceived(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsClient) return;
                if (senderClientId != NetworkManager.ServerClientId) return;

                reader.ReadValueSafe(out byte active);
                reader.ReadValueSafe(out int rinkIndex);
                reader.ReadValueSafe(out int inFavour);
                reader.ReadValueSafe(out int required);
                reader.ReadValueSafe(out uint length);
                if (length > MaxKeyBytes) return;

                RinkStripVoteProgress progress = RinkStripVoteProgress.None;
                if (active != 0)
                {
                    RinkStripMode mode = RinkStripMode.Empty;
                    if (length > 0)
                    {
                        byte[] bytes = new byte[(int)length];
                        reader.ReadBytesSafe(ref bytes, (int)length);
                        RinkStripModeUtil.TryParseVoteKey(Encoding.UTF8.GetString(bytes), out rinkIndex, out mode);
                    }
                    progress = new RinkStripVoteProgress
                    {
                        Active = true,
                        RinkIndex = rinkIndex,
                        Mode = mode,
                        InFavour = inFavour,
                        Required = required
                    };
                }

                CurrentProgress = progress;
                RinkMotdUI.ApplyStripVoteProgress(progress);
                RinkScoreboardTab.ApplyStripVoteProgress(progress);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Bad strip progress sync: " + ex.Message);
            }
        }
    }
}
