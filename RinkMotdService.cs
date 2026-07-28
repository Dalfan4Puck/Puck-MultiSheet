using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server-side rink occupancy (players counted by position) and the custom-message
    /// channels backing the practice MOTD: status broadcasts down, teleport requests up.
    /// </summary>
    internal static class RinkMotdService
    {
        private const string StatusChannel = "multisheet-motd-v1";
        private const string RequestChannel = "multisheet-motd-req-v1";
        private const byte ProtocolVersion = 4;
        private const byte OpRequestShow = 0;
        private const byte OpTeleport = 1;
        private const byte OpSetRole = 2;
        private const byte OpSetSlidable = 3;
        private const int MaxPayloadBytes = 4096;
        private const float OccupancyPollSeconds = 0.5f;

        private static readonly HashSet<ulong> autoSentClients = new HashSet<ulong>();
        private static NetworkManager registeredManager;
        private static CustomMessagingManager registeredMessaging;
        private static bool active;
        private static bool serverRoleActive;
        private static bool localConnected;
        private static float nextOccupancyPoll;
        private static int[] lastCounts;

        internal static void Initialize()
        {
            active = true;
            Tick();

            // Mid-session enable: the player may already be connected and the server
            // already sent the welcome packet before our handlers were registered.
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsConnectedClient)
            {
                if (!localConnected)
                {
                    localConnected = true;
                    PracticeFlowClient.OnLocalConnected();
                }
                ClientRequestShow();
            }
        }

        internal static void Tick()
        {
            if (!active) return;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager != registeredManager)
            {
                DetachNetwork();
                if (manager != null) AttachNetwork(manager);
            }

            if (manager == null)
            {
                ResetLocalConnection();
                return;
            }

            TryRegisterMessaging(manager);

            if (manager.IsServer && !serverRoleActive)
            {
                serverRoleActive = true;
                autoSentClients.Clear();
                lastCounts = null;
            }
            else if (!manager.IsServer && serverRoleActive)
            {
                serverRoleActive = false;
                autoSentClients.Clear();
            }

            if (manager.IsServer)
            {
                ServerTick(manager);
                PracticeFlowServer.Tick();
            }

            bool connectedNow = manager.IsConnectedClient;
            if (connectedNow && !localConnected)
            {
                localConnected = true;
                PracticeFlowClient.OnLocalConnected();
            }
            else if (!connectedNow && localConnected)
            {
                ResetLocalConnection();
            }

            RinkMotdUI.Tick();
            PracticeFlowClient.Tick();
        }

        internal static void Teardown()
        {
            active = false;
            DetachNetwork();
            serverRoleActive = false;
            autoSentClients.Clear();
            lastCounts = null;
            PracticeFlowServer.Reset();
            PracticeFlowClient.Reset();
            PracticeGoalieSpawn.Reset();
            ResetLocalConnection();
            RinkMotdUI.Teardown();
            RinkStripVote.Teardown();
            RinkScoreboardTab.Teardown();
            RinkPreview.Teardown();
        }

        // ---------------------------------------------------------------- server

        /// <summary>Players per rink, counted by nearest-rink body position (server).</summary>
        internal static int[] CountPlayersPerRink(MultiRinkConfig cfg)
        {
            int rinkCount = cfg?.Rinks != null ? cfg.Rinks.Count : 0;
            int[] counts = new int[Mathf.Max(rinkCount, 1)];
            if (rinkCount == 0) return counts;

            try
            {
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm == null) return counts;
                foreach (Player player in pm.GetPlayers())
                {
                    if (player == null || player.PlayerBody == null) continue;
                    int rink = RinkLocator.NearestRink(cfg, player.PlayerBody.transform.position);
                    if (rink >= 0 && rink < counts.Length) counts[rink]++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Rink occupancy count failed: " + ex.Message);
            }
            return counts;
        }

        /// <summary>Human players standing on a specific rink (nearest-rink body position).</summary>
        internal static int CountHumanPlayersOnRink(MultiRinkConfig cfg, int rinkIndex)
        {
            if (cfg?.Rinks == null || rinkIndex < 0 || rinkIndex >= cfg.Rinks.Count)
                return 0;

            int count = 0;
            try
            {
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm == null) return 0;
                foreach (Player player in pm.GetPlayers())
                {
                    if (player == null || player.PlayerBody == null) continue;
                    if (FakePlayerDetector.IsAnyFakePlayer(player)) continue;
                    try
                    {
                        if (FakePlayerDetector.IsAnyFakeClientId(player.OwnerClientId)) continue;
                    }
                    catch { }
                    try { if (player.IsReplay != null && player.IsReplay.Value) continue; } catch { }

                    int rink = RinkLocator.NearestRink(cfg, player.PlayerBody.transform.position);
                    if (rink == rinkIndex) count++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Human rink count failed: " + ex.Message);
            }
            return count;
        }

        /// <summary>Capacity gate used by chat commands and MOTD teleports alike.</summary>
        internal static bool IsRinkFullFor(ulong clientId, RinkSlot slot, out string message)
        {
            message = null;
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            int capacity = cfg.RinkCapacity;
            if (capacity <= 0) return false;

            int targetIndex = cfg.Rinks.IndexOf(slot);
            if (targetIndex < 0) return false;

            // Moving to the rink you are already on is always allowed (respawn).
            int currentIndex = -1;
            try
            {
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                Player player = pm != null ? pm.GetPlayerByClientId(clientId) : null;
                if (player != null && player.PlayerBody != null)
                    currentIndex = RinkLocator.NearestRink(cfg, player.PlayerBody.transform.position);
            }
            catch { }
            if (currentIndex == targetIndex) return false;

            int[] counts = CountPlayersPerRink(cfg);
            if (targetIndex < counts.Length && counts[targetIndex] >= capacity)
            {
                message = $"{slot.Label} is full ({counts[targetIndex]}/{capacity}).";
                return true;
            }
            return false;
        }

        /// <summary>Push fresh status to everyone (e.g. right after a teleport).</summary>
        internal static void BroadcastStatus()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsServer) return;
            lastCounts = CountPlayersPerRink(MultiRinkConfig.Current);
            SendStatusToAll(manager, forceShow: false);
        }

        private static void ServerTick(NetworkManager manager)
        {
            if (Time.unscaledTime < nextOccupancyPoll) return;
            nextOccupancyPoll = Time.unscaledTime + OccupancyPollSeconds;

            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (!cfg.EnableMultiRink || cfg.Rinks == null || cfg.Rinks.Count == 0) return;

            int[] counts = CountPlayersPerRink(cfg);
            bool changed = lastCounts == null || lastCounts.Length != counts.Length;
            if (!changed)
            {
                for (int i = 0; i < counts.Length; i++)
                {
                    if (counts[i] != lastCounts[i]) { changed = true; break; }
                }
            }

            if (changed)
            {
                lastCounts = counts;
                SendStatusToAll(manager, forceShow: false);
            }
        }

        private static void SendStatusToAll(NetworkManager manager, bool forceShow)
        {
            if (manager.ConnectedClientsIds == null) return;
            foreach (ulong clientId in manager.ConnectedClientsIds)
                SendStatus(clientId, forceShow, welcomeIfFirst: true);
        }

        private static void SendStatus(ulong clientId, bool forceShow, bool welcomeIfFirst)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.CustomMessagingManager == null || !manager.IsServer) return;

            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0) return;
            int[] counts = lastCounts ?? CountPlayersPerRink(cfg);

            // First status for a client doubles as the welcome trigger.
            bool welcome = welcomeIfFirst && !autoSentClients.Contains(clientId);

            try
            {
                using (FastBufferWriter writer = new FastBufferWriter(MaxPayloadBytes, Allocator.Temp))
                {
                    writer.WriteValueSafe(ProtocolVersion);
                    writer.WriteValueSafe((byte)(forceShow ? 1 : welcome ? 2 : 0));
                    writer.WriteValueSafe((byte)Mathf.Clamp(cfg.RinkCapacity, 0, 255));
                    WriteString(writer, cfg.MotdTitle ?? "");
                    WriteString(writer, cfg.MotdSubtitle ?? "");
                    writer.WriteValueSafe((byte)Mathf.Min(cfg.Rinks.Count, 16));
                    for (int i = 0; i < cfg.Rinks.Count && i < 16; i++)
                    {
                        RinkSlot slot = cfg.Rinks[i];
                        WriteString(writer, slot?.Id ?? ("rink" + (i + 1)));
                        WriteString(writer, slot?.Label ?? ("Rink " + (i + 1)));
                        writer.WriteValueSafe((byte)Mathf.Clamp(i < counts.Length ? counts[i] : 0, 0, 255));
                        Vector3 origin = slot != null ? slot.Origin : Vector3.zero;
                        writer.WriteValueSafe(origin.x);
                        writer.WriteValueSafe(origin.z);
                    }

                    writer.WriteValueSafe(ReadLocalRoleByte(clientId));

                    int stripCount = Mathf.Min(cfg.Rinks.Count, 16);
                    writer.WriteValueSafe((byte)stripCount);
                    for (int i = 0; i < stripCount; i++)
                    {
                        RinkStripMode mode = RinkStripVote.GetServerMode(i);
                        writer.WriteValueSafe((byte)mode);
                    }

                    writer.WriteValueSafe((byte)(FlamiePracFeatures.SlidablePhysicsEnabled ? 1 : 0));

                    manager.CustomMessagingManager.SendNamedMessage(
                        StatusChannel, clientId, writer, NetworkDelivery.ReliableSequenced);
                }
                autoSentClients.Add(clientId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Rink status send failed for client " + clientId + ": " + ex.Message);
            }
        }

        private static void OnRequestReceived(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                NetworkManager manager = NetworkManager.Singleton;
                if (manager == null || !manager.IsServer) return;

                reader.ReadValueSafe(out byte op);
                if (op == OpRequestShow)
                {
                    lastCounts = CountPlayersPerRink(MultiRinkConfig.Current);
                    SendStatus(senderClientId, forceShow: true, welcomeIfFirst: false);
                    return;
                }

                if (op != OpTeleport && op != OpSetRole && op != OpSetSlidable) return;

                if (op == OpSetRole)
                {
                    reader.ReadValueSafe(out byte roleByte);
                    PlayerRole role = roleByte == 1 ? PlayerRole.Goalie : PlayerRole.Attacker;
                    if (PracticeFlowServer.TrySetRole(senderClientId, role, out string roleMessage))
                        PracticeLog.Info("[PHLPractice] Role change client=" + senderClientId + " -> " + role);
                    QueuePrivateChat(senderClientId, roleMessage ?? "Could not change role.");
                    BroadcastStatus();
                    return;
                }

                if (op == OpSetSlidable)
                {
                    reader.ReadValueSafe(out byte enabledByte);
                    if (!TryAuthorizeSlidable(senderClientId, out string denyMessage))
                    {
                        QueuePrivateChat(senderClientId, denyMessage ?? "Admin only.");
                        return;
                    }

                    bool enabled = enabledByte != 0;
                    FlamiePracFeatures.SetSlidablePhysicsEnabled(enabled);
                    PracticeLog.Info("[PHLPractice] Slidable physics " + (enabled ? "enabled" : "disabled") +
                                     " by client " + senderClientId);
                    QueuePrivateChat(senderClientId, "Slidable physics " + (enabled ? "enabled" : "disabled") + ".");
                    BroadcastStatus();
                    return;
                }

                if (op != OpTeleport) return;

                reader.ReadValueSafe(out byte rinkIndex);

                MultiRinkConfig cfg = MultiRinkConfig.Current;
                if (cfg?.Rinks == null || rinkIndex >= cfg.Rinks.Count) return;
                RinkSlot slot = cfg.Rinks[rinkIndex];
                if (slot == null) return;

                if (MultiRinkService.TryAssignRink(senderClientId, slot, out string message))
                {
                    PracticeLog.Info("[PHLPractice] MOTD teleport client=" + senderClientId + " -> " + slot.Id);
                }
                QueuePrivateChat(senderClientId, message ?? "Could not switch rink.");
                BroadcastStatus();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Rink teleport request rejected: " + ex.Message);
            }
        }

        private static void QueuePrivateChat(ulong clientId, string message)
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

        // ---------------------------------------------------------------- client

        /// <summary>Ask the server for a fresh status + force the MOTD open (F9 / tab).</summary>
        internal static void ClientRequestShow()
        {
            SendRequest(writer =>
            {
                writer.WriteValueSafe(OpRequestShow);
            }, 1);
        }

        /// <summary>Ask the server to teleport the local player to a rink.</summary>
        internal static void ClientRequestTeleport(int rinkIndex)
        {
            if (rinkIndex < 0 || rinkIndex > 255) return;
            byte index = (byte)rinkIndex;
            SendRequest(writer =>
            {
                writer.WriteValueSafe(OpTeleport);
                writer.WriteValueSafe(index);
            }, 2);
        }

        /// <summary>Ask the server to switch the local player between skater and goalie.</summary>
        internal static void ClientRequestSetRole(byte role)
        {
            SendRequest(writer =>
            {
                writer.WriteValueSafe(OpSetRole);
                writer.WriteValueSafe(role > 0 ? (byte)1 : (byte)0);
            }, 2);
        }

        /// <summary>Ask the server to enable or disable slidable physics (admin/host).</summary>
        internal static void ClientRequestSetSlidable(bool enabled)
        {
            SendRequest(writer =>
            {
                writer.WriteValueSafe(OpSetSlidable);
                writer.WriteValueSafe(enabled ? (byte)1 : (byte)0);
            }, 2);
        }

        private static void SendRequest(Action<FastBufferWriter> fill, int size)
        {
            try
            {
                NetworkManager manager = NetworkManager.Singleton;
                if (manager == null || !manager.IsConnectedClient) return;
                CustomMessagingManager messaging = manager.CustomMessagingManager;
                if (messaging == null) return;

                using (FastBufferWriter writer = new FastBufferWriter(size, Allocator.Temp))
                {
                    fill(writer);
                    messaging.SendNamedMessage(
                        RequestChannel, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Rink MOTD request failed: " + ex.Message);
            }
        }

        private static void OnStatusReceived(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                NetworkManager manager = NetworkManager.Singleton;
                if (manager == null || !manager.IsClient || senderClientId != NetworkManager.ServerClientId) return;

                reader.ReadValueSafe(out byte version);
                if (version != ProtocolVersion) return;
                reader.ReadValueSafe(out byte show); // 0 update, 1 forced, 2 welcome
                reader.ReadValueSafe(out byte capacity);

                var payload = new RinkMotdPayload
                {
                    Capacity = capacity,
                    Title = ReadString(reader),
                    Subtitle = ReadString(reader)
                };

                reader.ReadValueSafe(out byte rinkCount);
                for (int i = 0; i < rinkCount; i++)
                {
                    var entry = new RinkStatusEntry
                    {
                        Id = ReadString(reader),
                        Label = ReadString(reader)
                    };
                    reader.ReadValueSafe(out byte count);
                    entry.Count = count;
                    reader.ReadValueSafe(out float ox);
                    reader.ReadValueSafe(out float oz);
                    entry.OriginX = ox;
                    entry.OriginZ = oz;
                    payload.Rinks.Add(entry);
                }

                if (version >= 2)
                {
                    reader.ReadValueSafe(out byte localRole);
                    payload.LocalRole = localRole > 0 ? (byte)1 : (byte)0;
                }

                if (version >= 3)
                {
                    reader.ReadValueSafe(out byte stripCount);
                    for (int i = 0; i < stripCount; i++)
                    {
                        reader.ReadValueSafe(out byte modeByte);
                        payload.StripModes.Add(RinkStripModeUtil.Parse(modeByte));
                    }
                    payload.StripVoteProgress = RinkStripVote.CurrentProgress;
                }

                if (version >= 4)
                {
                    reader.ReadValueSafe(out byte slidableByte);
                    payload.SlidablePhysicsEnabled = slidableByte != 0;
                }

                // Build deferred client-side rink visuals before the MOTD/preview rig
                // captures tiles — otherwise rinks 2+ render without cloned geometry or
                // offset fill lights (dark thumbnails). Pass the payload so the client
                // clones the server's rink count/origins, not the built-in defaults.
                CustomLevelPlugin.ConfirmPracticeServer(payload);

                RinkMotdUI.OnStatusReceived(payload, show);
                RinkScoreboardTab.OnStripModesUpdated(payload);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Rejected malformed rink status: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- plumbing

        private static void AttachNetwork(NetworkManager manager)
        {
            registeredManager = manager;
            manager.OnClientConnectedCallback += OnClientConnected;
            manager.OnClientDisconnectCallback += OnClientDisconnected;
            TryRegisterMessaging(manager);
        }

        private static void TryRegisterMessaging(NetworkManager manager)
        {
            CustomMessagingManager messaging = manager?.CustomMessagingManager;
            if (messaging == null || ReferenceEquals(messaging, registeredMessaging)) return;
            try { messaging.UnregisterNamedMessageHandler(StatusChannel); } catch { }
            try { messaging.UnregisterNamedMessageHandler(RequestChannel); } catch { }
            messaging.RegisterNamedMessageHandler(StatusChannel, OnStatusReceived);
            messaging.RegisterNamedMessageHandler(RequestChannel, OnRequestReceived);
            registeredMessaging = messaging;
        }

        private static void DetachNetwork()
        {
            NetworkManager manager = registeredManager;
            registeredManager = null;
            CustomMessagingManager messaging = registeredMessaging;
            registeredMessaging = null;
            if (manager == null) return;

            try
            {
                manager.OnClientConnectedCallback -= OnClientConnected;
                manager.OnClientDisconnectCallback -= OnClientDisconnected;
                messaging?.UnregisterNamedMessageHandler(StatusChannel);
                messaging?.UnregisterNamedMessageHandler(RequestChannel);
            }
            catch { }
        }

        private static void OnClientConnected(ulong clientId)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null) return;

            if (clientId == manager.LocalClientId)
            {
                localConnected = true;
                PracticeFlowClient.OnLocalConnected();
            }

            if (!manager.IsServer) return;
            lastCounts = CountPlayersPerRink(MultiRinkConfig.Current);
            SendStatus(clientId, forceShow: false, welcomeIfFirst: true);
        }

        private static void OnClientDisconnected(ulong clientId)
        {
            autoSentClients.Remove(clientId);
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsServer)
            {
                PracticeFlowServer.OnClientDisconnected(clientId);
                MultiRinkService.OnClientDisconnected(clientId);
            }
            if (manager != null && clientId == manager.LocalClientId) ResetLocalConnection();
        }

        private static void ResetLocalConnection()
        {
            localConnected = false;
            PracticeFlowClient.OnLocalDisconnected();
            MinimapSessionOverride.RestoreOnDisconnect();
            RinkMotdUI.OnDisconnected();
            RinkScoreboardTab.OnDisconnected();
            RinkPreview.Teardown();
            CustomLevelPlugin.OnPracticeConnectionLost();
        }

        private static byte ReadLocalRoleByte(ulong clientId)
        {
            try
            {
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                Player player = pm != null ? pm.GetPlayerByClientId(clientId) : null;
                if (player != null && player.Role == PlayerRole.Goalie) return 1;
            }
            catch { }
            return 0;
        }

        private static void WriteString(FastBufferWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > 512)
            {
                byte[] cut = new byte[512];
                Buffer.BlockCopy(bytes, 0, cut, 0, 512);
                bytes = cut;
            }
            writer.WriteValueSafe((ushort)bytes.Length);
            writer.WriteBytesSafe(bytes);
        }

        private static string ReadString(FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort length);
            if (length > 512) throw new InvalidOperationException("Rink status string too large.");
            byte[] bytes = new byte[length];
            reader.ReadBytesSafe(ref bytes, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool TryAuthorizeSlidable(ulong clientId, out string message)
        {
            message = null;
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer && clientId == nm.LocalClientId)
                return true;

            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            Player player = pm != null ? pm.GetPlayerByClientId(clientId) : null;
            if (player == null)
            {
                message = "Player not found.";
                return false;
            }

            if (IsAdminPlayer(player))
                return true;

            message = "Admin only: slidable toggle requires host or admin.";
            return false;
        }

        private static bool IsAdminPlayer(Player player)
        {
            if (player == null) return false;
            if (player.AdminLevel != null && player.AdminLevel.Value > 0)
                return true;

            try
            {
                ServerManager instance = NetworkBehaviourSingleton<ServerManager>.Instance;
                if (instance?.AdminManager == null)
                    return false;
                AdminManager adminManager = instance.AdminManager;
                FixedString32Bytes value = player.SteamId.Value;
                return adminManager.IsSteamIdAdmin(value.ToString());
            }
            catch
            {
                return false;
            }
        }
    }
}
