using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Finds pushable training beams/panels in the hive prefab and gives each a heavy dynamic rigidbody.
/// </summary>
public static class SlidableObstacleSetup
{
    // Canonical names applied at spawn — see TrainingPrefabNames + training_prefab_names.json
    // Train_CenterPushBeam   = pushable long panel, center ice (~x0 z24)
    // Train_ShooterSideRail  = static segmented rail, +x shooter side (~x17 z31)
    // Train_FarEndRail       = static segmented rail, far -x end (~x-16 z-21)
    private static readonly string[] ExplicitSlidableRootNames = TrainingPrefabNames.SlidableBeamRoots;

    private static readonly string[] SkipNameTokens =
    {
        "goalie", "goaltarp", "net", "tarp", "speaker", "spinner", "stick", "passer",
        "hitface", "boardvisual", "target", "cone", "puck", "radio", "playerwithstick"
    };

    private const float MinColliderHeight = 1.25f;
    private const float MinSpeakerColliderHeight = 0.75f;
    private const float SpeakerMass = 12f;
    private const float SpeakerLinearDrag = 1.4f;
    private const float SpeakerAngularDrag = 1.1f;
    private const float SpeakerMaxLinearSpeed = 4f;
    private const float SpeakerMaxAngularSpeed = 1.8f;
    private const float SpeakerMaxTipAngularSpeed = 8f;
    private const float SpeakerStickPushScale = 26f;
    private const float BeamMass = 60f;
    private const float BeamLinearDrag = 1.2f;
    private const float BeamAngularDrag = 5f;
    private const float BeamMaxLinearSpeed = 4f;
    private const float BeamMaxAngularSpeed = 1.2f;
    private const float BeamStickPushScale = 24f;
    /// <summary>Grow beam height + thickness; length stays the same.</summary>
    private const float BeamCrossSectionScale = 1.5f;
    public const string PasserRelativePathPrefix = "passer";

    public static string PasserRelativePathFor(int syncId) => PasserRelativePathPrefix + "#" + syncId;

    /// <summary>
    /// Pass-back boards stay frozen at spawn. Sliding was unreliable and the solid body
    /// collider created a center-face dead zone; keep a back-half player-push slab only.
    /// </summary>
    public static void ConfigurePasserServer(GameObject passerRoot, int syncId, Vector3 scale)
    {
        if (passerRoot == null || !IsServerSide())
            return;

        LockPasserPose(passerRoot, syncId, scale, ownAnchor: false);
        Debug.Log("[FlamiePrac] Passer locked at spawn: syncId=" + syncId + " (no slide sync)");
    }

    public static void ConfigurePasserClient(GameObject passerRoot, int syncId, Vector3 scale)
    {
        if (passerRoot == null)
            return;

        // Anchor is the DDOL ownership root (see TrainingSync.GetClientOwnershipRoot).
        LockPasserPose(passerRoot, syncId, scale, ownAnchor: true);
    }

    private static void LockPasserPose(GameObject passerRoot, int syncId, Vector3 scale, bool ownAnchor)
    {
        SlidableObstacle oldObstacle = passerRoot.GetComponent<SlidableObstacle>();
        if (oldObstacle != null)
            UnityEngine.Object.Destroy(oldObstacle);

        SlidableObstacleVisual oldVisual = passerRoot.GetComponent<SlidableObstacleVisual>();
        if (oldVisual != null)
        {
            SlidableObstacleSync.UnregisterVisual(oldVisual);
            UnityEngine.Object.Destroy(oldVisual);
        }

        GameObject anchor = null;
        if (ownAnchor)
        {
            anchor = new GameObject("PassBackAnchor_" + syncId);
            anchor.transform.SetPositionAndRotation(
                passerRoot.transform.position,
                passerRoot.transform.rotation);
            passerRoot.transform.SetParent(anchor.transform, true);
        }

        Rigidbody rb = passerRoot.GetComponent<Rigidbody>();
        if (rb == null)
            rb = passerRoot.AddComponent<Rigidbody>();

        rb.mass = 1000f;
        rb.useGravity = false;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        rb.constraints = RigidbodyConstraints.FreezeAll;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        EnsurePasserCollider(passerRoot, scale);
        ApplyPasserBodyMaterial(passerRoot);

        PassBackBoardLock boardLock = passerRoot.GetComponent<PassBackBoardLock>();
        if (boardLock == null)
            boardLock = passerRoot.AddComponent<PassBackBoardLock>();
        boardLock.ownedAnchor = anchor;
    }

    private static void EnsurePasserCollider(GameObject root, Vector3 scale)
    {
        foreach (Collider col in root.GetComponents<Collider>())
        {
            if (col != null && !col.isTrigger)
                UnityEngine.Object.Destroy(col);
        }

        BoxCollider box = root.GetComponent<BoxCollider>();
        if (box == null)
            box = root.AddComponent<BoxCollider>();

        // Match the neon BoardVisual exactly (full depth, centered). Pucks ignore this solid via
        // SlidablePuckFilter and still enter HitFace — a back-half-only slab made stick contact
        // feel shifted behind the mesh on dedicated (client visual vs server hitbox).
        float width = Mathf.Max(scale.x, 0.35f);
        float height = Mathf.Max(scale.y, 0.35f);
        float depth = Mathf.Max(scale.z, 0.2f);
        box.center = Vector3.zero;
        box.size = new Vector3(width, height, depth);
        box.isTrigger = false;
        CollisionHelper.SetSlidablePhysicsLayer(root);
        SlidablePuckFilter.ConfigureForPlayerPushOnly(root);
    }

    private static void ApplyPasserBodyMaterial(GameObject root)
    {
        if (root == null)
            return;

        PhysicsMaterial heavy = new PhysicsMaterial("FlamiePrac_PasserBody")
        {
            dynamicFriction = 0.7f,
            staticFriction = 0.85f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Maximum,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };

        foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
        {
            if (col != null && !col.isTrigger)
                col.material = heavy;
        }
    }

    public static void ConfigureServer(GameObject trainingRoot, int syncId)
    {
        if (trainingRoot == null || !IsServerSide())
            return;

        List<Transform> beams = FindAllSlidableBeams(trainingRoot.transform);
        foreach (Transform beam in beams)
        {
            ScalePushableBeamCrossSection(beam);
            AttachSlidableDecals(beam, maxDistance: 2.5f);
            AbsorbNearbyBeamMeshes(beam, trainingRoot.transform, maxDistance: 3.5f);
            ConfigureSlidableServer(
                trainingRoot.transform,
                beam,
                syncId,
                BeamMass,
                MinColliderHeight,
                preset: ApplyBeamPhysicsProfileFields);
        }

        List<Transform> speakers = FindAllSpeakers(trainingRoot.transform);
        int speakerUnits = 0;
        foreach (Transform speaker in speakers)
            speakerUnits += ConfigureSlidableSpeakerServer(trainingRoot.transform, speaker, syncId);

        int total = beams.Count + speakerUnits;
        if (total == 0)
        {
            Debug.Log("[FlamiePrac] No slidable beams or speakers found on '" + trainingRoot.name + "'.");
            return;
        }

        Debug.Log("[FlamiePrac] Configured " + beams.Count + " slidable beam(s) and " +
                  speakerUnits + " speaker unit(s) on '" + trainingRoot.name + "'.");
    }

