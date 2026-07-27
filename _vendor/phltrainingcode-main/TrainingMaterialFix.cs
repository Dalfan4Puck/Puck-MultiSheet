using System;
using System.Collections.Generic;
using MyMod;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Re-applies bundle materials and URP/Lit shaders after Instantiate (CustomLevelPlugin pattern).
/// </summary>
public static class TrainingMaterialFix
{
    private static readonly Dictionary<string, Material> BundleMaterials =
        new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

    private static Shader urpLitShader;
    private static bool initialized;

    public static void Initialize(IEnumerable<AssetBundle> bundles)
    {
        BundleMaterials.Clear();
        urpLitShader = Shader.Find("Universal Render Pipeline/Lit");

        if (bundles == null)
        {
            initialized = true;
            return;
        }

        foreach (AssetBundle bundle in bundles)
        {
            if (bundle == null)
                continue;

            try
            {
                foreach (string assetName in bundle.GetAllAssetNames())
                {
                    if (!assetName.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Material mat = bundle.LoadAsset<Material>(assetName);
                    if (mat == null)
                        continue;

                    string key = mat.name.ToLowerInvariant();
                    if (!BundleMaterials.ContainsKey(key))
                        BundleMaterials[key] = mat;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FlamiePrac] TrainingMaterialFix bundle scan failed: " + ex.Message);
            }
        }

        initialized = true;
        Debug.Log("[FlamiePrac] TrainingMaterialFix cached " + BundleMaterials.Count + " material(s).");
    }

    public static Material CreateLitMaterial(Color color)
    {
        Shader shader = urpLitShader ?? Shader.Find("Universal Render Pipeline/Lit");
        Material mat = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        else if (mat.HasProperty("_Color"))
            mat.color = color;
        return mat;
    }

    public static void ApplyFromPrefab(GameObject instance, GameObject sourcePrefab)
    {
        if (instance == null || Application.isBatchMode)
            return;

        if (!initialized)
            Initialize(Class1.Instance != null ? GetBundlesFromPlugin() : null);

        Dictionary<string, string> slotMap = BuildSlotMap(sourcePrefab);
        FixRenderers(instance, slotMap);
    }

    public static void ApplyPrimitiveRenderer(GameObject obj, Color color)
    {
        if (obj == null || Application.isBatchMode)
            return;

        Renderer renderer = obj.GetComponent<Renderer>();
        if (renderer == null)
            return;

        renderer.sharedMaterial = CreateLitMaterial(color);
        renderer.enabled = true;
    }

    private static IEnumerable<AssetBundle> GetBundlesFromPlugin()
    {
        return Class1.Instance?.GetLoadedBundles();
    }

    private static Dictionary<string, string> BuildSlotMap(GameObject prefab)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (prefab == null)
            return map;

        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
                continue;

            Material[] shared = renderer.sharedMaterials;
            for (int i = 0; i < shared.Length; i++)
            {
                if (shared[i] == null)
                    continue;

                map[renderer.gameObject.name + "_" + i] = shared[i].name.ToLowerInvariant();
            }
        }

        return map;
    }

    private static void FixRenderers(GameObject instance, Dictionary<string, string> slotMap)
    {
        Shader shader = urpLitShader ?? Shader.Find("Universal Render Pipeline/Lit");
        int fixedCount = 0;

        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
                continue;

            Material[] mats = renderer.materials;
            bool changed = false;

            for (int i = 0; i < mats.Length; i++)
            {
                Material mat = mats[i];
                if (mat == null || IsBroken(mat))
                {
                    string slotKey = renderer.gameObject.name + "_" + i;
                    if (slotMap.TryGetValue(slotKey, out string matName) &&
                        BundleMaterials.TryGetValue(matName, out Material bundleMat))
                    {
                        mats[i] = bundleMat;
                        changed = true;
                        continue;
                    }

                    mats[i] = CreateLitMaterial(new Color(0.35f, 0.38f, 0.42f));
                    changed = true;
                    continue;
                }

                if (shader != null && (mat.shader == null || IsBroken(mat)))
                {
                    mat.shader = shader;
                    changed = true;
                }

                if (mat.HasProperty("_Surface"))
                    mat.SetFloat("_Surface", 0f);
            }

            if (changed)
            {
                renderer.materials = mats;
                fixedCount++;
            }

            renderer.enabled = true;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }

        if (fixedCount > 0)
            Debug.Log("[FlamiePrac] Fixed materials on '" + instance.name + "' (" + fixedCount + " renderer(s)).");
    }

    private static bool IsBroken(Material mat)
    {
        if (mat == null || mat.shader == null)
            return true;

        return mat.shader.name == "Hidden/InternalErrorShader";
    }
}
