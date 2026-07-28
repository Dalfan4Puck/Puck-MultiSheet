using UnityEngine;
using Object = UnityEngine.Object;

using Unity.Netcode;

using System;

using System.IO;

using System.Collections.Generic;

using System.Linq;

using System.Reflection;

using HarmonyLib;



namespace MyMod

{

    /// <summary>
    /// FlamiePrac core. Standalone builds expose IPuckPlugin; MultiSheet hosts this type.
    /// </summary>
#if FLAMIE_STANDALONE
    public class Class1 : IPuckPlugin
#else
    public class Class1
#endif
    {

        private static readonly Harmony harmony =

            new Harmony("Flamie.TrainingMod");



        public static Class1 Instance { get; private set; }



        private readonly List<AssetBundle> bundles = new();

        private readonly Dictionary<string, GameObject> loadedPrefabs = new();

        private GameObject managerObject;

        private bool harmonyPatched;





        // All prefab names to load from the bundle

        private static readonly string[] PrefabNames = new string[]

        {

            "TrainingPrefab",

            "Goaltarp",

            "WallTarget",

            "halfrinkhockey"

        };



        public bool OnEnable()

        {

            Instance = this;

            harmonyPatched = false;



            try

            {

                string modPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

                UnloadBundles(false);



                List<string> bundleCandidates = new List<string>();

                string primaryBundlePath = Path.Combine(modPath, "trainingprefabs");

                if (File.Exists(primaryBundlePath))

                    bundleCandidates.Add(primaryBundlePath);



                foreach (string file in Directory.GetFiles(modPath))

                {

                    string name = Path.GetFileName(file);

                    string extension = Path.GetExtension(file).ToLowerInvariant();

                    bool isBundleCandidate =

                        name.StartsWith("trainingprefabs", StringComparison.OrdinalIgnoreCase) ||

                        extension == ".bundle" ||

                        extension == ".assetbundle" ||

                        extension == ".unity3d";



                    if (!isBundleCandidate)

                        continue;



                    if (!bundleCandidates.Contains(file))

                        bundleCandidates.Add(file);

                }



                if (bundleCandidates.Count == 0)

                {

                    FlamieLog.Error("[FlamiePrac] No bundle files found. Expected at least: " + primaryBundlePath);

                    RollbackEnable();

                    return false;

                }



                foreach (string bundlePath in bundleCandidates)

                {

                    try

                    {

                        AssetBundle loadedBundle = AssetBundle.LoadFromFile(bundlePath);

                        if (loadedBundle == null)

                        {

                            FlamieLog.Warn("[FlamiePrac] Failed to load bundle candidate: " + bundlePath);

                            continue;

                        }



                        bundles.Add(loadedBundle);

                        string[] assetNames = loadedBundle.GetAllAssetNames();

                        FlamieLog.Info($"[FlamiePrac] Loaded bundle '{Path.GetFileName(bundlePath)}' with {assetNames.Length} asset(s)");

                        foreach (string assetName in assetNames)

                            FlamieLog.Info($"[FlamiePrac]   Asset: {assetName}");

                    }

                    catch (Exception ex)

                    {

                        FlamieLog.Warn($"[FlamiePrac] Error loading bundle candidate '{bundlePath}': {ex.Message}");

                    }

                }



                if (bundles.Count == 0)

                {

                    FlamieLog.Error("[FlamiePrac] No valid AssetBundles could be loaded.");

                    RollbackEnable();

                    return false;

                }



                loadedPrefabs.Clear();

                foreach (AssetBundle loadedBundle in bundles)

                {

                    foreach (string prefabName in PrefabNames)

                    {

                        try

                        {

                            GameObject prefab = loadedBundle.LoadAsset<GameObject>(prefabName);

                            if (prefab != null)

                                RegisterPrefab(prefabName, prefab);

                        }

                        catch (Exception ex)

                        {

                            FlamieLog.Warn($"[FlamiePrac] Could not load prefab '{prefabName}': {ex.Message}");

                        }

                    }

                }



                foreach (AssetBundle loadedBundle in bundles)

                {

                    try

                    {

                        GameObject[] allPrefabs = loadedBundle.LoadAllAssets<GameObject>();

                        foreach (GameObject prefab in allPrefabs)

                            RegisterPrefab(prefab.name, prefab);

                    }

                    catch (Exception ex)

                    {

                        FlamieLog.Warn($"[FlamiePrac] Failed to LoadAllAssets<GameObject>: {ex.Message}");

                    }



                    try

                    {

                        string[] allAssetNames = loadedBundle.GetAllAssetNames();

                        foreach (string assetName in allAssetNames)

                        {

                            GameObject prefab = loadedBundle.LoadAsset<GameObject>(assetName);

                            if (prefab != null)

                            {

                                string key = Path.GetFileNameWithoutExtension(assetName);

                                RegisterPrefab(key, prefab);

                            }

                        }

                    }

                    catch { }

                }



                if (loadedPrefabs.Count == 0)

                {

                    FlamieLog.Error("[FlamiePrac] No prefabs found in AssetBundle!");

                    RollbackEnable();

                    return false;

                }



                TrainingMaterialFix.Initialize(bundles);



                managerObject = new GameObject("FlamiePrac_Bootstrap");

                managerObject.AddComponent<TrainingSync>();
                if (FlamiePracFeatures.EnableRadio)
                    managerObject.AddComponent<RadioController>();
                managerObject.AddComponent<FlamiePracGoalieBootstrap>();
                // MultiSheet already owns R-key puck spawn (PuckSpawnSync). Only wire
                // Flamie's duplicate when running as a standalone MyMod.dll.
                if (!Application.isBatchMode && !IsHostedInsideMultiSheet())
                    managerObject.AddComponent<FlamiePracTestPuckSpawn>();

                UnityEngine.Object.DontDestroyOnLoad(managerObject);

                FlamieLog.InfoOnce("bootstrap", "[FlamiePrac] TrainingSync bootstrap created. " + FlamiePracVersion.Banner);



                // Never PatchAll the whole assembly — when compiled into MultiSheet.dll that
                // would double-apply PHLPractice Harmony patches already installed separately.
                PatchFlamieHarmonyOnly();

                harmonyPatched = true;



                FlamieLog.InfoOnce("flamie-enabled", $"[FlamiePrac] Enabled with {loadedPrefabs.Count} prefab(s) across {bundles.Count} bundle(s). " +

                    $"Dedicated={Application.isBatchMode} " + FlamiePracVersion.Banner);

                return true;

            }

            catch (Exception ex)

            {

                FlamieLog.Error("[FlamiePrac] Failed to enable: " + ex);

                RollbackEnable();

                return false;

            }

        }



        public bool OnDisable()

        {

            try

            {

                FlamiePracLifecycle.Shutdown();



                if (harmonyPatched)

                {

                    harmony.UnpatchSelf();

                    harmonyPatched = false;

                }



                if (managerObject != null)

                {

                    UnityEngine.Object.Destroy(managerObject);

                    managerObject = null;

                }



                loadedPrefabs.Clear();

                UnloadBundles(true);

                Instance = null;



                FlamieLog.Info("[FlamiePrac] Disabled — patches removed, scene and UI cleaned up");

                return true;

            }

            catch (Exception ex)

            {

                FlamieLog.Error("[FlamiePrac] Failed to disable: " + ex);

                return false;

            }

        }



        /// <summary>
        /// True when Class1 is compiled into / loaded from MultiSheet.dll (same assembly as the PHL plugin).
        /// </summary>
        private static bool IsHostedInsideMultiSheet()
        {
            return typeof(Class1).Assembly.GetType("PHLPracticeModPack.PHLPracticeModPackPlugin") != null;
        }

        private static void PatchFlamieHarmonyOnly()

        {

            Type[] types;

            try

            {

                types = typeof(Class1).Assembly.GetTypes();

            }

            catch (ReflectionTypeLoadException ex)

            {

                types = ex.Types;

            }



            if (types == null)

                return;



            for (int i = 0; i < types.Length; i++)

            {

                Type type = types[i];

                if (type == null || type.IsInterface)

                    continue;



                string ns = type.Namespace ?? string.Empty;

                if (ns.StartsWith("PHLPracticeModPack", StringComparison.Ordinal) ||

                    ns.StartsWith("CustomLevel", StringComparison.Ordinal) ||

                    ns.StartsWith("CL_", StringComparison.Ordinal))

                    continue;



                if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: true).Length == 0)

                    continue;



                try

                {

                    harmony.CreateClassProcessor(type).Patch();

                }

                catch (Exception ex)

                {

                    FlamieLog.Warn("[FlamiePrac] Harmony patch skipped for " + type.Name + ": " + ex.Message);

                }

            }

        }



        private void RollbackEnable()

        {

            if (harmonyPatched)

            {

                harmony.UnpatchSelf();

                harmonyPatched = false;

            }



            if (managerObject != null)

            {

                UnityEngine.Object.Destroy(managerObject);

                managerObject = null;

            }



            loadedPrefabs.Clear();

            UnloadBundles(false);

            Instance = null;

        }



        /// <summary>

        /// Get a loaded prefab by name (case-insensitive).

        /// </summary>

        public GameObject GetPrefab(string name)

        {

            if (string.IsNullOrEmpty(name))

            {

                foreach (var kvp in loadedPrefabs)

                    return kvp.Value;

                return null;

            }



            string key = name.ToLowerInvariant();

            if (loadedPrefabs.TryGetValue(key, out GameObject prefab))

                return prefab;



            foreach (var kvp in loadedPrefabs)

            {

                if (kvp.Key.Contains(key))

                    return kvp.Value;

            }



            return null;

        }



        /// <summary>

        /// Get all available prefab names.

        /// </summary>

        public List<string> GetPrefabNames()

        {

            return loadedPrefabs.Keys.OrderBy(x => x).ToList();

        }



        private void RegisterPrefab(string key, GameObject prefab)

        {

            if (prefab == null)

                return;



            string normalizedKey = NormalizeKey(string.IsNullOrWhiteSpace(key) ? prefab.name : key);

            if (!loadedPrefabs.ContainsKey(normalizedKey))

            {

                loadedPrefabs[normalizedKey] = prefab;

                FlamieLog.Info($"[FlamiePrac] Loaded prefab: {normalizedKey} (source='{prefab.name}')");

            }



            string prefabNameKey = NormalizeKey(prefab.name);

            if (!loadedPrefabs.ContainsKey(prefabNameKey))

                loadedPrefabs[prefabNameKey] = prefab;

        }



        private string NormalizeKey(string value)

        {

            return (value ?? string.Empty).Trim().ToLowerInvariant();

        }

        

        private void UnloadBundles(bool unloadAllLoadedObjects)

        {

            foreach (AssetBundle loadedBundle in bundles)

            {

                try { loadedBundle.Unload(unloadAllLoadedObjects); } catch { }

            }

            bundles.Clear();

        }

        internal IReadOnlyList<AssetBundle> GetLoadedBundles() => bundles;

    }

}


