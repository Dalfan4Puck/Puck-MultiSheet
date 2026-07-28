using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Renames Blender-exported transforms on spawn so code/logs use readable names.
/// Edit training_prefab_names.json beside the mod DLL to add mappings.
/// </summary>
public static class TrainingPrefabRenamer
{
    [Serializable]
    public class RenameEntry
    {
        public string OldName;
        public string NewName;
        public string Description;
    }

    [Serializable]
    public class RenameFile
    {
        public RenameEntry[] Renames;
    }

    private static RenameFile cached;

    public static void Apply(GameObject trainingRoot)
    {
        if (trainingRoot == null)
            return;

        int renamed = ApplyJsonRenames(trainingRoot);
        int organized = OrganizeCatalog(trainingRoot);

        if (renamed > 0 || organized > 0)
        {
            FlamieLog.Info("[FlamiePrac] Prefab catalog: " + renamed + " json rename(s), " +
                      organized + " organized label(s).");
        }
    }

    private static int ApplyJsonRenames(GameObject trainingRoot)
    {
        RenameFile file = Load();
        if (file?.Renames == null || file.Renames.Length == 0)
            return 0;

        int renamed = 0;
        foreach (RenameEntry entry in file.Renames)
        {
            if (entry == null ||
                string.IsNullOrWhiteSpace(entry.OldName) ||
                string.IsNullOrWhiteSpace(entry.NewName))
                continue;

            renamed += RenameExact(trainingRoot.transform, entry.OldName.Trim(), entry.NewName.Trim());
        }

        return renamed;
    }

    /// <summary>
    /// Bulk labels for scattered props/stickers that share Blender name patterns.
    /// </summary>
    private static int OrganizeCatalog(GameObject trainingRoot)
    {
        Transform root = trainingRoot.transform;
        int count = 0;
        count += OrganizeDecorativePucks(root);
        count += OrganizeBoardStickers(root);
        return count;
    }

    private static int OrganizeDecorativePucks(Transform root)
    {
        var pucks = new List<Transform>();
        foreach (Transform child in root)
        {
            if (child == null)
                continue;

            if (child.name == "Puck" || child.name.StartsWith("Puck ", StringComparison.Ordinal) ||
                child.name.StartsWith("Puck(", StringComparison.Ordinal))
                pucks.Add(child);
        }

        if (pucks.Count == 0)
            return 0;

        pucks.Sort(CompareByWorldXZ);
        for (int i = 0; i < pucks.Count; i++)
            pucks[i].name = TrainingPrefabNames.DecorPuckPrefix + (i + 1).ToString("00");

        return pucks.Count;
    }

    private static int OrganizeBoardStickers(Transform root)
    {
        var buckets = new Dictionary<string, List<Transform>>();

        foreach (Transform child in root)
        {
            if (child == null)
                continue;

            if (!TryClassifyBoardSticker(child.name, out string category))
                continue;

            if (!buckets.TryGetValue(category, out List<Transform> list))
            {
                list = new List<Transform>();
                buckets[category] = list;
            }

            list.Add(child);
        }

        int renamed = 0;
        foreach (KeyValuePair<string, List<Transform>> bucket in buckets)
        {
            List<Transform> stickers = bucket.Value;
            stickers.Sort(CompareByWorldXZ);
            for (int i = 0; i < stickers.Count; i++)
            {
                stickers[i].name = bucket.Key + "_" + (i + 1).ToString("00");
                renamed++;
            }
        }

        return renamed;
    }

    private static int CompareByWorldXZ(Transform a, Transform b)
    {
        if (a == null || b == null)
            return 0;

        Vector3 ap = a.position;
        Vector3 bp = b.position;
        int z = ap.z.CompareTo(bp.z);
        return z != 0 ? z : ap.x.CompareTo(bp.x);
    }

