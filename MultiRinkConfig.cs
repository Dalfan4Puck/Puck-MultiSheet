using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server config for multi-rink layout. Copy config/multi_rink.example.json to
    /// ./config/multi_rink.json beside the plugin DLL on the host.
    /// </summary>
    internal sealed class MultiRinkConfig
    {
        public int ConfigVersion = 1;
        /// <summary>When true, clone base-game Rink + goals for each configured slot (no AssetBundle).</summary>
        public bool EnableMultiRink = true;
        /// <summary>Legacy: open-world TestLevel bundle from PuckLargeLevel. Off by default.</summary>
        public bool UseAssetBundle;
        public bool HideHangar = true;
        public string AssetBundleFile = "assets/puckobjects";
        public string LevelPrefabName = "TestLevel";
        public List<string> CloneTemplates = new List<string>
        {
            "Rink",
            "Goal Blue",
            "Goal Red",
            "Lights/Goal Blue",
            "Lights/Goal Red",
        };
        /// <summary>
        /// Rink spacing must be a multiple of the 32 m chunk grid so each rink's origin
        /// lands exactly on a chunk center and the whole rink fits one chunk (±50 m window).
        /// </summary>
        public float RinkSpacingZ = 128f;
        /// <summary>
        /// Column spacing for grid layouts. Same chunk-grid rule as RinkSpacingZ.
        /// 64 m is the tightest lateral pitch the rectangular chunk-zone envelopes
        /// allow (rinks are ~45 m wide, envelopes ±30 m on X).
        /// </summary>
        public float RinkSpacingX = 64f;
        /// <summary>Max players per rink (0 disables the cap). Enforced on chat and MOTD teleports.</summary>
        public int RinkCapacity = 5;
        /// <summary>
        /// Send rate (Hz) for objects on rinks OTHER than the one a client is standing
        /// on. Own-rink objects always stream at the full sync tick rate. 0 disables
        /// the throttle (vanilla full-rate broadcast of everything to everyone).
        /// </summary>
        public int OffRinkSyncHz = 10;
        /// <summary>Re-enable informational logging (clone/teleport/sync details).</summary>
        public bool VerboseLogging;
        /// <summary>Welcome MOTD headline shown to joining clients.</summary>
        public string MotdTitle = "Welcome to PHL MultiSheet Practice";
        /// <summary>Welcome MOTD subtitle shown to joining clients.</summary>
        public string MotdSubtitle = "Nine sheets, one server. Pick a rink below — press R for a puck.";
        public List<RinkSlot> Rinks = new List<RinkSlot>();

        public static MultiRinkConfig Current { get; private set; } = CreateDefaults();

        /// <summary>
        /// Pure clients do not read the host's multi_rink.json — they start on the
        /// built-in default layout. Once the server's MOTD status arrives, replace the
        /// rink list with the server's origins so BuildClientSide matches the host
        /// (e.g. a 1-rink A/B must not still spawn nine client sheets).
        /// </summary>
        public static void ApplyClientLayoutFromServer(RinkMotdPayload payload)
        {
            if (payload?.Rinks == null || payload.Rinks.Count == 0) return;

            MultiRinkConfig cfg = Current ?? CreateDefaults();
            var rinks = new List<RinkSlot>(payload.Rinks.Count);
            for (int i = 0; i < payload.Rinks.Count; i++)
            {
                RinkStatusEntry entry = payload.Rinks[i];
                if (entry == null) continue;
                Vector3 origin = entry.Origin;
                string id = string.IsNullOrEmpty(entry.Id) ? ("rink" + (i + 1)) : entry.Id;
                string label = string.IsNullOrEmpty(entry.Label) ? ("Rink " + (i + 1)) : entry.Label;
                string command = "/rink" + (i + 1);
                rinks.Add(new RinkSlot(id, command, label, origin, origin));
            }

            if (rinks.Count == 0) return;
            cfg.Rinks = rinks;
            Current = cfg;
            PracticeLog.Info("[PHLPractice] Client layout synced from server (" + rinks.Count + " rink(s)).");
        }

        public static void LoadServerConfig()
        {
            try
            {
                string path = Path.Combine(".", "config", "multi_rink.json");
                if (!File.Exists(path))
                {
                    Current = CreateDefaults();
                    PracticeLog.Info("[PHLPractice] No config/multi_rink.json — using built-in rink layout.");
                    return;
                }

                var loaded = JsonConvert.DeserializeObject<MultiRinkConfig>(File.ReadAllText(path));
                if (loaded?.Rinks == null || loaded.Rinks.Count == 0)
                {
                    Debug.LogWarning("[PHLPractice] multi_rink.json invalid — using defaults.");
                    Current = CreateDefaults();
                    return;
                }

                Current = loaded;
                PracticeLog.Info($"[PHLPractice] Loaded multi-rink config ({Current.Rinks.Count} rinks, multiRink={Current.EnableMultiRink}, bundle={Current.UseAssetBundle}).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Failed to load multi_rink.json: " + ex.Message);
                Current = CreateDefaults();
            }
        }

        public static MultiRinkConfig CreateDefaults()
        {
            // Nine rinks in a 3×3 world grid (3 columns on X, 3 rows on Z), each origin
            // on the chunk grid so every rink still gets its own dedicated chunk
            // (x ∈ {0,64,128}, z ∈ {0,128,256} are all multiples of 32 m).
            // Rink 1 stays at (0,0) — vanilla clients must see it byte-identical.
            var config = new MultiRinkConfig { EnableMultiRink = true, Rinks = new List<RinkSlot>() };
            for (int i = 0; i < 9; i++)
            {
                float x = (i % 3) * config.RinkSpacingX;
                float z = (i / 3) * config.RinkSpacingZ;
                config.Rinks.Add(new RinkSlot(
                    "rink" + (i + 1),
                    "/rink" + (i + 1),
                    "Rink " + (i + 1),
                    new Vector3(x, 0f, z),
                    new Vector3(x, 0f, z)));
            }
            return config;
        }

        public RinkSlot FindByCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string normalized = command.Trim().ToLowerInvariant();
            for (int i = 0; i < Rinks.Count; i++)
            {
                RinkSlot slot = Rinks[i];
                if (slot != null && string.Equals(slot.Command, normalized, StringComparison.OrdinalIgnoreCase))
                    return slot;
            }
            return null;
        }
    }

    internal sealed class RinkSlot
    {
        public string Id;
        public string Command;
        public string Label;
        public Vec3 WorldOrigin;
        public Vec3 Spawn;

        public RinkSlot() { }

        public RinkSlot(string id, string command, string label, Vector3 origin, Vector3 spawn)
        {
            Id = id;
            Command = command;
            Label = label;
            WorldOrigin = Vec3.From(origin);
            Spawn = Vec3.From(spawn);
        }

        public Vector3 Origin => WorldOrigin.ToVector3();
        public Vector3 SpawnPoint => Spawn.ToVector3();
    }

    internal struct Vec3
    {
        public float x, y, z;

        public static Vec3 From(Vector3 v) => new Vec3 { x = v.x, y = v.y, z = v.z };
        public Vector3 ToVector3() => new Vector3(x, y, z);
    }
}
