using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Client-side environment lighting for the multi-sheet layout, in two modes.
    ///
    /// <para><b>Fixed indoor</b> (day/night off): a neutral, bright, time-independent
    /// arena environment.</para>
    ///
    /// <para><b>Day/night</b> (default): the sun, the ambient and the procedural skybox
    /// are all driven from one hour-of-day value, so the sky and the ice agree. The
    /// vendored PuckLargeLevel driver (LevelDayNightCycle) only moved the sun and the
    /// ambient — it never touched the skybox, so at 1am the scene kept a bright noon sky
    /// over a blue-black rink. It also let the sheets go dark: rink 1 hides that behind
    /// baked lightmaps, but rinks 2-9 are lit purely at runtime, so they fell away at
    /// every hour of the day. Both are fixed here — the skybox is modulated in lockstep,
    /// and the cloned sheets' fill rig is boosted as daylight drops so all nine sheets
    /// hold rink 1's baked brightness.</para>
    ///
    /// The skybox baseline is re-read whenever something else replaces
    /// <c>RenderSettings.skybox</c>, which is exactly what ToastersReskinLoader does when
    /// its Skybox sliders move (it clones a fresh material every time). That keeps the
    /// user's TRL atmosphere/tint/exposure choices as our noon reference instead of
    /// fighting them.
    /// </summary>
    internal static class ArenaLighting
    {
        /// <summary>Ambient used when the day/night system is off — softer than noon so
        /// the fixed indoor look does not wash out the ice and players.</summary>
        private static readonly Color IndoorAmbient = new Color(0.40f, 0.40f, 0.39f, 1f);
        private static readonly Color IndoorSun = new Color(1f, 0.98f, 0.95f, 1f);
        private const float IndoorSunIntensity = 0.72f;
        private static readonly Quaternion IndoorSunRotation = Quaternion.Euler(52f, 150f, 0f);

        // Day/night key frames.
        //
        private static readonly Color NightAmbient = new Color(0.30f, 0.31f, 0.35f, 1f);
        private static readonly Color DayAmbient = new Color(0.62f, 0.62f, 0.60f, 1f);
        private static readonly Color NightSun = new Color(0.55f, 0.62f, 0.82f, 1f);
        private static readonly Color HorizonSun = new Color(1f, 0.72f, 0.45f, 1f);
        private static readonly Color DaySun = new Color(1f, 0.97f, 0.92f, 1f);
        private const float NightSunIntensity = 0.35f;
        private const float DaySunIntensity = 1.15f;

        /// <summary>Fill multiplier at midnight — modest; clones already get SH ambient.</summary>
        private const float NightFillBoost = 1.45f;

        /// <summary>
        /// DrawMesh clones get this fraction of <see cref="Ambient"/> as SH L0. Full ambient
        /// + sun + fills washed rinks 2–9 brighter than rink 1's baked lightmaps.
        /// </summary>
        internal const float CloneAmbientScale = 0.48f;

        private static bool captured;
        /// <summary>True while Limit Rink Changes is active — no day/night or glare writes.</summary>
        private static bool stockLook;
        private static AmbientMode originalAmbientMode;
        private static Color originalAmbientLight;
        private static Color originalAmbientSky;
        private static Color originalAmbientEquator;
        private static Color originalAmbientGround;
        private static float originalAmbientIntensity;
        private static float originalReflectionIntensity;
        /// <summary>User/TRL-authored reflection level; 0 means reflections off.</summary>
        private static float reflectionUserBaseline = -1f;
        private static Material originalSkybox;
        private static GameObject enforcerObject;

        private static readonly List<RinkLight> rinkLights = new List<RinkLight>();
        private static readonly Dictionary<int, SunSnapshot> sunSnapshots = new Dictionary<int, SunSnapshot>();
        private static readonly List<Light> directionalCache = new List<Light>(4);

        /// <summary>How often to re-Find directional lights (not every enforcer tick).</summary>
        private const float SunCacheRefreshSeconds = 10f;
        private static float nextSunCacheRefresh;

        private static Material skyboxInstance;
        private static Material skyboxTracked;
        private static SkyboxBaseline skyBaseline;
        private static bool skyBaselineValid;

        /// <summary>Ambient currently in force — read by the clone draw path.</summary>
        internal static Color Ambient { get; private set; } = IndoorAmbient;

        /// <summary>True after <see cref="Apply"/> until <see cref="Restore"/>.</summary>
        internal static bool IsActive => captured;

        /// <summary>True when MultiSheet cosmetics are suspended (Limit Rink Changes).</summary>
        internal static bool IsStockLook => stockLook;

        internal static bool DayNightEnabled
        {
            get { return MultiSheetClientSettings.DayNightEnabled; }
        }

        /// <summary>True when the user pinned an hour instead of following the system clock.</summary>
        internal static bool IsManualHour
        {
            get { return MultiSheetClientSettings.ManualHour >= 0f; }
        }

        /// <summary>Hour of day (0-24) currently driving the lighting.</summary>
        internal static float Hour
        {
            get
            {
                float manual = MultiSheetClientSettings.ManualHour;
                if (manual >= 0f) return Mathf.Repeat(manual, 24f);
                return (float)DateTime.Now.TimeOfDay.TotalHours;
            }
        }

        /// <summary>
        /// The per-sheet realtime light rig: cloned arena fixtures plus the synthetic
        /// overhead fills. Independent of the day/night sun/sky cycle — either, both, or
        /// neither may be enabled.
        /// </summary>
        internal static bool ArenaLightingEnabled => !MultiSheetClientSettings.SkipArenaLighting;

        internal static void SetArenaLightingEnabled(bool enabled)
        {
            MultiSheetClientSettings.SkipArenaLighting = !enabled;
            MultiSheetClientSettings.Save();
            if (!enabled) SetAllRinkLightsEnabled(false);
            Apply();
            RefreshRinkLightCulling();
            PracticeLog.Info("[PHLPractice] Arena lighting " + (enabled ? "ON" : "OFF") + " — " +
                      rinkLights.Count + " clone light(s) " +
                      (enabled ? "culled to the local sheet" : "disabled") + ".");
        }

        internal static void SetDayNightEnabled(bool enabled)
        {
            MultiSheetClientSettings.DayNightEnabled = enabled;
            MultiSheetClientSettings.Save();
            Apply();
        }

        /// <summary>Pin the hour, or pass a negative value to follow the local clock again.</summary>
        internal static void SetManualHour(float hour)
        {
            MultiSheetClientSettings.ManualHour = hour < 0f ? -1f : Mathf.Repeat(hour, 24f);
            MultiSheetClientSettings.Save();
            ApplyEnvironment();
            SyncEnforcer();
        }

        /// <summary>Format an hour-of-day as 24h clock text.</summary>
        internal static string FormatHour(float hour)
        {
            float wrapped = Mathf.Repeat(hour, 24f);
            int h = Mathf.FloorToInt(wrapped);
            int m = Mathf.FloorToInt((wrapped - h) * 60f);
            if (m >= 60) { m = 0; h = (h + 1) % 24; }
            return h.ToString("00") + ":" + m.ToString("00");
        }

        /// <summary>Apply the environment and keep it applied.</summary>
        internal static void Apply()
        {
            MultiSheetClientSettings.Load();

            bool skipFills = MultiSheetClientSettings.SkipArenaLighting;
            bool dayNight = DayNightEnabled;

            if (skipFills)
                SetAllRinkLightsEnabled(false);

            // Neither toggle — hand sun/sky/ambient back to stock; clone fills stay off.
            if (skipFills && !dayNight)
            {
                if (captured)
                {
                    RestoreDirectionalLights();
                    ReleaseSkybox();
                    RestoreCapturedRenderSettings();
                    Ambient = RenderSettings.ambientLight;
                }
                SyncEnforcer();
                PracticeLog.Info("[PHLPractice] Arena lighting and day/night off — stock sun/sky/ambient, clone fills off.");
                return;
            }

            CaptureOriginal();
            DisableDayNightCycles();

            if (MultiSheetClientSettings.AllowRinkChanges)
            {
                stockLook = false;
                ApplyEnvironment();
                TrlPracticeSmoothnessOverride.Apply();
                // ArenaGlare skipped: FindObjectsByType(ReflectionProbe) + ice material
                // walks were a steady client tax; helmet glare is acceptable vs ~50+ FPS.
            }
            else
            {
                stockLook = true;
                if (!skipFills)
                    ApplyRinkFill(1f);
            }

            SyncEnforcer();

            PracticeLog.Info("[PHLPractice] Arena lighting applied — " +
                      (stockLook
                          ? "stock look (Limit Rink Changes)"
                          : ("fills " + (skipFills ? "off" : "on") + ", " +
                             (dayNight
                                 ? ("day/night @" + FormatHour(Hour) + (IsManualHour ? " (pinned)" : " (local clock)"))
                                 : "fixed indoor"))) +
                      " (" + rinkLights.Count + " light(s)), enforcer=" +
                      (enforcerObject != null));
        }

        /// <summary>
        /// Suspend MultiSheet cosmetics and hand sky/sun/ambient/glare back — fill lights
        /// stay (clones still need them). Used by Limit Rink Changes.
        /// </summary>
        internal static void EnterStockLook()
        {
            stockLook = true;
            ArenaGlare.Restore();
            RestoreDirectionalLights();
            ReleaseSkybox();
            RestoreCapturedRenderSettings();
            Ambient = RenderSettings.ambientLight;
            ApplyRinkFill(1f);
        }

        /// <summary>Re-enable MultiSheet day/night after Limit Rink Changes.</summary>
        internal static void ExitStockLook()
        {
            stockLook = false;
            if (!captured)
            {
                Apply();
                return;
            }
            ApplyEnvironment();
            SyncEnforcer();
        }

        private static void SyncEnforcer()
        {
            // Clock follow only needs day/night — not the clone fill rig.
            bool want = !stockLook
                        && MultiSheetClientSettings.AllowRinkChanges
                        && DayNightEnabled
                        && !IsManualHour;
            if (want)
            {
                if (enforcerObject == null)
                {
                    enforcerObject = new GameObject("PHL_ArenaLighting");
                    UnityEngine.Object.DontDestroyOnLoad(enforcerObject);
                    enforcerObject.hideFlags = HideFlags.HideAndDontSave;
                    enforcerObject.AddComponent<ArenaLightingEnforcer>();
                }
            }
            else
                TearDownEnforcer();
        }

        private static void TearDownEnforcer()
        {
            if (enforcerObject == null) return;
            UnityEngine.Object.Destroy(enforcerObject);
            enforcerObject = null;
        }

        /// <summary>Hand the scene back to the game — the local player left the practice server.</summary>
        internal static void Restore()
        {
            TearDownEnforcer();

            ArenaGlare.Restore();

            ClearRinkLights();
            RestoreDirectionalLights();
            ReleaseSkybox();
            MultiSheetClientSettings.Flush();
            Ambient = IndoorAmbient;
            stockLook = false;

            if (!captured) return;
            RestoreCapturedRenderSettings();
            reflectionUserBaseline = -1f;
            captured = false;
        }

        private static void RestoreCapturedRenderSettings()
        {
            if (!captured) return;
            RenderSettings.ambientMode = originalAmbientMode;
            RenderSettings.ambientLight = originalAmbientLight;
            RenderSettings.ambientSkyColor = originalAmbientSky;
            RenderSettings.ambientEquatorColor = originalAmbientEquator;
            RenderSettings.ambientGroundColor = originalAmbientGround;
            RenderSettings.ambientIntensity = originalAmbientIntensity;
            RenderSettings.reflectionIntensity = originalReflectionIntensity;
        }

        internal static void ClearRinkLights()
        {
            rinkLights.Clear();
        }

        /// <summary>
        /// Register a light that lives on a cloned sheet. Its authored intensity is the
        /// noon value; it is scaled up as daylight falls so the clone keeps pace with
        /// rink 1's baked lighting. Only the local player's sheet (plus MOTD preview
        /// sheets) stay enabled — eight sheets of realtime point lights kill FPS.
        /// </summary>
        internal static void RegisterRinkLight(Light light, Vector3 rinkOrigin)
        {
            if (light == null) return;
            rinkLights.Add(new RinkLight
            {
                Light = light,
                BaseIntensity = light.intensity,
                OriginX = rinkOrigin.x,
                OriginZ = rinkOrigin.z
            });
            RefreshRinkLightCulling();
        }

        /// <summary>Re-assert the whole environment. Cheap enough to call several times a second.</summary>
        internal static void ApplyEnvironment()
        {
            if (stockLook || !MultiSheetClientSettings.AllowRinkChanges) return;

            if (!DayNightEnabled)
            {
                ApplyFixedIndoor();
                return;
            }

            float hour = Hour;
            // sin peaks at noon, troughs at midnight — a stand-in for solar altitude.
            float altitude = Mathf.Sin((hour - 6f) / 12f * Mathf.PI);
            float dayT = Mathf.Clamp01((altitude + 0.18f) / 0.50f);
            // Peaks while the sun is near the horizon, for the sunrise/sunset warmth.
            float horizonT = Mathf.Clamp01(1f - Mathf.Abs(altitude) / 0.35f) * Mathf.Clamp01(dayT * 3f);

            Color sunColor = Color.Lerp(NightSun, DaySun, dayT);
            sunColor = Color.Lerp(sunColor, HorizonSun, horizonT * 0.85f);
            float sunIntensity = Mathf.Lerp(NightSunIntensity, DaySunIntensity, dayT);

            // 06:00 sunrise in the east, 18:00 sunset in the west. After dusk the single
            // directional light stands in for the moon, so it swings a half-day ahead —
            // otherwise it points up out of the arena and nothing is lit at all at night.
            float lightHour = altitude >= 0f ? hour : hour + 12f;
            Quaternion sunRotation = Quaternion.Euler((lightHour - 6f) / 24f * 360f, 170f, 0f);

            Ambient = Color.Lerp(NightAmbient, DayAmbient, dayT);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Ambient;
            RenderSettings.ambientIntensity = 1f;
            ApplyReflectionFromBaseline(Mathf.Lerp(0.5f, 1f, dayT));

            ApplyToDirectionalLights(sunColor, sunIntensity, sunRotation);
            ApplySkybox(dayT, horizonT);
            ApplyRinkFill(dayT);
        }

        private static void ApplyFixedIndoor()
        {
            Ambient = IndoorAmbient;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = IndoorAmbient;
            RenderSettings.ambientIntensity = 1f;
            ApplyReflectionFromBaseline(1f);

            ApplyToDirectionalLights(IndoorSun, IndoorSunIntensity, IndoorSunRotation);
            // Fixed indoor has no time of day, so hand the sky straight back — whatever
            // the user set in TRL's Skybox panel applies unmodified.
            ReleaseSkybox();
            ApplyRinkFill(1f);
        }

        /// <summary>
        /// Directional lights have no position, so every sheet already receives the same
        /// sun; only its colour, intensity and angle need normalising. The scene also has
        /// dozens of clone fill point lights — never FindObjectsByType&lt;Light&gt; every tick.
        /// </summary>
        private static void ApplyToDirectionalLights(Color color, float intensity, Quaternion rotation)
        {
            RefreshDirectionalCacheIfNeeded();

            for (int i = directionalCache.Count - 1; i >= 0; i--)
            {
                Light light = directionalCache[i];
                if (light == null)
                {
                    directionalCache.RemoveAt(i);
                    continue;
                }

                // Snapshot before the first write so Restore can hand the scene's own sun
                // back untouched — disabling the mod mid-session must not strand the level
                // on whatever hour was pinned at the time.
                int id = light.GetInstanceID();
                if (!sunSnapshots.ContainsKey(id))
                {
                    sunSnapshots[id] = new SunSnapshot
                    {
                        Light = light,
                        Color = light.color,
                        Intensity = light.intensity,
                        Rotation = light.transform.rotation
                    };
                }

                if (light.color != color) light.color = color;
                if (!Mathf.Approximately(light.intensity, intensity)) light.intensity = intensity;
                if (light.transform.rotation != rotation) light.transform.rotation = rotation;
            }
        }

        private static void RefreshDirectionalCacheIfNeeded()
        {
            if (directionalCache.Count > 0 && Time.unscaledTime < nextSunCacheRefresh)
                return;

            nextSunCacheRefresh = Time.unscaledTime + SunCacheRefreshSeconds;
            directionalCache.Clear();

            Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light != null && light.type == LightType.Directional)
                    directionalCache.Add(light);
            }
        }

        private static void RestoreDirectionalLights()
        {
            foreach (KeyValuePair<int, SunSnapshot> kv in sunSnapshots)
            {
                SunSnapshot snapshot = kv.Value;
                if (snapshot.Light == null) continue;
                snapshot.Light.color = snapshot.Color;
                snapshot.Light.intensity = snapshot.Intensity;
                snapshot.Light.transform.rotation = snapshot.Rotation;
            }
            sunSnapshots.Clear();
            directionalCache.Clear();
            nextSunCacheRefresh = 0f;
        }

        /// <summary>
        /// Modulate the procedural skybox around the baseline someone else authored, so
        /// the sky darkens with the sun instead of staying at noon. Writing in place on
        /// our own clone means TRL keeps a clean original to re-clone from.
        /// </summary>
        private static void ApplySkybox(float dayT, float horizonT)
        {
            Material active = RenderSettings.skybox;
            if (active == null) return;

            // Anything other than our own instance is a fresh authored baseline (TRL
            // rebuilds its material from scratch on every slider change).
            if (!ReferenceEquals(active, skyboxInstance) && !ReferenceEquals(active, skyboxTracked))
            {
                skyboxTracked = active;
                if (originalSkybox == null) originalSkybox = active;
                skyBaselineValid = SkyboxBaseline.TryRead(active, out skyBaseline);
                if (!skyBaselineValid) return;

                if (skyboxInstance != null) UnityEngine.Object.Destroy(skyboxInstance);
                skyboxInstance = new Material(active);
                skyboxInstance.hideFlags = HideFlags.HideAndDontSave;
                RenderSettings.skybox = skyboxInstance;
            }

            if (!skyBaselineValid || skyboxInstance == null) return;

            float exposure = skyBaseline.Exposure * Mathf.Lerp(0.08f, 1f, dayT);
            // A thicker atmosphere near the horizon is what reddens dawn and dusk.
            float thickness = skyBaseline.Thickness * Mathf.Lerp(1f, 1.75f, horizonT);
            Color tint = Color.Lerp(skyBaseline.SkyTint * 0.35f, skyBaseline.SkyTint, dayT);
            Color ground = Color.Lerp(skyBaseline.GroundColor * 0.25f, skyBaseline.GroundColor, dayT);

            skyboxInstance.SetFloat("_Exposure", exposure);
            skyboxInstance.SetFloat("_AtmosphereThickness", thickness);
            skyboxInstance.SetColor("_SkyTint", tint);
            skyboxInstance.SetColor("_GroundColor", ground);
        }

        /// <summary>
        /// Drop our modulated clone. The material we cloned from is preferred over the
        /// one present at startup, so a TRL skybox edit made while day/night was running
        /// survives switching back to fixed indoor.
        /// </summary>
        private static void ReleaseSkybox()
        {
            if (skyboxInstance == null) return;

            if (ReferenceEquals(RenderSettings.skybox, skyboxInstance))
                RenderSettings.skybox = skyboxTracked != null ? skyboxTracked : originalSkybox;

            UnityEngine.Object.Destroy(skyboxInstance);
            skyboxInstance = null;
            // Forget the source too, so re-enabling day/night re-reads a fresh baseline.
            skyboxTracked = null;
            skyBaselineValid = false;
        }

        /// <summary>
        /// Rink 1's baked lightmaps do not dim at night, so the clone rig has to make up
        /// the difference or the offset sheets read as black while rink 1 stays lit.
        /// </summary>
        private static void ApplyRinkFill(float dayT)
        {
            if (rinkLights.Count == 0) return;

            // User / kill-switch: keep every clone fill off. Cost is enabled lights, not
            // the disabled Light components sitting in the hierarchy.
            if (MultiSheetClientSettings.SkipArenaLighting)
            {
                SetAllRinkLightsEnabled(false);
                return;
            }

            float boost = Mathf.Lerp(NightFillBoost, 1f, dayT);
            // Same policy as CloneVisualProxy / mirrors (RinkRenderFocus).
            bool lightAll = RinkRenderFocus.RenderAll;
            float focusX = 0f, focusZ = 0f;
            bool haveFocus = !lightAll && RinkRenderFocus.TryGetGameplayFocus(out focusX, out focusZ);
            float liveX = 0f, liveZ = 0f;
            bool haveLive = !lightAll && RinkPreview.TryGetLivePreviewOrigin(out liveX, out liveZ);

            for (int i = rinkLights.Count - 1; i >= 0; i--)
            {
                Light light = rinkLights[i].Light;
                if (light == null)
                {
                    rinkLights.RemoveAt(i);
                    continue;
                }

                float target = rinkLights[i].BaseIntensity * boost;
                if (!Mathf.Approximately(light.intensity, target)) light.intensity = target;

                // Chunk-local lighting: only the client's current sheet (plus live preview).
                bool on = lightAll
                    || (haveFocus && SameRink(rinkLights[i].OriginX, rinkLights[i].OriginZ, focusX, focusZ))
                    || (haveLive && SameRink(rinkLights[i].OriginX, rinkLights[i].OriginZ, liveX, liveZ))
                    || RinkPreview.IsOriginInCapture(rinkLights[i].OriginX, rinkLights[i].OriginZ);
                if (light.enabled != on) light.enabled = on;
            }
        }

        private static void SetAllRinkLightsEnabled(bool enabled)
        {
            for (int i = rinkLights.Count - 1; i >= 0; i--)
            {
                Light light = rinkLights[i].Light;
                if (light == null)
                {
                    rinkLights.RemoveAt(i);
                    continue;
                }
                if (light.enabled != enabled) light.enabled = enabled;
            }
        }

        internal static void RefreshRinkLightCulling()
        {
            // No registered fill lights yet (still loading / torn down).
            // Do not require CaptureOriginal — RegisterRinkLight runs before Apply(), and
            // skipArenaLighting never captures; culling must still disable fills.
            if (rinkLights.Count == 0) return;
            if (MultiSheetClientSettings.SkipArenaLighting)
            {
                SetAllRinkLightsEnabled(false);
                return;
            }
            // dayT from current mode — ApplyRinkFill already has the full path.
            if (!DayNightEnabled)
            {
                ApplyRinkFill(1f);
                return;
            }
            float hour = Hour;
            float altitude = Mathf.Sin((hour - 6f) / 12f * Mathf.PI);
            float dayT = Mathf.Clamp01((altitude + 0.18f) / 0.50f);
            ApplyRinkFill(dayT);
        }

        internal static bool SameRink(float ax, float az, float bx, float bz)
        {
            float dx = ax - bx;
            float dz = az - bz;
            return dx * dx + dz * dz < 1f;
        }

        /// <summary>
        /// TRL just wrote reflection intensity (often 0 when the user disables glass
        /// reflections). Re-read before ApplyEnvironment so we do not clobber that choice.
        /// </summary>
        internal static void SyncReflectionBaselineFromScene()
        {
            reflectionUserBaseline = RenderSettings.reflectionIntensity;
        }

        private static void ApplyReflectionFromBaseline(float dayNightMul)
        {
            EnsureReflectionBaseline();
            if (reflectionUserBaseline <= 0.0001f)
            {
                RenderSettings.reflectionIntensity = 0f;
                return;
            }

            RenderSettings.reflectionIntensity = reflectionUserBaseline * Mathf.Clamp01(dayNightMul);
        }

        private static void EnsureReflectionBaseline()
        {
            if (reflectionUserBaseline >= 0f) return;
            reflectionUserBaseline = captured
                ? originalReflectionIntensity
                : RenderSettings.reflectionIntensity;
        }

        private static void CaptureOriginal()
        {
            if (captured) return;
            captured = true;

            originalAmbientMode = RenderSettings.ambientMode;
            originalAmbientLight = RenderSettings.ambientLight;
            originalAmbientSky = RenderSettings.ambientSkyColor;
            originalAmbientEquator = RenderSettings.ambientEquatorColor;
            originalAmbientGround = RenderSettings.ambientGroundColor;
            originalAmbientIntensity = RenderSettings.ambientIntensity;
            originalReflectionIntensity = RenderSettings.reflectionIntensity;
            reflectionUserBaseline = originalReflectionIntensity;
            originalSkybox = RenderSettings.skybox;
        }

        /// <summary>
        /// Remove the vendored outdoor driver — it fights us for the sun every 30s and
        /// has no idea the skybox exists.
        /// </summary>
        private static void DisableDayNightCycles()
        {
            LevelDayNightCycle[] cycles = UnityEngine.Object.FindObjectsByType<LevelDayNightCycle>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < cycles.Length; i++)
            {
                if (cycles[i] == null) continue;
                UnityEngine.Object.Destroy(cycles[i]);
            }
            if (cycles.Length > 0)
                PracticeLog.Info("[PHLPractice] Replaced " + cycles.Length + " vendored day/night driver(s) with the MultiSheet cycle.");
        }

        private struct RinkLight
        {
            public Light Light;
            public float BaseIntensity;
            public float OriginX;
            public float OriginZ;
        }

        /// <summary>A scene directional light as we first found it.</summary>
        private struct SunSnapshot
        {
            public Light Light;
            public Color Color;
            public float Intensity;
            public Quaternion Rotation;
        }

        /// <summary>Authored values of a Skybox/Procedural material, used as our noon reference.</summary>
        private struct SkyboxBaseline
        {
            public float Exposure;
            public float Thickness;
            public Color SkyTint;
            public Color GroundColor;

            internal static bool TryRead(Material material, out SkyboxBaseline baseline)
            {
                baseline = default(SkyboxBaseline);
                if (material == null) return false;
                if (!material.HasProperty("_Exposure") || !material.HasProperty("_SkyTint")) return false;

                baseline.Exposure = material.GetFloat("_Exposure");
                baseline.Thickness = material.HasProperty("_AtmosphereThickness")
                    ? material.GetFloat("_AtmosphereThickness")
                    : 1f;
                baseline.SkyTint = material.GetColor("_SkyTint");
                baseline.GroundColor = material.HasProperty("_GroundColor")
                    ? material.GetColor("_GroundColor")
                    : new Color(0.369f, 0.349f, 0.341f, 1f);
                return true;
            }
        }
    }

    /// <summary>
    /// Auto day/night clock follow only (created when that mode is active). Light culling
    /// is event-driven from RegisterRinkLight / client build / render-scope toggle.
    /// </summary>
    internal sealed class ArenaLightingEnforcer : MonoBehaviour
    {
        private const float ClockFollowSeconds = 60f;
        private float nextClockFollow;

        private void LateUpdate()
        {
            MultiSheetClientSettings.Flush();
            if (ArenaLighting.IsStockLook || !MultiSheetClientSettings.AllowRinkChanges) return;
            if (!ArenaLighting.DayNightEnabled || ArenaLighting.IsManualHour) return;
            if (Time.unscaledTime < nextClockFollow) return;
            nextClockFollow = Time.unscaledTime + ClockFollowSeconds;
            ArenaLighting.ApplyEnvironment();
        }
    }

    /// <summary>
    /// Deterministic flat ambient for GPU-drawn clone geometry.
    ///
    /// Clone sheets are drawn with Graphics.DrawMesh, which cannot carry lightmaps, and
    /// they sit far outside rink 1's baked light-probe volume — probe extrapolation out
    /// there returns near-black. Feeding explicit L0 spherical-harmonic constants makes
    /// every clone sample a controlled ambient instead. Scaled below full arena ambient
    /// so sun + fill lights do not stack into a brighter look than rink 1's lightmaps.
    /// </summary>
    internal static class AmbientProbeBlock
    {
        private static MaterialPropertyBlock block;
        private static Color applied;

        internal static MaterialPropertyBlock Get()
        {
            Color source = ArenaLighting.Ambient;
            if (block == null || applied != source)
            {
                Color c = source * ArenaLighting.CloneAmbientScale;
                applied = source;
                block = block ?? new MaterialPropertyBlock();

                // URP's SampleSH evaluates dot(unity_SHAr, float4(normal, 1)); putting the
                // colour in .w and zeroing the rest yields a constant (flat) ambient.
                block.SetVector("unity_SHAr", new Vector4(0f, 0f, 0f, c.r));
                block.SetVector("unity_SHAg", new Vector4(0f, 0f, 0f, c.g));
                block.SetVector("unity_SHAb", new Vector4(0f, 0f, 0f, c.b));
                block.SetVector("unity_SHBr", Vector4.zero);
                block.SetVector("unity_SHBg", Vector4.zero);
                block.SetVector("unity_SHBb", Vector4.zero);
                block.SetVector("unity_SHC", Vector4.zero);
            }
            return block;
        }
    }
}
