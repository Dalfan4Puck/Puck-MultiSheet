using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Per-machine client preferences. These only affect what the local player sees, so
    /// they never travel over the network and are not part of the server's
    /// config/multi_rink.json.
    ///
    /// File: <c>{game cwd}/config/multisheet_client.json</c> (Steam Puck install root).
    /// Edit only while Puck is fully quit — <see cref="Flush"/> rewrites the whole file
    /// from memory and will clobber hand-edits made while the game is running.
    /// </summary>
    internal sealed class MultiSheetClientSettings
    {
        private const string RelativePath = "config/multisheet_client.json";

        /// <summary>Opt in to the hour-of-day lighting cycle instead of fixed indoor light.</summary>
        public bool dayNightEnabled = true;

        /// <summary>Pinned hour of day (0-24), or negative to follow the local system clock.</summary>
        public float manualHour = -1f;

        /// <summary>When true, draw/light every offset sheet. When false, only the local rink.</summary>
        public bool renderAllRinks = false;

        /// <summary>
        /// When true, MultiSheet may drive day/night, glare, and TRL re-layer hooks.
        /// When false, stock TRL + simple lighting (Limit Rink Changes).
        /// </summary>
        public bool allowRinkChanges = true;

        // --- FPS A/B kill switches (client only; default off) ---

        /// <summary>Skip CL_ChunkSyncClient enable (chunk decode / FilterAndReplace).</summary>
        public bool skipChunkClient = false;

        /// <summary>
        /// When true, keep clone fill lights off (independent of day/night sun/sky cycle).
        /// Day/night may still drive sun, ambient, and skybox while fills stay disabled.
        /// </summary>
        public bool skipArenaLighting = false;

        /// <summary>Skip deferred client clone / ground / proxy build.</summary>
        public bool skipClientBuild = false;

        /// <summary>Skip practice HUD clock LateTick.</summary>
        public bool skipPracticeHud = false;

        /// <summary>
        /// Hide every puck visually except the local player's R-spawned puck (FPS A/B).
        /// Does not despawn or stop network/physics for the others.
        /// </summary>
        public bool hideStockPucks = false;

        /// <summary>
        /// Skip native scoreboard Rinks-tab inject + Tab hold-open patches (FPS A/B).
        /// Vanilla Tab scoreboard behavior is restored.
        /// </summary>
        public bool skipScoreboardUi = false;

        /// <summary>Skip join auto-open of the Rinks tab (FPS A/B). F9 still opens when scoreboard UI is on.</summary>
        public bool skipMotdUi = false;

        /// <summary>Skip minimap rink-local translate patch (FPS A/B).</summary>
        public bool skipMinimap = false;

        /// <summary>Key bound to spawn test puck (default R).</summary>
        public string spawnPuckKey = "R";

        /// <summary>Key bound to toggle skater/goalie (default P).</summary>
        public string toggleRoleKey = "P";

        /// <summary>Key bound to toggle slidable physics (default L).</summary>
        public string slidableToggleKey = "L";

        /// <summary>Key bound to show/hide the minimap (default M).</summary>
        public string minimapToggleKey = "M";

        /// <summary>Hide the stock minimap while in MultiSheet (client-only).</summary>
        public bool minimapHidden = false;

        /// <summary>Radio output volume 0–1.</summary>
        public float radioVolume = 0.1f;

        /// <summary>Whether radio is tuned in (streaming enabled).</summary>
        public bool radioListening = false;

        /// <summary>Legacy — collapsible sections always start closed on join.</summary>
        public bool settingsSectionOpen = false;

        /// <summary>Legacy — collapsible sections always start closed on join.</summary>
        public bool keybindsSectionOpen = false;

        /// <summary>Legacy — collapsible sections always start closed on join.</summary>
        public bool radioInfoSectionOpen = false;

        /// <summary>When true, radio plays at full volume everywhere (intercom). When false, only near speakers.</summary>
        public bool radioPlayEverywhere = false;

        /// <summary>Max hear distance in meters for speaker-proximity radio (5–100).</summary>
        public float radioSpeakerRange = 72f;

        private static MultiSheetClientSettings current;
        private static bool loaded;
        private static bool dirty;
        private static string loadedFromPath;

        internal static bool DayNightEnabled
        {
            get { return Load().dayNightEnabled; }
            set { Load().dayNightEnabled = value; }
        }

        internal static float ManualHour
        {
            get { return Load().manualHour; }
            set { Load().manualHour = value; }
        }

        internal static bool RenderAllRinks
        {
            get { return Load().renderAllRinks; }
            set { Load().renderAllRinks = value; }
        }

        internal static bool AllowRinkChanges
        {
            get { return Load().allowRinkChanges; }
            set { Load().allowRinkChanges = value; }
        }

        internal static bool SkipChunkClient => Load().skipChunkClient;

        internal static bool SkipArenaLighting
        {
            get { return Load().skipArenaLighting; }
            set { Load().skipArenaLighting = value; }
        }

        internal static bool SkipClientBuild => Load().skipClientBuild;
        internal static bool SkipPracticeHud => Load().skipPracticeHud;
        internal static bool HideStockPucks => Load().hideStockPucks;
        internal static bool SkipScoreboardUi => Load().skipScoreboardUi;
        internal static bool SkipMotdUi => Load().skipMotdUi;
        internal static bool SkipMinimap => Load().skipMinimap;

        internal static string SpawnPuckKey
        {
            get
            {
                string key = Load().spawnPuckKey;
                return string.IsNullOrWhiteSpace(key) ? "R" : key.Trim();
            }
            set
            {
                Load().spawnPuckKey = string.IsNullOrWhiteSpace(value) ? "R" : value.Trim();
            }
        }

        internal static string ToggleRoleKey
        {
            get
            {
                string key = Load().toggleRoleKey;
                return string.IsNullOrWhiteSpace(key) ? "P" : key.Trim();
            }
            set
            {
                Load().toggleRoleKey = string.IsNullOrWhiteSpace(value) ? "P" : value.Trim();
            }
        }

        internal static string SlidableToggleKey
        {
            get
            {
                string key = Load().slidableToggleKey;
                return string.IsNullOrWhiteSpace(key) ? "L" : key.Trim();
            }
            set
            {
                Load().slidableToggleKey = string.IsNullOrWhiteSpace(value) ? "L" : value.Trim();
            }
        }

        internal static string MinimapToggleKey
        {
            get
            {
                string key = Load().minimapToggleKey;
                return string.IsNullOrWhiteSpace(key) ? "M" : key.Trim();
            }
            set
            {
                Load().minimapToggleKey = string.IsNullOrWhiteSpace(value) ? "M" : value.Trim();
            }
        }

        internal static bool MinimapHidden
        {
            get { return Load().minimapHidden; }
            set { Load().minimapHidden = value; }
        }

        internal static float RadioVolume
        {
            get
            {
                float v = Load().radioVolume;
                return Mathf.Clamp01(v <= 0f ? 0.1f : v);
            }
            set { Load().radioVolume = Mathf.Clamp01(value); }
        }

        internal static bool RadioListening
        {
            get { return Load().radioListening; }
            set { Load().radioListening = value; }
        }

        internal static bool RadioPlayEverywhere
        {
            get { return Load().radioPlayEverywhere; }
            set { Load().radioPlayEverywhere = value; }
        }

        internal static float RadioSpeakerRange
        {
            get
            {
                float v = Load().radioSpeakerRange;
                if (v < 5f) return 72f;
                return Mathf.Clamp(v, 5f, 100f);
            }
            set { Load().radioSpeakerRange = Mathf.Clamp(value, 5f, 100f); }
        }

        internal static void ResetJoinPresentationState()
        {
            presentationRefreshPending = false;
        }

        private static bool presentationRefreshPending;

        internal static string LoadedFromPath => Load() != null ? (loadedFromPath ?? ResolvePath()) : ResolvePath();

        /// <summary>Apply persisted client prefs after load or reconnect.</summary>
        internal static void ApplyLoadedPreferences()
        {
            Load();
            presentationRefreshPending = true;
            MinimapSessionOverride.ApplyPersistedPreference();
            RadioController.ApplyClientPreferencesFromMultiSheet();
            ApplyPresentationPreferences();
        }

        /// <summary>One-shot retry when client layout finishes after connect.</summary>
        internal static void TickDeferredJoinApply()
        {
            if (!presentationRefreshPending)
                return;

            ApplyPresentationPreferences();
        }

        private static void ApplyPresentationPreferences()
        {
            if (ModRuntimeContext.IsDedicatedGameServer)
                return;

            if (!ModRuntimeContext.ShouldInstallClientPresentation())
                return;

            if (!CustomLevelPlugin.IsClientLayoutReady)
                return;

            ArenaLighting.RefreshRinkLightCulling();
            presentationRefreshPending = false;
        }

        internal static MultiSheetClientSettings Load()
        {
            if (loaded) return current;
            loaded = true;
            current = new MultiSheetClientSettings();
            loadedFromPath = ResolvePath();

            try
            {
                if (File.Exists(loadedFromPath))
                {
                    string raw = File.ReadAllText(loadedFromPath);
                    var parsed = JsonConvert.DeserializeObject<MultiSheetClientSettings>(raw);
                    if (parsed != null)
                    {
                        current = parsed;
                        MigrateLegacyPlayerPrefs(current, raw);
                    }
                }
                else
                {
                    MigrateLegacyPlayerPrefs(current, null);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Failed to read " + loadedFromPath + ": " + ex.Message);
            }

            // Always log once so A/B mistakes (wrong file / Flush clobber / old DLL) are obvious.
            Debug.Log("[PHLPractice] Client settings from " + loadedFromPath +
                      " (exists=" + File.Exists(loadedFromPath) + ")" +
                      " skipMotdUi=" + current.skipMotdUi +
                      " skipScoreboardUi=" + current.skipScoreboardUi +
                      " skipArenaLighting=" + current.skipArenaLighting +
                      " skipClientBuild=" + current.skipClientBuild +
                      " skipChunkClient=" + current.skipChunkClient +
                      " skipPracticeHud=" + current.skipPracticeHud +
                      " hideStockPucks=" + current.hideStockPucks +
                      " renderAllRinks=" + current.renderAllRinks);

            return current;
        }

        /// <summary>
        /// Queue a write. Dragging the time slider fires a change event per frame, so the
        /// actual file write is deferred to <see cref="Flush"/> on the lighting tick.
        /// </summary>
        internal static void Save()
        {
            if (loaded) dirty = true;
        }

        internal static void Flush()
        {
            if (!dirty) return;
            dirty = false;
            try
            {
                string path = ResolvePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(current, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Failed to write " + RelativePath + ": " + ex.Message);
            }
        }

        private static void MigrateLegacyPlayerPrefs(MultiSheetClientSettings settings, string rawJson)
        {
            if (settings == null) return;

            bool hasRadioVolume = rawJson != null && rawJson.IndexOf("\"radioVolume\"", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasRadioListening = rawJson != null && rawJson.IndexOf("\"radioListening\"", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!hasRadioVolume && PlayerPrefs.HasKey("FlamiePrac_RadioVolume"))
            {
                settings.radioVolume = PlayerPrefs.GetFloat("FlamiePrac_RadioVolume", 0.1f);
                dirty = true;
            }

            if (!hasRadioListening && PlayerPrefs.HasKey("FlamiePrac_RadioListening"))
            {
                settings.radioListening = PlayerPrefs.GetInt("FlamiePrac_RadioListening", 0) != 0;
                dirty = true;
            }
        }

        private static string ResolvePath()
        {
            // Game working directory (Steam Puck install root) — same place other client
            // configs already live for this mod.
            return Path.GetFullPath(Path.Combine(
                Directory.GetCurrentDirectory(),
                RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