    private static void ApplyBeamPhysicsProfileFields(SlidableObstacle obstacle)
    {
        if (obstacle == null)
            return;

        obstacle.mass = BeamMass;
        obstacle.linearDrag = BeamLinearDrag;
        obstacle.angularDrag = BeamAngularDrag;
        obstacle.maxLinearSpeed = BeamMaxLinearSpeed;
        obstacle.maxAngularSpeed = BeamMaxAngularSpeed;
        obstacle.stickPushForceScale = BeamStickPushScale;
        obstacle.stickLiftForceScale = 0f;
        obstacle.keepOnIce = true;
        obstacle.freezePitchRoll = true;
        obstacle.settleFlatOnIce = true;
    }

    /// <summary>
    /// +50% height and width; leave the long axis alone so the beam doesn't get longer.
    /// </summary>
    private static void ScalePushableBeamCrossSection(Transform beam)
    {
        if (beam == null || Mathf.Abs(BeamCrossSectionScale - 1f) < 0.001f)
            return;

        Vector3 mul = new Vector3(BeamCrossSectionScale, BeamCrossSectionScale, BeamCrossSectionScale);

        float upX = Mathf.Abs(beam.right.y);
        float upY = Mathf.Abs(beam.up.y);
        float upZ = Mathf.Abs(beam.forward.y);

        // Local axis most aligned with world up = height (always scaled).
        // Of the other two, scale the shorter mesh extent (width); skip the longer (length).
        if (TryGetCombinedRendererBounds(beam.gameObject, out Bounds worldBounds) &&
            TryWorldBoundsToLocalBox(beam, worldBounds, 0f, out _, out Vector3 localSize))
        {
            float ax = Mathf.Abs(localSize.x);
            float ay = Mathf.Abs(localSize.y);
            float az = Mathf.Abs(localSize.z);

            int heightAxis = 1;
            if (upX >= upY && upX >= upZ)
                heightAxis = 0;
            else if (upZ >= upY && upZ >= upX)
                heightAxis = 2;

            int lengthAxis;
            if (heightAxis == 0)
                lengthAxis = az >= ay ? 2 : 1;
            else if (heightAxis == 1)
                lengthAxis = ax >= az ? 0 : 2;
            else
                lengthAxis = ax >= ay ? 0 : 1;

            mul = Vector3.one * BeamCrossSectionScale;
            if (lengthAxis == 0)
                mul.x = 1f;
            else if (lengthAxis == 1)
                mul.y = 1f;
            else
                mul.z = 1f;
        }
        else
        {
            // Fallback: grow local Y + smaller horizontal axis.
            mul = new Vector3(BeamCrossSectionScale, BeamCrossSectionScale, 1f);
        }

        beam.localScale = Vector3.Scale(beam.localScale, mul);
        Debug.Log("[FlamiePrac] Beam cross-section scale x" + BeamCrossSectionScale.ToString("F2") +
                  " on '" + beam.name + "' localScale=" + beam.localScale.ToString("F3"));
    }

    private static void ApplySpeakerPhysicsProfileFields(SlidableObstacle obstacle)
    {
        if (obstacle == null)
            return;

        obstacle.mass = SpeakerMass;
        obstacle.linearDrag = SpeakerLinearDrag;
        obstacle.angularDrag = SpeakerAngularDrag;
        obstacle.maxLinearSpeed = SpeakerMaxLinearSpeed;
        obstacle.maxAngularSpeed = SpeakerMaxAngularSpeed;
        obstacle.maxTipAngularSpeed = SpeakerMaxTipAngularSpeed;
        obstacle.stickPushForceScale = SpeakerStickPushScale;
        obstacle.stickLiftForceScale = 0f;
        // Snap faces flush like the beam — AABB/mesh-axis mismatch was leaving side poses
        // looking "stuck on an edge" while the collider thought a face was down.
        obstacle.settleFlatOnIce = true;
        obstacle.keepOnIce = false;
        obstacle.freezePitchRoll = false;
        obstacle.useCabinetMeshColliders = true;
        obstacle.maxTipAngularSpeed = SpeakerMaxTipAngularSpeed;
    }

    private static void ConfigureSlidableServer(
        Transform trainingRoot,
        Transform target,
        int syncId,
        float? mass,
        float minColliderHeight,
        string relativePathOverride = null,
        System.Action<SlidableObstacle> preset = null)
    {
        SlidableObstacle obstacle = target.GetComponent<SlidableObstacle>();
        if (obstacle == null)
            obstacle = target.gameObject.AddComponent<SlidableObstacle>();

        if (mass.HasValue)
            obstacle.mass = mass.Value;

        preset?.Invoke(obstacle);

        string relativePath = CanonicalSlidablePath(
            relativePathOverride ?? GetRelativePath(trainingRoot, target));
        obstacle.Initialize(trainingRoot, syncId, relativePath);

        SlidableStickCollision.RegisterSlidable(target.gameObject);

        // Only the slidable root leaves the kinematic hive tree — children (decals) follow via hierarchy.
        if (target.parent == trainingRoot || target.IsChildOf(trainingRoot))
            target.SetParent(null, true);

        if (TryGetCombinedRendererBounds(target.gameObject, out Bounds bounds))
        {
            int layer = target.gameObject.layer;
            Debug.Log("[FlamiePrac] Slidable '" + relativePath + "' name=" + target.name +
                      " size=" + bounds.size + " layer=" + LayerMask.LayerToName(layer));
        }
    }

    private static int ConfigureSlidableSpeakerServer(Transform trainingRoot, Transform speaker, int syncId)
    {
        int attached = AttachSpeakerDecals(speaker, trainingRoot);

        string basePath = GetRelativePath(trainingRoot, speaker);
        List<Transform> units = ResolveSpeakerSlidableUnits(speaker);
        for (int i = 0; i < units.Count; i++)
        {
            string path = units.Count > 1 ? basePath + "#" + i : null;
            ConfigureSlidableServer(
                trainingRoot,
                units[i],
                syncId,
                SpeakerMass,
                MinSpeakerColliderHeight,
                path,
                preset: ApplySpeakerPhysicsProfileFields);
            TrainingObjectFactory.RegisterRadioSpeaker(
                units[i].gameObject,
                TrainingObjectFactory.BuildRole.ServerAuthority);
        }

        if (units.Count > 1)
        {
            Debug.Log("[FlamiePrac] Split speaker into " + units.Count +
                      " slidable unit(s) at '" + basePath + "' (cabinet meshes only).");
        }

        if (attached > 0)
        {
            Debug.Log("[FlamiePrac] Attached " + attached + " sticker(s) to speaker at '" +
                      basePath + "'.");
        }

        return units.Count;
    }

    private static void ConfigureSlidableSpeakerClient(Transform trainingRoot, Transform speaker, int syncId)
    {
        AttachSpeakerDecals(speaker, trainingRoot);

        string basePath = GetRelativePath(trainingRoot, speaker);
        List<Transform> units = ResolveSpeakerSlidableUnits(speaker);
        for (int i = 0; i < units.Count; i++)
        {
            string path = units.Count > 1 ? basePath + "#" + i : GetRelativePath(trainingRoot, units[i]);
            ConfigureSlidableClient(trainingRoot, units[i], syncId, MinSpeakerColliderHeight, path);
            TrainingObjectFactory.RegisterRadioSpeaker(
                units[i].gameObject,
                TrainingObjectFactory.BuildRole.ClientVisual);
        }
    }

    /// <summary>
    /// Two cabinets often share one Speaker transform — split into independent slidable roots.
    /// Only reparents meshes already under this Speaker; never scans the wider hive.
    /// </summary>
    private static List<Transform> ResolveSpeakerSlidableUnits(Transform speaker)
    {
        var units = new List<Transform>();
        if (speaker == null)
            return units;

        var cabinets = new List<Transform>();
        foreach (MeshFilter meshFilter in speaker.GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter == null || !IsSpeakerCabinetMesh(meshFilter.transform))
                continue;

            if (!cabinets.Contains(meshFilter.transform))
                cabinets.Add(meshFilter.transform);
        }