    private static bool TryClassifyBoardSticker(string name, out string category)
    {
        category = null;
        if (string.IsNullOrEmpty(name))
            return false;

        if (name.StartsWith("PHL-US4x", StringComparison.Ordinal) ||
            name == "PHL PFP" ||
            name == "PHLBanner")
        {
            category = TrainingPrefabNames.StickerPhlLogo;
            return true;
        }

        if (name.StartsWith("PHL Text", StringComparison.Ordinal))
        {
            category = TrainingPrefabNames.StickerPhlText;
            return true;
        }

        if (name.StartsWith("PHL_Offseason_Open", StringComparison.Ordinal))
        {
            category = TrainingPrefabNames.StickerDivisionOpen;
            return true;
        }

        if (name.StartsWith("All-Star", StringComparison.Ordinal))
        {
            category = TrainingPrefabNames.StickerDivisionAllStar;
            return true;
        }

        if (name.StartsWith("Contender", StringComparison.Ordinal))
        {
            category = TrainingPrefabNames.StickerDivisionContender;
            return true;
        }

        if (name.StartsWith("prospect", StringComparison.OrdinalIgnoreCase))
        {
            category = TrainingPrefabNames.StickerDivisionProspect;
            return true;
        }

        if (name == "Pro" || name.StartsWith("Pro (", StringComparison.Ordinal))
        {
            category = TrainingPrefabNames.StickerDivisionPro;
            return true;
        }

        if (name.StartsWith("frontier-fill-color", StringComparison.Ordinal))
        {
            category = TrainingPrefabNames.StickerDivisionFrontierColor;
            return true;
        }

        if (name.StartsWith("frontier-fill-black", StringComparison.Ordinal))
        {
            category = TrainingPrefabNames.StickerDivisionFrontierBlack;
            return true;
        }

        return false;
    }

    private static int RenameExact(Transform root, string oldName, string newName)
    {
        int count = 0;
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            if (transform == null || transform.name != oldName)
                continue;

            transform.name = newName;
            count++;
        }

        return count;
    }

    private static RenameFile Load()
    {
        if (cached != null)
            return cached;

        cached = LoadFromDisk() ?? BuiltInDefaults();
        return cached;
    }

    public static void Reload()
    {
        cached = null;
    }

    private static RenameFile LoadFromDisk()
    {
        try
        {
            string modPath = Path.GetDirectoryName(typeof(TrainingPrefabRenamer).Assembly.Location);
            string path = Path.Combine(modPath ?? string.Empty, "training_prefab_names.json");
            if (!File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            if (json.Length > 0 && json[0] == '\uFEFF')
                json = json.Substring(1);

            RenameFile file = JsonUtility.FromJson<RenameFile>(json);
            int count = file?.Renames?.Length ?? 0;
            FlamieLog.Info("[FlamiePrac] Loaded training_prefab_names.json (" + count + " rename(s)).");
            // Empty/failed deserialize must not cache forever and skip BuiltInDefaults.
            if (count == 0)
                return null;
            return file;
        }
        catch (Exception ex)
        {
            FlamieLog.Warn("[FlamiePrac] Failed to load training_prefab_names.json: " + ex.Message);
            return null;
        }
    }

    private static RenameFile BuiltInDefaults()
    {
        return new RenameFile
        {
            Renames = new[]
            {
                new RenameEntry
                {
                    OldName = "Untitl234ed",
                    NewName = TrainingPrefabNames.CenterPushBeam,
                    Description = "Long dark pushable panel, center ice (~x0 z24)"
                },
                new RenameEntry
                {
                    OldName = "Untitled 1",
                    NewName = TrainingPrefabNames.ShooterSideRail,
                    Description = "Segmented static board rail, +x shooter-tutor side (~x17 z31)"
                },
                new RenameEntry
                {
                    OldName = "Untitled 1 (1)",
                    NewName = TrainingPrefabNames.FarEndRail,
                    Description = "Segmented static board rail, far -x end (~x-16 z-21)"
                },
                new RenameEntry
                {
                    OldName = "Speaker",
                    NewName = TrainingPrefabNames.RadioSpeaker,
                    Description = "Dual radio speaker cabinet (+x boards)"
                },
                new RenameEntry
                {
                    OldName = "GoaltarpV1",
                    NewName = TrainingPrefabNames.ShooterTutor,
                    Description = "Shooter tutor net tarp at goal line"
                },
                // GoalieDecor unused — leave legacy GoalieModel names; factory disables both.
                // new RenameEntry
                // {
                //     OldName = "GoalieModel",
                //     NewName = TrainingPrefabNames.GoalieDecor,
                //     Description = "Decorative goalie mesh (unused)"
                // },
                // new RenameEntry
                // {
                //     OldName = "GoalieModelStick",
                //     NewName = TrainingPrefabNames.GoalieDecorStick,
                //     Description = "Decorative goalie stick mesh (unused)"
                // },
                new RenameEntry
                {
                    OldName = "PlayerWithStick",
                    NewName = TrainingPrefabNames.PracticePlayer,
                    Description = "Static practice player prop"
                },
                new RenameEntry
                {
                    OldName = "RotatingStick",
                    NewName = TrainingPrefabNames.RotatingStickRight,
                    Description = "Spinning stick trainer (+x side)"
                },
                new RenameEntry
                {
                    OldName = "RotatingStick2",
                    NewName = TrainingPrefabNames.RotatingStickLeft,
                    Description = "Spinning stick trainer (-x side)"
                }
            }
        };
    }
}
