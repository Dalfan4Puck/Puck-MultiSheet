using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Server config for multi-rink layout. One file only:
    /// <c>{plugin}/config/multi_rink.json</c> (Workshop folder on dedicated hosts).
    /// Rink positions are computed from <see cref="RinkCount"/> — never edited by hand.
    /// </summary>
    internal sealed class MultiRinkConfig
    {
        public const int CurrentConfigVersion = 7;
        public const int GridColumns = 3;
        public const int MaxRinks = 9;
        public const int DefaultRinkCount = 6;

        public int ConfigVersion = CurrentConfigVersion;
        /// <summary>How many sheets to clone (1–9). Layout is a 3-wide grid from rink 1 at (0,0).</summary>
        public int RinkCount = DefaultRinkCount;
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
        public float RinkSpacingZ = 128f;
        public float RinkSpacingX = 64f;
        public int RinkCapacity = 5;
        public int OffRinkSyncHz = 10;
        public bool VerboseLogging;
        public string MotdTitle = "Welcome to PHL MultiSheet Practice";
        public string MotdSubtitle = "Six sheets, one server. Pick a rink below — press R for a puck.";

        /// <summary>Legacy import only — coordinates in old JSON files are ignored.</summary>
        [JsonProperty("Rinks")]
        private List<RinkSlot> LegacyRinksJson;

        /// <summary>Runtime grid built from <see cref="RinkCount"/>.</summary>
        [JsonIgnore]
        public List<RinkSlot> Rinks { get; private set; } = new List<RinkSlot>();

        public static MultiRinkConfig Current { get; private set; } = CreateDefaults();

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
                rinks.Add(new RinkSlot(id, "/rink" + (i + 1), label, origin, origin));
            }

            if (rinks.Count == 0) return;
            cfg.Rinks = rinks;
            cfg.RinkCount = rinks.Count;
            Current = cfg;
            PracticeLog.Info("[PHLPractice] Client layout synced from server (" + rinks.Count + " rink(s)).");
        }

        public static void LoadServerConfig()
        {
            try
            {
                string path = GetConfigFilePath(createDir: true);
                if (path == null)
                {
                    Current = CreateDefaults();
                    Debug.LogWarning("[PHLPractice] Could not resolve plugin config directory — using 6-rink defaults.");
                    return;
                }

                if (!File.Exists(path))
                {
                    if (Application.isBatchMode)
                    {
                        WriteDefaultConfigFile(path);
                        PracticeLog.Info("[PHLPractice] Created default config/multi_rink.json at " + path);
                    }
                    else
                    {
                        PracticeLog.Info("[PHLPractice] No config/multi_rink.json beside plugin — using 6-rink defaults.");
                        Current = CreateDefaults();
                        return;
                    }
                }

                MultiRinkConfig loaded = JsonConvert.DeserializeObject<MultiRinkConfig>(File.ReadAllText(path));
                if (loaded == null)
                {
                    Debug.LogWarning("[PHLPractice] multi_rink.json empty — using 6-rink defaults.");
                    Current = CreateDefaults();
                    return;
                }

                loaded.NormalizeAndBuildLayout(out string note);
                Current = loaded;
                PracticeLog.Info("[PHLPractice] Loaded multi_rink.json from " + path + " (" +
                          Current.Rinks.Count + " rinks, VerboseLogging=" + Current.VerboseLogging +
                          (string.IsNullOrEmpty(note) ? "" : ", " + note) + ").");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Failed to load multi_rink.json: " + ex.Message +
                                 " — using 6-rink defaults (fix JSON booleans: use false not False).");
                Current = CreateDefaults();
            }
        }

        /// <summary>Single config location: <c>{plugin}/config/multi_rink.json</c>.</summary>
        internal static string GetConfigFilePath(bool createDir)
        {
            try
            {
                string pluginDir = Path.GetDirectoryName(typeof(MultiRinkConfig).Assembly.Location);
                if (string.IsNullOrEmpty(pluginDir))
                    return null;

                string configDir = Path.Combine(pluginDir, "config");
                if (createDir && !Directory.Exists(configDir))
                    Directory.CreateDirectory(configDir);
                return Path.Combine(configDir, "multi_rink.json");
            }
            catch
            {
                return null;
            }
        }

        internal static void WriteDefaultConfigFile(string path)
        {
            MultiRinkConfig template = CreateDefaults();
            string json = JsonConvert.SerializeObject(template.ToFileModel(), Formatting.Indented);
            File.WriteAllText(path, json);
        }

        private void NormalizeAndBuildLayout(out string note)
        {
            note = null;
            int count = RinkCount;

            if (LegacyRinksJson != null && LegacyRinksJson.Count > 0)
            {
                if (count <= 0)
                    count = LegacyRinksJson.Count;
                note = "legacy Rinks[] coordinates ignored";
            }

            if (count <= 0)
                count = DefaultRinkCount;

            RinkCount = Mathf.Clamp(count, 1, MaxRinks);
            Rinks = BuildRinkGrid(RinkCount, RinkSpacingX, RinkSpacingZ);
            LegacyRinksJson = null;

            if (string.IsNullOrEmpty(MotdSubtitle))
                MotdSubtitle = RinkCount + " sheets, one server. Pick a rink below — press R for a puck.";
        }

        internal static List<RinkSlot> BuildRinkGrid(int count, float spacingX, float spacingZ)
        {
            count = Mathf.Clamp(count, 1, MaxRinks);
            var list = new List<RinkSlot>(count);
            for (int i = 0; i < count; i++)
            {
                float x = (i % GridColumns) * spacingX;
                float z = (i / GridColumns) * spacingZ;
                var origin = new Vector3(x, 0f, z);
                int n = i + 1;
                list.Add(new RinkSlot("rink" + n, "/rink" + n, "Rink " + n, origin, origin));
            }
            return list;
        }

        public static MultiRinkConfig CreateDefaults()
        {
            var config = new MultiRinkConfig { EnableMultiRink = true, RinkCount = DefaultRinkCount };
            config.NormalizeAndBuildLayout(out _);
            return config;
        }

        internal MultiRinkConfigFile ToFileModel()
        {
            return new MultiRinkConfigFile
            {
                ConfigVersion = CurrentConfigVersion,
                RinkCount = RinkCount,
                EnableMultiRink = EnableMultiRink,
                UseAssetBundle = UseAssetBundle,
                HideHangar = HideHangar,
                CloneTemplates = CloneTemplates != null ? new List<string>(CloneTemplates) : null,
                AssetBundleFile = AssetBundleFile,
                LevelPrefabName = LevelPrefabName,
                RinkSpacingZ = RinkSpacingZ,
                RinkSpacingX = RinkSpacingX,
                RinkCapacity = RinkCapacity,
                OffRinkSyncHz = OffRinkSyncHz,
                VerboseLogging = VerboseLogging,
                MotdTitle = MotdTitle,
                MotdSubtitle = MotdSubtitle,
            };
        }

        public RinkSlot FindByCommand(string command)
        {
            if (string.IsNullOrEmpty(command) || Rinks == null) return null;
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

    /// <summary>On-disk JSON — set <see cref="RinkCount"/> and flags, not world coordinates.</summary>
    internal sealed class MultiRinkConfigFile
    {
        public int ConfigVersion = MultiRinkConfig.CurrentConfigVersion;
        public int RinkCount = MultiRinkConfig.DefaultRinkCount;
        public bool EnableMultiRink = true;
        public bool UseAssetBundle;
        public bool HideHangar = true;
        public List<string> CloneTemplates;
        public string AssetBundleFile = "assets/puckobjects";
        public string LevelPrefabName = "TestLevel";
        public float RinkSpacingZ = 128f;
        public float RinkSpacingX = 64f;
        public int RinkCapacity = 5;
        public int OffRinkSyncHz = 10;
        public bool VerboseLogging;
        public string MotdTitle = "Welcome to PHL MultiSheet Practice";
        public string MotdSubtitle = "Six sheets, one server. Pick a rink below — press R for a puck.";
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