        if (cabinets.Count <= 1)
        {
            units.Add(speaker);
            return units;
        }

        List<List<Transform>> clusters = ClusterByAxisGap(cabinets, 0.45f);
        if (clusters.Count <= 1)
        {
            units.Add(speaker);
            return units;
        }

        Transform parent = speaker.parent;
        for (int i = 0; i < clusters.Count; i++)
        {
            List<Transform> cluster = clusters[i];
            if (cluster.Count == 0)
                continue;

            Vector3 center = AverageRendererCenter(cluster);
            GameObject splitRoot = new GameObject(speaker.name + "_Slidable_" + i);
            splitRoot.transform.SetPositionAndRotation(center, speaker.rotation);
            if (parent != null)
                splitRoot.transform.SetParent(parent, true);

            foreach (Transform cabinet in cluster)
                cabinet.SetParent(splitRoot.transform, true);

            // Pivot must sit at visual center — transform.position average drifts one side and tips.
            RecenterRootToCabinetBounds(splitRoot.transform);
            units.Add(splitRoot.transform);
        }

        AssignDecalsToNearestUnits(speaker, units);
        // Thin shells / non-cabinet renderers used to stay on the original Speaker — sync moved
        // the collider roots while the mesh the player sees stayed put with no hitbox.
        ReparentLeftoverRenderersToNearestUnit(speaker, units);

        if (speaker.parent != null)
        {
            AssignLooseDecalsNearUnits(speaker.parent, speaker, units);
            ReparentLooseRenderersNearUnits(speaker.parent, speaker, units);
        }

        if (speaker.childCount == 0 && speaker.GetComponentsInChildren<Renderer>(true).Length == 0)
            UnityEngine.Object.Destroy(speaker.gameObject);
        else if (speaker.GetComponentsInChildren<Renderer>(true).Length > 0)
        {
            Debug.LogWarning("[FlamiePrac] Speaker '" + speaker.name +
                             "' still has leftover renderers after split — forcing onto nearest unit.");
            ReparentLeftoverRenderersToNearestUnit(speaker, units);
            if (speaker.GetComponentsInChildren<Renderer>(true).Length == 0 && speaker.childCount == 0)
                UnityEngine.Object.Destroy(speaker.gameObject);
        }

