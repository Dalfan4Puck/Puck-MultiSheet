using UnityEngine;

/// <summary>
/// Sets up proper collision meshes on training objects from AssetBundles.
/// Extracts mesh data from MeshFilters and creates accurate MeshColliders
/// that work on dedicated servers (headless/batch mode).
/// </summary>
public static class CollisionHelper
{
    // Static hive hitboxes use an unused user layer (not Default/Ice).
    // Slidables use a second unused layer so Stick↔Ice stays vanilla while Stick↔slidable pushes.
    private static int? _iceLayerIndex;
    private static int? _staticTrainingLayerIndex;
    private static int? _slidablePropLayerIndex;

    private static int IceLayer
    {
        get
        {
            if (!_iceLayerIndex.HasValue)
            {
                int layer = LayerMask.NameToLayer("Ice");
                if (layer < 0)
                    layer = LayerMask.NameToLayer("ice");
                if (layer < 0)
                {
                    layer = LayerMask.NameToLayer("Boards");
                    Debug.LogWarning($"[FlamiePrac] 'Ice' layer not found, falling back to 'Boards' layer ({layer})");
                }
                if (layer < 0)
                {
                    layer = 0;
                    Debug.LogWarning("[FlamiePrac] No suitable layer found, using Default layer");
                }
                _iceLayerIndex = layer;
                FlamieLog.InfoOnce("layer-ice", "[FlamiePrac] Rink Ice layer index: " + layer +
                    " (" + LayerMask.LayerToName(layer) + ")");
            }
            return _iceLayerIndex.Value;
        }
    }

    private static int SlidablePropLayer
    {
        get
        {
            if (!_slidablePropLayerIndex.HasValue)
            {
                int exclude = StaticTrainingLayer;
                int layer = FindUnusedUserLayer(exclude);
                if (layer < 0)
                {
                    layer = LayerMask.NameToLayer("Post Processing");
                    if (layer < 0 || layer == exclude)
                        layer = 0;
                    Debug.LogWarning("[FlamiePrac] No empty user layer for slidables; using " +
                                     LayerMask.LayerToName(layer) + " (" + layer + ")");
                }
                else
                {
                    FlamieLog.InfoOnce("layer-slidable", "[FlamiePrac] Slidable prop layer index: " + layer +
                              " (Stick/Body collide; Puck ignored; Stick↔Ice unchanged).");
                }

                _slidablePropLayerIndex = layer;
            }

            return _slidablePropLayerIndex.Value;
        }
    }

    private static int StaticTrainingLayer
    {
        get
        {
            if (!_staticTrainingLayerIndex.HasValue)
            {
                int layer = FindUnusedUserLayer();
                if (layer < 0)
                {
                    // Last resort — Prefer not Default (can't ignore Stick↔Default safely).
                    layer = LayerMask.NameToLayer("Post Processing");
                    if (layer < 0)
                        layer = LayerMask.NameToLayer("Ignore Raycast");
                    if (layer < 0)
                        layer = 0;
                    Debug.LogWarning("[FlamiePrac] No empty user layer; static training using " +
                                     LayerMask.LayerToName(layer) + " (" + layer + ")");
                }
                else
                {
                    FlamieLog.InfoOnce("layer-static-training", "[FlamiePrac] Static training hitbox layer index: " + layer +
                              " (unused — Stick ignored, Puck/Player/Ice collide)");
                }
                _staticTrainingLayerIndex = layer;
            }
            return _staticTrainingLayerIndex.Value;
        }
    }

