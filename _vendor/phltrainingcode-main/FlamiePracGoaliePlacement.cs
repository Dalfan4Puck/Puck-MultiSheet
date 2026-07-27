using UnityEngine;

/// <summary>
/// MaxPractice AI crease for the training hive. Decorative GoalieModel in the prefab bundle
/// is deleted on spawn — placement uses authored constants (not a live decor transform).
/// </summary>
public static class FlamiePracGoaliePlacement
{
    // Authored crease at the real (non-ShooterTutor) net. Was read from GoalieModel once;
    // the mesh is removed from every hive instance so we never "hide then hitbox" it.
    private static readonly Vector3 DefaultCrease = new Vector3(-0.38f, 0f, -39.17f);
    private static readonly Vector3 DefaultNet = new Vector3(-0.38f, 0f, -41.02f);
    private const PlayerTeam DefaultTeam = PlayerTeam.Red;

    private static bool active;
    private static Vector3 trainingGoalPos;
    private static PlayerTeam trainingTeam;
    private static Vector3 creasePos;
    private static Quaternion creaseRot;

    public static bool TryGetGoalPos(PlayerTeam team, out Vector3 pos)
    {
        if (!active || team != trainingTeam)
        {
            pos = default;
            return false;
        }

        pos = trainingGoalPos;
        return true;
    }

    public static bool TryGetCrease(PlayerTeam team, out Vector3 pos, out Quaternion rot)
    {
        if (!active || team != trainingTeam)
        {
            pos = default;
            rot = default;
            return false;
        }

        pos = creasePos;
        rot = creaseRot;
        return true;
    }

    /// <summary>
    /// Activate AI crease for this hive. Does not inspect or require GoalieDecor in the tree.
    /// </summary>
    public static bool ConfigureFromTrainingHive(GameObject trainingRoot, out PlayerTeam spawnTeam)
    {
        spawnTeam = DefaultTeam;
        if (trainingRoot == null)
            return false;

        // Belt-and-suspenders: if a rebuild path skipped strip, kill decor before AI stands there.
        StripGoalieDecorFromPrefabInstance(trainingRoot);

        spawnTeam = DefaultTeam;
        trainingTeam = DefaultTeam;
        creasePos = DefaultCrease;
        trainingGoalPos = DefaultNet;

        float iceY = ResolveIceY(creasePos);
        creasePos.y = iceY;
        trainingGoalPos.y = iceY;

        Vector3 towardIce = Vector3.forward; // Red net → look toward center ice (+Z)
        creaseRot = Quaternion.LookRotation(towardIce);
        active = true;

        Debug.Log("[FlamiePrac] Training goalie crease=" + creasePos + " net=" + trainingGoalPos +
                  " team=" + spawnTeam + " (layout constants — GoalieDecor not used)");
        return true;
    }

    public static void Clear()
    {
        active = false;
    }

    /// <summary>
    /// Prefab bundle still contains Blender GoalieModel — delete it from the instance immediately
    /// so AddHitboxes never sees it. ShooterTutor / Goaltarp is not touched.
    /// </summary>
    public static void StripGoalieDecorFromPrefabInstance(GameObject hiveRoot)
    {
        if (hiveRoot == null)
            return;

        int removed = 0;
        // Snapshot names first — destroying while iterating children is unsafe.
        foreach (string childName in new[]
                 {
                     "GoalieModel",
                     "GoalieModelStick",
                     TrainingPrefabNames.GoalieDecor,
                     TrainingPrefabNames.GoalieDecorStick
                 })
        {
            Transform child = FindDeep(hiveRoot.transform, childName);
            if (child == null)
                continue;

            // Immediate — deferred Destroy would still be visible to AddHitboxes this frame.
            Object.DestroyImmediate(child.gameObject);
            removed++;
        }

        if (removed > 0)
            Debug.Log("[FlamiePrac] Removed GoalieDecor from hive instance (" + removed + " root(s)).");
    }

    // --- Retired: CacheAnchorFromHive / DisableUnusedGoalieDecor / PurgeUnusedGoalieDecor ---
    // We used to soft-hide decor, let AddHitboxes run, then purge. That left invisible colliders.
    // Placement no longer reads the mesh; strip on spawn instead.

    private static float ResolveIceY(Vector3 near)
    {
        Vector3 origin = new Vector3(near.x, near.y + 5f, near.z);
        int ice = LayerMask.NameToLayer("Ice");
        int mask = ice >= 0 ? (1 << ice) : Physics.DefaultRaycastLayers;

        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 20f, mask, QueryTriggerInteraction.Ignore);
        float best = float.PositiveInfinity;
        bool found = false;

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || hit.collider.isTrigger)
                continue;

            if (hit.collider.GetComponentInParent<TrainingSyncMarker>() != null)
                continue;

            if (hit.collider.GetComponentInParent<SlidableObstacle>() != null)
                continue;

            if (Vector3.Dot(hit.normal, Vector3.up) < 0.7f)
                continue;

            if (hit.point.y < best)
            {
                best = hit.point.y;
                found = true;
            }
        }

        return found ? best : 0f;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root == null)
            return null;

        if (root.name == name)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), name);
            if (found != null)
                return found;
        }

        return null;
    }
}