        return units;
    }

    private static void ReparentLeftoverRenderersToNearestUnit(Transform speaker, List<Transform> units)
    {
        if (speaker == null || units == null || units.Count == 0)
            return;

        var leftovers = new List<Transform>();
        foreach (Renderer renderer in speaker.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
                continue;

            Transform t = renderer.transform;
            if (units.Exists(u => u != null && (t == u || t.IsChildOf(u))))
                continue;

            leftovers.Add(t);
        }

        foreach (Transform leftover in leftovers)
        {
            Transform nearest = FindNearestUnit(leftover.position, units);
            if (nearest != null)
                leftover.SetParent(nearest, true);
        }
    }

    private static void ReparentLooseRenderersNearUnits(
        Transform scope,
        Transform speaker,
        List<Transform> units)
    {
        if (scope == null || units == null || units.Count == 0)
            return;

        Bounds zone = BuildUnitsBounds(units, 2.0f);
        for (int i = scope.childCount - 1; i >= 0; i--)
        {
            Transform candidate = scope.GetChild(i);
            if (candidate == null || candidate == speaker)
                continue;

            if (units.Exists(u => u != null && (candidate == u || candidate.IsChildOf(u))))
                continue;

            if (IsOwnedByOtherSpeaker(candidate, speaker))
                continue;

            if (candidate.GetComponentInChildren<Renderer>(true) == null)
                continue;

            if (!zone.Intersects(GetWorldBounds(candidate)))
                continue;

            // Don't steal other slidable beam roots / named assemblies.
            if (TrainingPrefabNames.IsSlidableBeamRoot(candidate.name) ||
                TrainingPrefabNames.IsSpeakerName(candidate.name) ||
                TrainingPrefabNames.IsSpeakerSlidableRoot(candidate.name))
                continue;

            Transform nearest = FindNearestUnit(candidate.position, units);
            if (nearest != null)
                candidate.SetParent(nearest, true);
        }
    }

    private static bool IsSpeakerCabinetMesh(Transform transform)
    {
        if (transform == null || IsDecalTransform(transform))
            return false;

        Renderer renderer = transform.GetComponent<Renderer>();
        if (renderer == null)
            return false;

        Vector3 size = renderer.bounds.size;
        float max = Mathf.Max(size.x, size.y, size.z);
        float min = Mathf.Min(size.x, size.y, size.z);
        return max >= 0.3f && min >= 0.12f;
    }

    private static List<List<Transform>> ClusterByAxisGap(List<Transform> parts, float minGap)
    {
        var clusters = new List<List<Transform>>();
        if (parts == null || parts.Count == 0)
            return clusters;

        if (parts.Count == 1)
        {
            clusters.Add(new List<Transform> { parts[0] });
            return clusters;
        }

        bool useX = Spread(parts, 0) >= Spread(parts, 2);
        parts.Sort((a, b) =>
        {
            float av = useX ? a.position.x : a.position.z;
            float bv = useX ? b.position.x : b.position.z;
            return av.CompareTo(bv);
        });

        var current = new List<Transform> { parts[0] };
        for (int i = 1; i < parts.Count; i++)
        {
            float prev = useX ? parts[i - 1].position.x : parts[i - 1].position.z;
            float next = useX ? parts[i].position.x : parts[i].position.z;
            if (Mathf.Abs(next - prev) > minGap)
            {
                clusters.Add(current);
                current = new List<Transform>();
            }

            current.Add(parts[i]);
        }

        clusters.Add(current);
        return clusters;
    }

    private static float Spread(List<Transform> parts, int axis)
    {
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        foreach (Transform part in parts)
        {
            float v = axis == 0 ? part.position.x : axis == 1 ? part.position.y : part.position.z;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return max - min;
    }

    private static Vector3 AveragePosition(List<Transform> transforms)
    {
        Vector3 sum = Vector3.zero;
        foreach (Transform transform in transforms)
            sum += transform.position;

        return sum / transforms.Count;
    }

    private static Vector3 AverageRendererCenter(List<Transform> transforms)
    {
        Vector3 sum = Vector3.zero;
        int count = 0;
        foreach (Transform transform in transforms)
        {
            if (transform == null)
                continue;

            Renderer renderer = transform.GetComponent<Renderer>();
            if (renderer != null)
            {
                sum += renderer.bounds.center;
                count++;
            }
            else
            {
                sum += transform.position;
                count++;
            }
        }

        return count > 0 ? sum / count : Vector3.zero;
    }

    /// <summary>
    /// Move root to cabinet visual center and keep children in place so COM at local 0 matches appearance.
    /// </summary>
    private static void RecenterRootToCabinetBounds(Transform root)
    {
        if (root == null || !TryGetCabinetLocalBounds(root.gameObject, out Vector3 localCenter, out _))
            return;

        if (localCenter.sqrMagnitude < 0.0001f)
            return;

        Vector3 worldCenter = root.TransformPoint(localCenter);
        Vector3 delta = worldCenter - root.position;
        root.position = worldCenter;

        for (int i = 0; i < root.childCount; i++)
            root.GetChild(i).position -= delta;
    }

    private static void AssignDecalsToNearestUnits(Transform sourceSpeaker, List<Transform> units)
    {
        if (sourceSpeaker == null || units == null || units.Count == 0)
            return;

        var decals = new List<Transform>();
        foreach (Transform child in sourceSpeaker.GetComponentsInChildren<Transform>(true))
        {
            if (child == null || child == sourceSpeaker)
                continue;

            if (!LooksLikeDecal(child))
                continue;

            if (units.Exists(u => u != null && child.IsChildOf(u)))
                continue;

            decals.Add(child);
        }

        foreach (Transform decal in decals)
        {
            Transform nearest = FindNearestUnit(decal.position, units);
            if (nearest != null)
                decal.SetParent(nearest, true);
        }
    }

    /// <summary>
    /// Pick up decal siblings left at the speaker parent after a cabinet split.
    /// </summary>
    private static void AssignLooseDecalsNearUnits(Transform scope, Transform speaker, List<Transform> units)
    {
        if (scope == null || units == null || units.Count == 0)
            return;

        Bounds zone = BuildUnitsBounds(units, 1.5f);

        for (int i = 0; i < scope.childCount; i++)
        {
            Transform candidate = scope.GetChild(i);
            if (candidate == null || candidate == speaker)
                continue;

            if (units.Exists(u => u != null && (candidate == u || candidate.IsChildOf(u))))
                continue;

            if (IsOwnedByOtherSpeaker(candidate, speaker))
                continue;

            if (!LooksLikeDecal(candidate))
                continue;

            if (!zone.Intersects(GetWorldBounds(candidate)))
                continue;

            Transform nearest = FindNearestUnit(candidate.position, units);
            if (nearest != null)
                candidate.SetParent(nearest, true);
        }
    }

    private static Bounds BuildUnitsBounds(List<Transform> units, float padding)
    {
        Bounds bounds = default;
        bool hasBounds = false;

        foreach (Transform unit in units)
        {
            if (unit == null)
                continue;

            Bounds unitBounds = GetWorldBounds(unit);
            if (!hasBounds)
            {
                bounds = unitBounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(unitBounds);
            }
        }

        if (!hasBounds)
            bounds = new Bounds(units[0].position, Vector3.one);

        bounds.Expand(padding);
        return bounds;
    }

    private static Bounds GetWorldBounds(Transform transform)
    {
        if (transform != null && TryGetCombinedRendererBounds(transform.gameObject, out Bounds bounds))
            return bounds;

        return new Bounds(transform != null ? transform.position : Vector3.zero, Vector3.one * 0.5f);
    }

    private static bool IsOwnedByOtherSpeaker(Transform candidate, Transform speaker)
    {
        Transform current = candidate;
        while (current != null)
        {
            if (TrainingPrefabNames.IsSpeakerName(current.name) && current != speaker)
                return true;

            current = current.parent;
        }

        return false;
    }

    private static int AttachSpeakerDecals(Transform speaker, Transform trainingRoot)
    {
        if (speaker == null)
            return 0;

        int attached = AttachSlidableDecals(speaker, maxDistance: 2.5f);
        attached += AttachNearbySpeakerDecals(speaker, trainingRoot);
        return attached;
    }

    private static int AttachNearbySpeakerDecals(Transform speaker, Transform trainingRoot)
    {
        if (speaker == null)
            return 0;

        Bounds zone = GetWorldBounds(speaker);
        zone.Expand(2.5f);

        int attached = 0;
        Transform scope = speaker.parent;
        for (int level = 0; level < 2 && scope != null; level++)
        {
            for (int i = scope.childCount - 1; i >= 0; i--)
            {
                Transform candidate = scope.GetChild(i);
                if (candidate == null || candidate == speaker || candidate.IsChildOf(speaker))
                    continue;

                if (candidate.name.StartsWith(TrainingPrefabNames.RadioSpeaker + TrainingPrefabNames.SpeakerSlidableSuffix, StringComparison.Ordinal) ||
                    candidate.name.StartsWith("Speaker" + TrainingPrefabNames.SpeakerSlidableSuffix, StringComparison.Ordinal))
                    continue;

                if (IsOwnedByOtherSpeaker(candidate, speaker))
                    continue;

                if (!LooksLikeDecal(candidate))
                    continue;

                if (!zone.Intersects(GetWorldBounds(candidate)))
                    continue;

                candidate.SetParent(speaker, true);
                attached++;
            }

            if (scope == trainingRoot)
                break;

            scope = scope.parent;
        }

        return attached;
    }

    /// <summary>
    /// Pull large sibling meshes into the beam root so the visible panel can't be left behind
    /// when the named root is unparented for physics/sync.
    /// </summary>
    private static int AbsorbNearbyBeamMeshes(Transform beam, Transform trainingRoot, float maxDistance)
    {
        if (beam == null)
            return 0;

        Transform parent = beam.parent;
        if (parent == null)
            return 0;

        Vector3 anchor = beam.position;
        if (TryGetCombinedRendererBounds(beam.gameObject, out Bounds beamBounds))
            anchor = beamBounds.center;

        int attached = 0;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform sibling = parent.GetChild(i);
            if (sibling == null || sibling == beam || sibling.IsChildOf(beam))
                continue;

            if (TrainingPrefabNames.IsSpeakerName(sibling.name) ||
                TrainingPrefabNames.IsSpeakerSlidableRoot(sibling.name) ||
                TrainingPrefabNames.IsSlidableBeamRoot(sibling.name) ||
                TrainingPrefabNames.IsRotatingStickName(sibling.name))
                continue;

            if (sibling.GetComponentInChildren<Renderer>(true) == null)
                continue;

            // Only absorb substantial meshes (not tiny fasteners).
            Bounds sibBounds = GetWorldBounds(sibling);
            float maxExtent = Mathf.Max(sibBounds.size.x, sibBounds.size.y, sibBounds.size.z);
            if (maxExtent < 0.75f)
                continue;

            if (Vector3.Distance(sibBounds.center, anchor) > maxDistance)
                continue;

            sibling.SetParent(beam, true);
            attached++;
        }

        if (attached > 0)
            Debug.Log("[FlamiePrac] Absorbed " + attached + " nearby mesh(es) into beam '" + beam.name + "'.");

        return attached;
    }

    /// <summary>
    /// Named decal siblings near a slidable root — parent before unparenting. Never scans the hive.
    /// </summary>
    private static int AttachSlidableDecals(Transform slidableRoot, float maxDistance = 1.75f)
    {
        if (slidableRoot == null)
            return 0;

        Transform parent = slidableRoot.parent;
        if (parent == null)
            return 0;

        Vector3 anchor = slidableRoot.position;
        if (TryGetCombinedRendererBounds(slidableRoot.gameObject, out Bounds bounds))
            anchor = bounds.center;

        int attached = 0;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform sibling = parent.GetChild(i);
            if (sibling == null || sibling == slidableRoot || sibling.IsChildOf(slidableRoot))
                continue;

            if (!LooksLikeDecal(sibling))
                continue;

            if (Vector3.Distance(sibling.position, anchor) > maxDistance)
                continue;

            sibling.SetParent(slidableRoot, true);
            attached++;
        }

        return attached;
    }

    private static bool LooksLikeDecal(Transform transform)
    {
        if (transform == null)
            return false;

        if (transform.GetComponentInParent<SlidableObstacle>() != null)
            return false;

        if (transform.GetComponent<PuckPasser>() != null ||
            transform.GetComponent<SlidableObstacleVisual>() != null)
            return false;

        if (IsDecalTransform(transform))
            return true;

        if (ShouldSkip(transform.gameObject))
            return false;

        Renderer renderer = transform.GetComponent<Renderer>();
        if (renderer == null)
            return false;

        if (transform.GetComponent<Rigidbody>() != null)
            return false;

        Collider col = transform.GetComponent<Collider>();
        if (col != null && !col.isTrigger)
            return false;

        Vector3 size = renderer.bounds.size;
        float max = Mathf.Max(size.x, size.y, size.z);
        float min = Mathf.Min(size.x, size.y, size.z);
        if (max > 2.75f || min > 0.22f)
            return false;

        float aspect = max / Mathf.Max(min, 0.01f);
        if (aspect < 2.5f)
            return false;

        // Thick mesh — cabinet chunk, not a flat sticker.
        if (max >= 0.35f && min >= 0.12f && aspect < 4f)
            return false;

        return true;
    }

    private static bool IsDecalTransform(Transform transform)
    {
        if (transform == null)
            return false;

        // Name-only: must still exclude stickers after they are parented under SlidableObstacle.
        string lower = transform.name.ToLowerInvariant();
        return lower.Contains("phl") ||
               lower.Contains("sticker") ||
               lower.Contains("decal") ||
               lower.Contains("logo") ||
               lower.Contains("all-star") ||
               lower.Contains("allstar") ||
               lower.Contains("shield") ||
               lower.Contains("badge") ||
               lower.Contains("diamond") ||
               lower.Contains("maple") ||
               lower.StartsWith("plane");
    }

    private static Transform FindNearestUnit(Vector3 position, List<Transform> units)
    {
        Transform best = null;
        float bestDist = float.MaxValue;
        foreach (Transform unit in units)
        {
            if (unit == null)
                continue;

            float dist = Vector3.SqrMagnitude(unit.position - position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = unit;
            }
        }

        return best;
    }

    public static void ConfigureClientMirror(GameObject trainingRoot, int syncId)
    {
        if (trainingRoot == null)
            return;

        List<Transform> beams = FindAllSlidableBeams(trainingRoot.transform);
        foreach (Transform beam in beams)
        {
            ScalePushableBeamCrossSection(beam);
            AttachSlidableDecals(beam, maxDistance: 2.5f);
            AbsorbNearbyBeamMeshes(beam, trainingRoot.transform, maxDistance: 3.5f);
            ConfigureSlidableClient(trainingRoot.transform, beam, syncId, MinColliderHeight);
        }

        List<Transform> speakers = FindAllSpeakers(trainingRoot.transform);
        foreach (Transform speaker in speakers)
            ConfigureSlidableSpeakerClient(trainingRoot.transform, speaker, syncId);
    }

    private static void ConfigureSlidableClient(
        Transform trainingRoot,
        Transform target,
        int syncId,
        float minColliderHeight,
        string relativePathOverride = null)
    {
        SlidableObstacleVisual visual = target.GetComponent<SlidableObstacleVisual>();
        if (visual == null)
            visual = target.gameObject.AddComponent<SlidableObstacleVisual>();

        string relativePath = CanonicalSlidablePath(
            relativePathOverride ?? GetRelativePath(trainingRoot, target));
        visual.Initialize(syncId, relativePath);

        SlidableBoardCollision.Ensure();

        bool isSpeaker = TrainingPrefabNames.IsSpeakerSlidableRoot(target.name) ||
                         TrainingPrefabNames.IsSpeakerName(target.name) ||
                         relativePath.StartsWith(TrainingPrefabNames.RadioSpeaker, StringComparison.Ordinal);

        if (isSpeaker)
            FitSpeakerCabinetMeshColliders(target.gameObject);
        else
            FitBeamBoxColliderFromMeshes(target.gameObject);

        // Detach like the server — must not stay under the hive kinematic Rigidbody compound.
        if (target.parent == trainingRoot || target.IsChildOf(trainingRoot))
            target.SetParent(null, true);

        CollisionHelper.SetSlidablePhysicsLayer(target.gameObject);
        SlidableStickCollision.RegisterSlidable(target.gameObject);
        SlidableObstacleSync.RegisterVisual(visual);
        SlidableObstacleSync.RegisterVisualAlias(visual, syncId, target.name);
        SlidableObstacleSync.RegisterVisualAlias(visual, syncId, CanonicalSlidablePath(target.name));
        SlidableObstacleSync.RegisterVisualAlias(visual, syncId, relativePath);

        // Beam-only aliases — never register these on speakers (overwrote the beam key).
        if (!isSpeaker &&
            (relativePath == TrainingPrefabNames.CenterPushBeam ||
             TrainingPrefabNames.IsSlidableBeamRoot(target.name) ||
             target.name == "Untitl234ed"))
        {
            SlidableObstacleSync.RegisterVisualAlias(visual, syncId, "Untitl234ed");
            SlidableObstacleSync.RegisterVisualAlias(visual, syncId, TrainingPrefabNames.CenterPushBeam);
        }
    }

    /// <summary>
    /// After AddHitboxes SetLayerRecursive, put unparented client slidables back on Ice.
    /// </summary>
    public static void RelayerClientSlidables(int syncId)
    {
        foreach (SlidableObstacleVisual visual in SlidableObstacleSync.GetVisualsForSyncId(syncId))
        {
            if (visual != null)
                CollisionHelper.SetSlidablePhysicsLayer(visual.gameObject);
        }
    }

    private static List<Transform> FindAllSpeakers(Transform root)
    {
        var matches = new List<Transform>();
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            if (transform == null || transform == root)
                continue;

            if (TrainingPrefabNames.IsSpeakerName(transform.name) &&
                !TrainingPrefabNames.IsSpeakerSlidableRoot(transform.name))
                matches.Add(transform);
        }

        return matches;
    }

    private static List<Transform> FindAllNamedChildren(Transform root, string name)
    {
        var matches = new List<Transform>();
        CollectNamedChildren(root, name, matches);
        return matches;
    }

    private static void CollectNamedChildren(Transform parent, string name, List<Transform> matches)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name)
                matches.Add(child);

            CollectNamedChildren(child, name, matches);
        }
    }

    public static void ApplySlideMaterial(GameObject obj)
    {
        // Ice-like: skate/jump on top without sticky wipeouts. Still slides when stick-pushed.
        PhysicsMaterial slide = new PhysicsMaterial("FlamiePrac_SlidableIce")
        {
            dynamicFriction = 0.04f,
            staticFriction = 0.04f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };

        foreach (Collider col in obj.GetComponentsInChildren<Collider>(true))
        {
            if (col != null && !col.isTrigger)
                col.material = slide;
        }
    }

    /// <summary>Tight axis-aligned box matched to visible mesh bounds (after pose snap).</summary>
    public static void FitBoxColliderFromRenderers(GameObject go, float padding = 0.015f)
    {
        StripNonTriggerCollidersImmediate(go);

        if (!TryGetCombinedRendererBounds(go, out Bounds worldBounds) &&
            !TryGetCombinedMeshBounds(go, out worldBounds))
        {
            Debug.LogWarning("[FlamiePrac] FitBoxCollider: no bounds on '" + go.name + "' — using fallback size.");
            worldBounds = new Bounds(go.transform.position, new Vector3(0.5f, 0.5f, 0.5f));
        }

        if (!TryWorldBoundsToLocalBox(go.transform, worldBounds, padding, out Vector3 center, out Vector3 size))
        {
            center = Vector3.zero;
            size = new Vector3(0.5f, 0.5f, 0.5f);
        }

        BoxCollider box = go.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
        box.isTrigger = false;
        CollisionHelper.SetSlidablePhysicsLayer(go);
        Debug.Log("[FlamiePrac] Fit box collider on '" + go.name + "' size=" + box.size.ToString("F3"));
    }

    /// <summary>
    /// Beam box from mesh corners in root local space (no world AABB inflation).
    /// </summary>
    public static void FitBeamBoxColliderFromMeshes(GameObject go)
    {
        if (go == null)
            return;

        StripNonTriggerCollidersImmediate(go);

        Transform root = go.transform;
        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;
        bool found = false;

        foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null)
                continue;

            string lower = mf.gameObject.name.ToLowerInvariant();
            if (lower.Contains("sticker") || lower.Contains("decal") || lower.Contains("phl"))
                continue;

            Bounds mb = mf.sharedMesh.bounds;
            Vector3 c = mb.center;
            Vector3 e = mb.extents;
            for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
            for (int zi = -1; zi <= 1; zi += 2)
            {
                Vector3 world = mf.transform.TransformPoint(c + Vector3.Scale(e, new Vector3(xi, yi, zi)));
                Vector3 local = root.InverseTransformPoint(world);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
                found = true;
            }
        }

        if (!found)
        {
            FitBoxColliderFromRenderers(go, padding: 0f);
            return;
        }

        Vector3 size = max - min;
        Vector3 center = (min + max) * 0.5f;
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.center = center;
        box.size = new Vector3(
            Mathf.Max(Mathf.Abs(size.x), 0.12f),
            Mathf.Max(Mathf.Abs(size.y), 0.12f),
            Mathf.Max(Mathf.Abs(size.z), 0.12f));
        box.isTrigger = false;
        CollisionHelper.SetSlidablePhysicsLayer(go);
        Debug.Log("[FlamiePrac] Fit beam box on '" + go.name + "' size=" + box.size.ToString("F3"));
    }

    /// <summary>
    /// Single root BoxCollider matched to the dominant cabinet mesh's own axes (OBB),
    /// not a world/root AABB union of every child renderer. AABB fit made the physics
    /// rectangle disagree with the visual cabinet — upright looked fine, side faces wouldn't
    /// sit flush on ice no matter how hard you tipped them.
    /// </summary>
    public static void FitSpeakerCabinetMeshColliders(GameObject go)
    {
        if (go == null)
            return;

        StripNonTriggerCollidersImmediate(go);

        if (!TryGetDominantCabinetMesh(go, out MeshFilter dominant) ||
            dominant.sharedMesh == null)
        {
            Debug.LogWarning("[FlamiePrac] Speaker cabinet mesh missing on '" + go.name + "' — renderer box fallback.");
            FitBoxColliderFromRenderers(go, padding: 0.008f);
            return;
        }

        // Align slidable root to the mesh axes so BoxCollider faces == visual faces.
        AlignRootRotationToMesh(go.transform, dominant.transform);

        Bounds mb = dominant.sharedMesh.bounds;
        Transform root = go.transform;
        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;
        Vector3 c = mb.center;
        Vector3 e = mb.extents;
        for (int xi = -1; xi <= 1; xi += 2)
        for (int yi = -1; yi <= 1; yi += 2)
        for (int zi = -1; zi <= 1; zi += 2)
        {
            Vector3 world = dominant.transform.TransformPoint(c + Vector3.Scale(e, new Vector3(xi, yi, zi)));
            Vector3 local = root.InverseTransformPoint(world);
            min = Vector3.Min(min, local);
            max = Vector3.Max(max, local);
        }

        Vector3 size = max - min;
        Vector3 center = (min + max) * 0.5f;

        // Pivot at box center so every face tips/settles symmetrically.
        if (center.sqrMagnitude > 0.0001f)
        {
            Vector3 worldCenter = root.TransformPoint(center);
            Vector3 delta = worldCenter - root.position;
            root.position = worldCenter;
            for (int i = 0; i < root.childCount; i++)
                root.GetChild(i).position -= delta;
            center = Vector3.zero;
        }

        BoxCollider box = go.AddComponent<BoxCollider>();
        box.center = center;
        box.size = new Vector3(
            Mathf.Max(Mathf.Abs(size.x), 0.12f),
            Mathf.Max(Mathf.Abs(size.y), 0.12f),
            Mathf.Max(Mathf.Abs(size.z), 0.12f));
        box.isTrigger = false;
        CollisionHelper.SetSlidablePhysicsLayer(go);
        Debug.Log("[FlamiePrac] Speaker cabinet box on '" + go.name +
                  "' mesh='" + dominant.name + "' center=" + box.center.ToString("F3") +
                  " size=" + box.size.ToString("F3"));
    }

    private static bool TryGetDominantCabinetMesh(GameObject go, out MeshFilter dominant)
    {
        dominant = null;
        if (go == null)
            return false;

        float bestVolume = -1f;
        foreach (MeshFilter meshFilter in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter == null || meshFilter.sharedMesh == null)
                continue;

            if (ShouldSkipSpeakerCollider(meshFilter.transform))
                continue;

            Bounds b = meshFilter.sharedMesh.bounds;
            Vector3 s = b.size;
            // Prefer the bulky cabinet shell — skip skinny cones/grills.
            float max = Mathf.Max(s.x, s.y, s.z);
            float min = Mathf.Min(s.x, s.y, s.z);
            if (max < 0.25f || min < 0.08f)
                continue;
            if (max > 0.0001f && max / Mathf.Max(min, 0.001f) > 4.5f)
                continue;

            float volume = Mathf.Abs(s.x * s.y * s.z);
            if (volume <= bestVolume)
                continue;

            bestVolume = volume;
            dominant = meshFilter;
        }

        return dominant != null;
    }

    private static void AlignRootRotationToMesh(Transform root, Transform mesh)
    {
        if (root == null || mesh == null)
            return;

        if (Quaternion.Angle(root.rotation, mesh.rotation) < 0.25f)
            return;

        int childCount = root.childCount;
        var worldPos = new Vector3[childCount];
        var worldRot = new Quaternion[childCount];
        for (int i = 0; i < childCount; i++)
        {
            Transform child = root.GetChild(i);
            worldPos[i] = child.position;
            worldRot[i] = child.rotation;
        }

        Vector3 rootPos = root.position;
        root.SetPositionAndRotation(rootPos, mesh.rotation);

        for (int i = 0; i < childCount; i++)
            root.GetChild(i).SetPositionAndRotation(worldPos[i], worldRot[i]);
    }

    public static bool TryGetCabinetLocalBounds(GameObject go, out Vector3 localCenter, out Vector3 localSize)
    {
        localCenter = Vector3.zero;
        localSize = Vector3.zero;
        if (go == null)
            return false;

        if (!TryGetDominantCabinetMesh(go, out MeshFilter dominant) || dominant.sharedMesh == null)
            return false;

        Transform root = go.transform;
        Bounds mb = dominant.sharedMesh.bounds;
        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;
        Vector3 c = mb.center;
        Vector3 e = mb.extents;
        for (int xi = -1; xi <= 1; xi += 2)
        for (int yi = -1; yi <= 1; yi += 2)
        for (int zi = -1; zi <= 1; zi += 2)
        {
            Vector3 world = dominant.transform.TransformPoint(c + Vector3.Scale(e, new Vector3(xi, yi, zi)));
            Vector3 local = root.InverseTransformPoint(world);
            min = Vector3.Min(min, local);
            max = Vector3.Max(max, local);
        }

        localSize = max - min;
        if (localSize.sqrMagnitude < 0.0001f)
            return false;

        localCenter = (min + max) * 0.5f;
        return true;
    }

    private static void StripNonTriggerCollidersImmediate(GameObject go)
    {
        if (go == null)
            return;

        Collider[] cols = go.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            Collider col = cols[i];
            if (col == null || col.isTrigger)
                continue;

            col.enabled = false;
            UnityEngine.Object.DestroyImmediate(col);
        }
    }

    private static bool ShouldSkipSpeakerCollider(Transform transform)
    {
        if (transform == null)
            return true;

        if (IsDecalTransform(transform) || LooksLikeDecal(transform))
            return true;

        // Avoid ShouldSkip() here — its "speaker" token would skip cabinet meshes on slidable roots.
        if (transform.GetComponent<PuckPasser>() != null ||
            transform.GetComponent<ConstantRotator>() != null ||
            transform.GetComponent<ConstantMover>() != null ||
            transform.GetComponent<RadioController>() != null)
            return true;

        return !IsSpeakerCabinetMesh(transform);
    }

    private static bool TryWorldBoundsToLocalBox(
        Transform root,
        Bounds worldBounds,
        float padding,
        out Vector3 localCenter,
        out Vector3 localSize)
    {
        localCenter = Vector3.zero;
        localSize = Vector3.zero;

        if (root == null)
            return false;

        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;
        Vector3 center = worldBounds.center;
        Vector3 half = worldBounds.extents;

        for (int xi = -1; xi <= 1; xi += 2)
        for (int yi = -1; yi <= 1; yi += 2)
        for (int zi = -1; zi <= 1; zi += 2)
        {
            Vector3 corner = center + Vector3.Scale(half, new Vector3(xi, yi, zi));
            Vector3 local = root.InverseTransformPoint(corner);
            min = Vector3.Min(min, local);
            max = Vector3.Max(max, local);
        }

        localSize = max - min;
        if (localSize.sqrMagnitude < 0.0001f)
            return false;

        localCenter = (min + max) * 0.5f;
        localSize = new Vector3(
            Mathf.Max(localSize.x + padding * 2f, 0.12f),
            Mathf.Max(localSize.y + padding * 2f, 0.12f),
            Mathf.Max(localSize.z + padding * 2f, 0.12f));
        return true;
    }

    private static bool TryGetCombinedMeshBounds(GameObject go, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        foreach (MeshFilter meshFilter in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter == null || meshFilter.sharedMesh == null || ShouldSkip(meshFilter.gameObject))
                continue;

            Bounds meshBounds = meshFilter.sharedMesh.bounds;
            Vector3 worldCenter = meshFilter.transform.TransformPoint(meshBounds.center);
            Vector3 worldSize = meshFilter.transform.TransformVector(meshBounds.size);
            Bounds worldBounds = new Bounds(worldCenter, worldSize);

            if (!hasBounds)
            {
                bounds = worldBounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(worldBounds);
            }
        }

        return hasBounds;
    }

    private static List<Transform> FindAllSlidableBeams(Transform root)
    {
        var beams = new List<Transform>();

        foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
        {
            if (node == null || node == root)
                continue;

            if (!TrainingPrefabNames.IsSlidableBeamRoot(node.name))
                continue;

            if (!IsDuplicateBeam(beams, node))
                beams.Add(node);
        }

        if (beams.Count == 0)
        {
            Debug.LogWarning("[FlamiePrac] No slidable center beam found on '" + root.name +
                             "' — expected " + TrainingPrefabNames.CenterPushBeam +
                             " (or legacy Untitl234ed). Check training_prefab_names.json rename.");
        }

        return beams;
    }

    private static bool IsUnderExplicitSlidableRoot(Transform t)
    {
        Transform current = t;
        while (current != null)
        {
            string name = current.name;
            foreach (string rootName in ExplicitSlidableRootNames)
            {
                if (name == rootName)
                    return true;
            }

            current = current.parent;
        }

        return false;
    }

    public static bool TryGetCombinedRendererBounds(GameObject go, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || ShouldSkip(renderer.gameObject))
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private static Transform ResolveSlidableTransform(Renderer renderer)
    {
        if (renderer == null)
            return null;

        MeshFilter onSelf = renderer.GetComponent<MeshFilter>();
        if (onSelf != null && onSelf.sharedMesh != null)
            return onSelf.transform;

        MeshFilter childMesh = renderer.GetComponentInChildren<MeshFilter>();
        if (childMesh != null && childMesh.sharedMesh != null)
            return childMesh.transform;

        return renderer.transform;
    }

    private static bool TryScoreSlidableBeam(Renderer renderer, out float score)
    {
        score = 0f;
        if (renderer == null || ShouldSkip(renderer.gameObject))
            return false;

        Bounds bounds = renderer.bounds;
        Vector3 size = bounds.size;
        float length = Mathf.Max(size.x, size.z);
        float width = Mathf.Min(size.x, size.z);
        float height = size.y;
        float footprint = length * width;

        // Long low beam (narrow rectangle on the ice)
        if (length >= 3.5f &&
            width <= 3.5f &&
            height >= 0.2f &&
            height <= 2.5f &&
            length / Mathf.Max(width, 0.25f) >= 2f)
        {
            score = length * 2f - height - width + 100f;
            return score > 0f;
        }

        // Wide / tall pushable panel (the large gray rectangle in the hive)
        if (length >= 2.5f &&
            width >= 1.5f &&
            height >= 0.4f &&
            height <= 5f &&
            footprint >= 6f &&
            length <= 14f &&
            width <= 14f)
        {
            score = footprint + length + width - height * 0.5f;
            return score > 0f;
        }

        return false;
    }

    private static bool IsDuplicateBeam(List<Transform> existing, Transform candidate)
    {
        if (candidate == null)
            return true;

        for (int i = existing.Count - 1; i >= 0; i--)
        {
            Transform other = existing[i];
            if (other == null)
                continue;

            if (candidate == other)
                return true;

            if (candidate.IsChildOf(other))
                return true;

            // Prefer the outer mesh when a panel contains nested renderers.
            if (other.IsChildOf(candidate))
                existing.RemoveAt(i);
        }

        foreach (Transform other in existing)
        {
            if (other == null)
                continue;

            if (Vector3.Distance(other.position, candidate.position) < 0.35f)
                return true;
        }

        return false;
    }

    public static bool IsSlidableSubtree(Transform transform)
    {
        Transform current = transform;
        while (current != null)
        {
            if (TrainingPrefabNames.IsSlidableBeamRoot(current.name) ||
                TrainingPrefabNames.IsSpeakerName(current.name) ||
                TrainingPrefabNames.IsSpeakerSlidableRoot(current.name))
                return true;

            // Speaker split roots: Train_RadioSpeaker_Slidable_0 / Speaker_Slidable_0
            if (current.GetComponent<SlidableObstacle>() != null ||
                current.GetComponent<SlidableObstacleVisual>() != null)
                return true;

            current = current.parent;
        }

        return false;
    }

    /// <summary>
    /// Stable wire key for slidable sync — absorbs legacy Blender names and speaker split aliases.
    /// </summary>
    public static string CanonicalSlidablePath(string pathOrName)
    {
        if (string.IsNullOrEmpty(pathOrName))
            return string.Empty;

        string leaf = pathOrName;
        int slash = pathOrName.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < pathOrName.Length)
            leaf = pathOrName.Substring(slash + 1);

        if (leaf == "Untitl234ed" || leaf == TrainingPrefabNames.CenterPushBeam)
            return TrainingPrefabNames.CenterPushBeam;

        // Train_RadioSpeaker#0 / Speaker#0
        int hash = leaf.LastIndexOf('#');
        if (hash > 0 && hash + 1 < leaf.Length &&
            int.TryParse(leaf.Substring(hash + 1), out int slot))
        {
            string prefix = leaf.Substring(0, hash);
            if (prefix == TrainingPrefabNames.RadioSpeaker ||
                prefix == "Speaker" ||
                prefix.EndsWith("RadioSpeaker", StringComparison.Ordinal))
                return TrainingPrefabNames.RadioSpeaker + "#" + slot;
        }

        // Train_RadioSpeaker_Slidable_0
        const string slidableTok = "_Slidable_";
        int slidableAt = leaf.LastIndexOf(slidableTok, StringComparison.Ordinal);
        if (slidableAt > 0 &&
            int.TryParse(leaf.Substring(slidableAt + slidableTok.Length), out int slidableSlot))
        {
            return TrainingPrefabNames.RadioSpeaker + "#" + slidableSlot;
        }

        return leaf;
    }

    private static bool ShouldSkip(GameObject go)
    {
        string lower = go.name.ToLowerInvariant();
        foreach (string token in SkipNameTokens)
        {
            if (lower.Contains(token))
                return true;
        }

        if (go.GetComponent<PuckPasser>() != null ||
            go.GetComponent<ConstantRotator>() != null ||
            go.GetComponent<ConstantMover>() != null ||
            go.GetComponent<RadioController>() != null)
            return true;

        return false;
    }

    private static void EnsureBoxCollider(GameObject go, float minColliderHeight)
    {
        foreach (Collider col in go.GetComponentsInChildren<Collider>(true))
        {
            if (col != null && !col.isTrigger)
                UnityEngine.Object.Destroy(col);
        }

        float minFootprint = minColliderHeight < 1f ? 0.25f : 0.35f;
        BoxCollider box = go.AddComponent<BoxCollider>();
        if (TryGetCombinedRendererBounds(go, out Bounds worldBounds))
        {
            box.center = go.transform.InverseTransformPoint(worldBounds.center);
            Vector3 localSize = go.transform.InverseTransformVector(worldBounds.size);
            box.size = new Vector3(
                Mathf.Max(Mathf.Abs(localSize.x), minFootprint),
                Mathf.Max(Mathf.Abs(localSize.y), minColliderHeight),
                Mathf.Max(Mathf.Abs(localSize.z), minFootprint));
        }
        else
        {
            box.size = new Vector3(8f, minColliderHeight, minFootprint);
        }

        box.isTrigger = false;
        CollisionHelper.SetTrainingPhysicsLayer(go);
        Debug.Log("[FlamiePrac] Box collider on '" + go.name + "' size=" + box.size);
    }

    private static void EnsureDynamicCollider(GameObject go, float minColliderHeight = MinColliderHeight)
    {
        foreach (Collider col in go.GetComponentsInChildren<Collider>(true))
        {
            if (col != null && !col.isTrigger)
                UnityEngine.Object.Destroy(col);
        }

        int meshCount = CollisionHelper.AddConvexMeshColliders(go, ShouldSkipColliderTransform);
        if (meshCount > 0)
        {
            CollisionHelper.SetTrainingPhysicsLayer(go);
            LogShapeColliderSummary(go, meshCount);
            return;
        }

        float minFootprint = minColliderHeight < 1f ? 0.25f : 0.35f;
        BoxCollider box = go.AddComponent<BoxCollider>();
        if (TryGetCombinedRendererBounds(go, out Bounds worldBounds))
        {
            box.center = go.transform.InverseTransformPoint(worldBounds.center);
            Vector3 localSize = go.transform.InverseTransformVector(worldBounds.size);
            box.size = new Vector3(
                Mathf.Max(Mathf.Abs(localSize.x), minFootprint),
                Mathf.Max(Mathf.Abs(localSize.y), minColliderHeight),
                Mathf.Max(Mathf.Abs(localSize.z), minFootprint));
        }
        else
        {
            box.size = new Vector3(8f, minColliderHeight, minFootprint);
        }

        box.isTrigger = false;
        CollisionHelper.SetTrainingPhysicsLayer(go);
        Debug.Log("[FlamiePrac] Box fallback collider on '" + go.name + "' size=" + box.size);
    }

    private static void LogShapeColliderSummary(GameObject go, int meshCount)
    {
        if (!TryGetCombinedRendererBounds(go, out Bounds bounds))
        {
            Debug.Log("[FlamiePrac] Shape colliders on '" + go.name + "': " + meshCount + " mesh(es)");
            return;
        }

        Debug.Log("[FlamiePrac] Shape colliders on '" + go.name + "': " + meshCount +
                  " mesh(es), visual bounds=" + bounds.size.ToString("F2"));
    }

    private static bool ShouldSkipColliderTransform(Transform transform)
    {
        if (transform == null)
            return true;

        if (ShouldSkip(transform.gameObject))
            return true;

        if (LooksLikeDecal(transform))
            return true;

        if (transform.name == "PuckShield" || transform.name.StartsWith("Shield_", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>Client mirror needs a collider too — host skips mirrors but pure clients push against visuals.</summary>
    private static void EnsureClientStickCollider(GameObject go, float minColliderHeight = MinColliderHeight)
    {
        foreach (Collider col in go.GetComponentsInChildren<Collider>(true))
        {
            if (col != null && !col.isTrigger)
                UnityEngine.Object.Destroy(col);
        }

        int meshCount = CollisionHelper.AddConvexMeshColliders(go, ShouldSkipColliderTransform);
        if (meshCount > 0)
        {
            CollisionHelper.SetSlidablePhysicsLayer(go);
            return;
        }

        float minFootprint = minColliderHeight < 1f ? 0.25f : 0.35f;
        BoxCollider box = go.AddComponent<BoxCollider>();
        if (TryGetCombinedRendererBounds(go, out Bounds worldBounds))
        {
            box.center = go.transform.InverseTransformPoint(worldBounds.center);
            Vector3 localSize = go.transform.InverseTransformVector(worldBounds.size);
            box.size = new Vector3(
                Mathf.Max(Mathf.Abs(localSize.x), minFootprint),
                Mathf.Max(Mathf.Abs(localSize.y), minColliderHeight),
                Mathf.Max(Mathf.Abs(localSize.z), minFootprint));
        }
        else
        {
            box.size = new Vector3(8f, minColliderHeight, minFootprint);
        }

        box.isTrigger = false;
        CollisionHelper.SetSlidablePhysicsLayer(go);
    }

    private static Bounds PadBoundsHeight(Bounds bounds, float minHeight)
    {
        if (bounds.size.y >= minHeight)
            return bounds;

        float delta = minHeight - bounds.size.y;
        bounds.center += Vector3.up * (delta * 0.5f);
        bounds.size = new Vector3(bounds.size.x, minHeight, bounds.size.z);
        return bounds;
    }

    public static string GetRelativePath(Transform root, Transform target)
    {
        if (root == null || target == null)
            return string.Empty;

        if (target == root)
            return string.Empty;

        var parts = new System.Collections.Generic.List<string>();
        Transform current = target;
        while (current != null && current != root)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        if (current != root)
            return target.name;

        parts.Reverse();
        return string.Join("/", parts);
    }

    public static Transform FindByRelativePath(Transform root, string relativePath)
    {
        if (root == null || string.IsNullOrEmpty(relativePath))
            return root;

        Transform current = root;
        string[] parts = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            Transform child = current.Find(part);
            if (child == null)
            {
                child = FindDeepChild(current, part);
                if (child == null)
                    return null;
            }

            current = child;
        }

        return current;
    }

    private static Transform FindDeepChild(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name)
                return child;

            Transform found = FindDeepChild(child, name);
            if (found != null)
                return found;
        }

        return null;
    }

    private static bool IsServerSide()
    {
        try
        {
            Unity.Netcode.NetworkManager nm = Unity.Netcode.NetworkManager.Singleton;
            return nm == null || nm.IsServer;
        }
        catch
        {
            return true;
        }
    }
}