    /// <summary>First empty TagManager slot (Puck leaves 21–31 blank).</summary>
    private static int FindUnusedUserLayer(int excludeLayer = -1)
    {
        for (int i = 21; i < 32; i++)
        {
            if (i == excludeLayer)
                continue;

            if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Adds proper collision hitboxes to a spawned training object.
    /// Uses MeshColliders derived from the actual mesh geometry for accurate collision.
    /// On dedicated servers, disables renderers since there's no GPU.
    /// </summary>
    public static void AddHitboxes(GameObject obj, bool serverAuthority = true)
    {
        FlamieLog.Setup("[FlamiePrac] AddHitboxes serverAuthority=" + serverAuthority +
                  " batchMode=" + Application.isBatchMode);
        if (obj == null)
        {
            Debug.LogWarning("[FlamiePrac] AddHitboxes called with null object!");
            return;
        }

        // Dedicated unused layer — not Ice (Ice↔Ice off) and not Default (Stick must stay free).
        SetLayerRecursive(obj, StaticTrainingLayer);
        SlidableBoardCollision.Ensure();

        // Add Rigidbody to root if missing — kinematic so the training object stays put
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb == null)
            rb = obj.AddComponent<Rigidbody>();

        rb.isKinematic = true;
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        rb.interpolation = RigidbodyInterpolation.None;

        // Add MeshColliders to all child meshes for accurate collision from .blend geometry
        MeshFilter[] meshFilters = obj.GetComponentsInChildren<MeshFilter>(true);
        SkinnedMeshRenderer[] skinnedMeshes = obj.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        int colliderCount = 0;

        foreach (MeshFilter mf in meshFilters)
        {
            if (mf == null || mf.sharedMesh == null)
                continue;

            if (ShouldSkipHitbox(mf.transform))
                continue;

            Mesh mesh = mf.sharedMesh;

            if (mesh.vertexCount < 3)
            {
                Debug.LogWarning($"[FlamiePrac] Skipping mesh '{mesh.name}' due to low vertex count ({mesh.vertexCount})");
                continue;
            }

            // Remove any existing colliders on this child to avoid duplicates
            Collider[] existingColliders = mf.gameObject.GetComponents<Collider>();
            foreach (var col in existingColliders)
                Object.Destroy(col);

            bool moving = IsUnderRotatingObstacle(mf.transform);
            // Non-convex MeshColliders are static-only — spinning sticks need convex/box or they ghost.
            bool added = false;
            if (!moving)
                added = TryAddMeshCollider(mf.gameObject, mesh, false);
            if (!added)
                added = TryAddMeshCollider(mf.gameObject, mesh, true);
            if (!added)
                AddBoxColliderFallback(mf.gameObject, mesh);

            if (added || mf.gameObject.GetComponent<Collider>() != null)
                colliderCount++;
        }

        // Some prefabs use skinned meshes instead of mesh filters
        foreach (SkinnedMeshRenderer smr in skinnedMeshes)
        {
            if (smr == null || smr.sharedMesh == null)
                continue;

            if (ShouldSkipHitbox(smr.transform))
                continue;

            Mesh mesh = smr.sharedMesh;
            if (mesh.vertexCount < 3)
                continue;

            Collider[] existingColliders = smr.gameObject.GetComponents<Collider>();
            foreach (var col in existingColliders)
                Object.Destroy(col);

            bool moving = IsUnderRotatingObstacle(smr.transform);
            bool added = false;
            if (!moving)
                added = TryAddMeshCollider(smr.gameObject, mesh, false);
            if (!added)
                added = TryAddMeshCollider(smr.gameObject, mesh, true);
            if (!added)
                AddBoxColliderFallback(smr.gameObject, mesh);

            if (added || smr.gameObject.GetComponent<Collider>() != null)
                colliderCount++;
        }

        EnsureRotatingStickColliders(obj);

        // If no meshes were found, add a box collider on the root as absolute fallback
        if (colliderCount == 0)
        {
            Debug.LogWarning("[FlamiePrac] No meshes found on training object, adding root BoxCollider fallback");
            if (obj.GetComponent<Collider>() == null)
            {
                BoxCollider box = obj.AddComponent<BoxCollider>();
                box.size = Vector3.one;
                box.isTrigger = false;
            }
        }

        // On dedicated server (batch mode), disable visual-only components.
        // Physics still works without renderers — MeshColliders use MeshFilter data.
        if (serverAuthority && Application.isBatchMode)
            DisableVisualsForHeadless(obj);

        ValidateVisuals(obj);

        FlamieLog.Setup("[FlamiePrac] Added hitboxes to '" + obj.name + "': " + colliderCount +
            " collider(s), layer=" + LayerMask.LayerToName(obj.layer));
    }

    /// <summary>
    /// Only skip the three movable setups (they own their own colliders).
    /// Everything else in the hive — ShooterTutor, rails, cones, rotating sticks, dummies —
    /// keeps solid hitboxes for puck / player / slidable interaction (not Stick).
    /// </summary>
    public static bool ShouldSkipHitbox(Transform transform)
    {
        if (transform == null)
            return true;

        // Speakers + center push beam: configured by SlidableObstacleSetup, not static hive hitboxes.
        if (SlidableObstacleSetup.IsSlidableSubtree(transform))
            return true;

        // Pass bumpers are built separately (BuildPasser) — never double-hitbox if nested.
        if (transform.GetComponentInParent<PuckPasser>() != null)
            return true;

        // GoalieDecor unused — never add Ice hitboxes (DummyRed climbs them).
        Transform current = transform;
        while (current != null)
        {
            if (TrainingPrefabNames.IsUnusedGoalieDecor(current.name))
                return true;

            current = current.parent;
        }

        return false;
    }

    /// <summary>No-op. Previously stripped ShooterTutor colliders by mistake — do not resurrect that.</summary>
    public static void StripNonPhysicsDecorColliders(GameObject root)
    {
        // Intentionally empty. Static hive props must keep puck-blocking hitboxes.
    }

    public static bool IsUnderRotatingObstacle(Transform transform)
    {
        Transform current = transform;
        while (current != null)
        {
            if (TrainingPrefabNames.IsRotatingStickName(current.name))
                return true;

            if (current.GetComponent<ConstantRotator>() != null)
                return true;

            current = current.parent;
        }

        return false;
    }

    /// <summary>
    /// Rotating sticks must stay kinematic (not slidable) but keep solid Ice colliders that move
    /// with the spin — convex/box only, then sync in ConstantRotator.FixedUpdate.
    /// </summary>
    public static void EnsureRotatingStickColliders(GameObject root)
    {
        if (root == null)
            return;

        int fixedCount = 0;
        foreach (ConstantRotator rotator in root.GetComponentsInChildren<ConstantRotator>(true))
        {
            if (rotator == null)
                continue;

            GameObject go = rotator.gameObject;
            SetLayerRecursive(go, StaticTrainingLayer);

            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null)
                    continue;

                foreach (Collider col in mf.gameObject.GetComponents<Collider>())
                {
                    if (col == null)
                        continue;

                    MeshCollider meshCol = col as MeshCollider;
                    if (meshCol != null && meshCol.convex)
                        continue;

                    // Non-convex or odd collider — replace with moving-safe shape.
                    Object.Destroy(col);
                }

                if (mf.gameObject.GetComponent<Collider>() != null)
                    continue;

                Mesh mesh = mf.sharedMesh;
                if (!TryAddMeshCollider(mf.gameObject, mesh, convex: true))
                    AddBoxColliderFallback(mf.gameObject, mesh);

                if (mf.gameObject.GetComponent<Collider>() != null)
                    fixedCount++;
            }
        }

