using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Optional JSON layout beside the mod DLL. Falls back to built-in rink-1 defaults.
/// </summary>
public static class TrainingLayoutConfig
{
    [Serializable]
    public class Vec3
    {
        public float x;
        public float y;
        public float z;

        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    public class SpawnEntry
    {
        public string Type = "prefab";
        public string Name = "trainingprefab";
        public Vec3 Position = new Vec3();
        public float RotationY;
        public float Speed = 14f;
        public Vec3 Scale = new Vec3 { x = DefaultPasserLength, y = 0.55f, z = 0.12f };
    }

    /// <summary>Vanilla blue end goal line (world Z). Pass bumpers stay entirely in front of this.</summary>
    public const float BlueGoalLineZ = 40.23f;

    /// <summary>Long axis length — longer than original 2m, but must not span past the goal line.</summary>
    public const float DefaultPasserLength = 5f;

    public const float DefaultPasserRotationY = 45f;

    /// <summary>
    /// Center Z for a ±45° passer so the goal-side tip sits on the line, not behind it in the end zone.
    /// </summary>
    public static float PasserCenterZ(float boardLengthX, float rotationYDegrees = DefaultPasserRotationY)
    {
        float halfLen = boardLengthX * 0.5f;
        float rad = rotationYDegrees * Mathf.Deg2Rad;
        float goalSideExtent = halfLen * Mathf.Abs(Mathf.Sin(rad));
        return BlueGoalLineZ - goalSideExtent;
    }

    public static Vec3 DefaultPasserScale() =>
        new Vec3 { x = DefaultPasserLength, y = 0.55f, z = 0.12f };

    [Serializable]
    public class LayoutFile
    {
        public bool AutoStart = true;
        public SpawnEntry[] Spawns;
    }

    private static LayoutFile cached;

    public static LayoutFile Current
    {
        get
        {
            if (cached == null)
                cached = Load();
            return cached;
        }
    }

    public static void Reload()
    {
        cached = Load();
    }

    private static LayoutFile Load()
    {
        try
        {
            string modPath = Path.GetDirectoryName(typeof(TrainingLayoutConfig).Assembly.Location);
            string path = Path.Combine(modPath ?? string.Empty, "training_layout.json");
            if (!File.Exists(path))
            {
                // Blank-server friendly: seed from shipped example so AutoStart works without a manual rename.
                string example = Path.Combine(modPath ?? string.Empty, "training_layout.example.json");
                if (File.Exists(example))
                {
                    File.Copy(example, path);
                    Debug.Log("[FlamiePrac] Seeded training_layout.json from example for first boot.");
                }
                else
                {
                    Debug.Log("[FlamiePrac] No training_layout.json — using built-in defaults.");
                    return DefaultLayout();
                }
            }

            string json = File.ReadAllText(path);
            LayoutFile layout = JsonUtility.FromJson<LayoutFile>(json);
            if (layout?.Spawns == null || layout.Spawns.Length == 0)
            {
                Debug.LogWarning("[FlamiePrac] training_layout.json empty — using built-in defaults.");
                return DefaultLayout();
            }

            Debug.Log("[FlamiePrac] Loaded training_layout.json (" + layout.Spawns.Length + " spawn(s)).");
            return layout;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[FlamiePrac] Failed to load training_layout.json: " + ex.Message);
            return DefaultLayout();
        }
    }

    public static LayoutFile DefaultLayout()
    {
        return new LayoutFile
        {
            AutoStart = true,
            Spawns = new[]
            {
                new SpawnEntry
                {
                    Type = "prefab",
                    Name = "trainingprefab",
                    Position = new Vec3 { x = 0.8f, y = 0f, z = 21f }
                },
                new SpawnEntry
                {
                    Type = "passer",
                    Position = new Vec3
                    {
                        x = 6f,
                        y = 0f,
                        z = PasserCenterZ(DefaultPasserLength, DefaultPasserRotationY)
                    },
                    RotationY = DefaultPasserRotationY,
                    Speed = 14f,
                    Scale = DefaultPasserScale()
                },
                new SpawnEntry
                {
                    Type = "passer",
                    Position = new Vec3
                    {
                        x = -6f,
                        y = 0f,
                        z = PasserCenterZ(DefaultPasserLength, -DefaultPasserRotationY)
                    },
                    RotationY = -DefaultPasserRotationY,
                    Speed = 14f,
                    Scale = DefaultPasserScale()
                }
            }
        };
    }

    public static void AppendSpawn(SpawnEntry entry)
    {
        LayoutFile layout = Current;
        var list = new List<SpawnEntry>();
        if (layout.Spawns != null)
            list.AddRange(layout.Spawns);
        list.Add(entry);
        layout.Spawns = list.ToArray();
        Save(layout);
        cached = layout;
    }

    public static void Save(LayoutFile layout)
    {
        try
        {
            string modPath = Path.GetDirectoryName(typeof(TrainingLayoutConfig).Assembly.Location);
            string path = Path.Combine(modPath ?? string.Empty, "training_layout.json");
            string json = JsonUtility.ToJson(layout, true);
            File.WriteAllText(path, json);
            Debug.Log("[FlamiePrac] Saved training_layout.json");
        }
        catch (Exception ex)
        {
            Debug.LogError("[FlamiePrac] Save layout failed: " + ex.Message);
        }
    }
}