        if (fixedCount > 0)
            FlamieLog.Setup("[FlamiePrac] Rotating-stick colliders ensured: " + fixedCount + " convex/box");
    }

    private static void ValidateVisuals(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        int renderersWithNoMaterial = 0;
        int renderersWithNoMesh = 0;

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            SkinnedMeshRenderer skinnedRenderer = renderer as SkinnedMeshRenderer;
            bool hasMesh = (meshFilter != null && meshFilter.sharedMesh != null)
                        || (skinnedRenderer != null && skinnedRenderer.sharedMesh != null);
            if (!hasMesh)
                renderersWithNoMesh++;

            Material[] materials = renderer.sharedMaterials;
            bool hasMaterial = materials != null && materials.Length > 0;
            if (hasMaterial)
            {
                hasMaterial = false;
                foreach (Material material in materials)
                {
                    if (material != null)
                    {
                        hasMaterial = true;
                        break;
                    }
                }
            }

            if (!hasMaterial)
                renderersWithNoMaterial++;
        }

        if (renderersWithNoMaterial > 0 || renderersWithNoMesh > 0)
        {
            FlamieLog.WarnOnce(
                "visual-issues-" + obj.name,
                "[FlamiePrac] Visual issues detected on '" + obj.name + "': noMaterial=" + renderersWithNoMaterial +
                ", noMesh=" + renderersWithNoMesh + ", totalRenderers=" + renderers.Length);
        }
    }

    /// <summary>
    /// Convex mesh colliders on each child mesh for a dynamic rigidbody (compound shape).
    /// </summary>
    public static int AddConvexMeshColliders(GameObject root, System.Func<Transform, bool> skipTransform = null)
    {
        if (root == null)
            return 0;

        int count = 0;
        MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (MeshFilter mf in meshFilters)
        {
            if (mf == null || mf.sharedMesh == null)
                continue;

            if (skipTransform != null && skipTransform(mf.transform))
                continue;

            Mesh mesh = mf.sharedMesh;
            if (mesh.vertexCount < 3)
                continue;

            foreach (Collider existing in mf.gameObject.GetComponents<Collider>())
            {
                if (existing != null && !existing.isTrigger)
                    Object.Destroy(existing);
            }

            if (TryAddMeshCollider(mf.gameObject, mesh, convex: true))
            {
                count++;
                continue;
            }

            AddBoxColliderFallback(mf.gameObject, mesh);
            if (mf.gameObject.GetComponent<Collider>() != null)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Try to add a MeshCollider. Returns false if it fails (bad mesh topology, etc).
    /// </summary>
    private static bool TryAddMeshCollider(GameObject go, Mesh mesh, bool convex)
    {
        try
        {
            MeshCollider mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = convex;
            mc.isTrigger = false;
            mc.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation
                              | MeshColliderCookingOptions.EnableMeshCleaning
                              | MeshColliderCookingOptions.WeldColocatedVertices;

            // Verify the collider was actually created successfully
            if (mc.sharedMesh == null)
            {
                Object.Destroy(mc);
                return false;
            }

            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[FlamiePrac] MeshCollider failed on '{go.name}' (convex={convex}): {ex.Message}");
            // Clean up the broken collider
            MeshCollider broken = go.GetComponent<MeshCollider>();
            if (broken != null)
                Object.Destroy(broken);
            return false;
        }
    }

    /// <summary>
    /// Fallback: add a BoxCollider that encompasses the mesh bounds.
    /// </summary>
    private static void AddBoxColliderFallback(GameObject go, Mesh mesh)
    {
        try
        {
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size;
            box.isTrigger = false;
            FlamieLog.Setup("[FlamiePrac] BoxCollider fallback on '" + go.name + "': size=" + mesh.bounds.size);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[FlamiePrac] BoxCollider fallback failed on '{go.name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively set layer on a GameObject and all children.
    /// </summary>
    public static void SetTrainingPhysicsLayer(GameObject obj)
    {
        if (obj == null)
            return;

        SetLayerRecursive(obj, StaticTrainingLayer);
    }

    /// <summary>
    /// Dedicated mod layer — not Ice — so vanilla Stick↔Ice dip is untouched.
    /// StickPositioner raycasts include this layer via SlidableGroundRaycastPatch.
    /// </summary>
    public static void SetSlidablePhysicsLayer(GameObject obj)
    {
        if (obj == null)
            return;

        SetLayerRecursive(obj, SlidablePropLayer);
    }

    public static int GetSlidablePropLayerIndex() => SlidablePropLayer;

    public static int GetStaticTrainingPropLayerIndex() => StaticTrainingLayer;

    private static void SetLayerRecursive(GameObject obj, int layer)
    {
        obj.layer = layer;
        for (int i = 0; i < obj.transform.childCount; i++)
        {
            SetLayerRecursive(obj.transform.GetChild(i).gameObject, layer);
        }
    }

    /// <summary>
    /// Disable visual components on dedicated server (no GPU).
    /// MeshFilter is kept because MeshCollider references it.
    /// </summary>
    private static void DisableVisualsForHeadless(GameObject obj)
    {
        // Disable renderers
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in renderers)
        {
            if (r != null)
                r.enabled = false;
        }

        // Disable particle systems
        ParticleSystem[] particles = obj.GetComponentsInChildren<ParticleSystem>(true);
        foreach (ParticleSystem ps in particles)
        {
            if (ps != null)
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        // Disable audio sources
        AudioSource[] audioSources = obj.GetComponentsInChildren<AudioSource>(true);
        foreach (AudioSource audio in audioSources)
        {
            if (audio != null)
                audio.enabled = false;
        }

        // Disable lights
        Light[] lights = obj.GetComponentsInChildren<Light>(true);
        foreach (Light l in lights)
        {
            if (l != null)
                l.enabled = false;
        }

        // Disable Animators (no need for visual animation on server)
        Animator[] animators = obj.GetComponentsInChildren<Animator>(true);
        foreach (Animator a in animators)
        {
            if (a != null)
                a.enabled = false;
        }
    }

    /// <summary>
    /// Removes all colliders from a training object (used during cleanup).
    /// </summary>
    public static void RemoveHitboxes(GameObject obj)
    {
        if (obj == null) return;

        Collider[] colliders = obj.GetComponentsInChildren<Collider>(true);
        foreach (var col in colliders)
        {
            if (col != null)
                Object.Destroy(col);
        }

        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
            Object.Destroy(rb);
    }
}