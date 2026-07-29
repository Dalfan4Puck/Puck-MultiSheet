using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Builds training props for server authority (physics) or client visuals (meshes/audio).
/// </summary>
public static class TrainingObjectFactory
{
    private static readonly Color PasserNeonGreen = new Color(0.12f, 1f, 0.18f);
    private static readonly Color SheetWhite = Color.white;

    public static readonly Vector3 DefaultSheetScale = new Vector3(16f, 0.06f, 12f);

    public enum BuildRole
    {
        ServerAuthority,
        ClientVisual
    }

    public static GameObject BuildPrefab(
        GameObject prefab,
        string prefabName,
        Vector3 position,
        Quaternion rotation,
        int syncId,
        BuildRole role)
    {
        if (prefab == null)
            return null;

        GameObject obj = Object.Instantiate(prefab, position, rotation);
        obj.name = "Training_" + prefabName + "_" + syncId;

        TrainingPrefabRenamer.Apply(obj);
        // Prefab bundle still contains Blender GoalieModel — delete before hitboxes/AI.
        // Crease comes from layout constants, not that mesh.
        FlamiePracGoaliePlacement.StripGoalieDecorFromPrefabInstance(obj);
        WirePrefabComponents(obj, role);

        if (role == BuildRole.ServerAuthority)
        {
            // Movables first (own colliders), then static hive hitboxes for everything else
            // (ShooterTutor, rails, cones, practice skater, rotating sticks — puck-blocking).
            SlidableObstacleSetup.ConfigureServer(obj, syncId);
            CollisionHelper.AddHitboxes(obj, serverAuthority: true);
        }
        else
        {
            EnsureClientRenderers(obj);
            // Same order as server: detach slidables BEFORE AddHitboxes. Otherwise the hive's
            // kinematic Rigidbody compounds their colliders and LateUpdate world-pose sync
            // cannot move the meshes (push "works", client never sees where they went).
            SlidableObstacleSetup.ConfigureClientMirror(obj, syncId);
            CollisionHelper.AddHitboxes(obj, serverAuthority: false);
            // Restore Ice layer after SetLayerRecursive stamped the whole hive static.
            SlidableObstacleSetup.RelayerClientSlidables(syncId);
        }

        // Fallback for any speaker still under the hive tree (client mirrors). Slidable
        // speaker units are unparented on the server and registered during slidable setup.
        WireRadioSpeakers(obj, role);

        TrainingMaterialFix.ApplyFromPrefab(obj, prefab);

        var marker = obj.GetComponent<TrainingSyncMarker>();
        if (marker == null)
            marker = obj.AddComponent<TrainingSyncMarker>();
        marker.SyncId = syncId;

        TrainingMotionSync.RegisterFromRoot(obj, syncId, role == BuildRole.ServerAuthority);

        return obj;
    }

    /// <param name="positionIsFinalWorldCenter">
    /// False (server/layout): <paramref name="position"/>.y is ice surface — lift + seat on rink ice.
    /// True (client mirror): <paramref name="position"/> is the server's final board center — place exactly.
    /// </param>
    public static GameObject BuildPasser(
        Vector3 position,
        float yRot,
        float speed,
        Vector3 scale,
        int syncId,
        BuildRole role,
        bool positionIsFinalWorldCenter = false)
    {
        Vector3 rootPos = position;
        if (!positionIsFinalWorldCenter)
            rootPos.y += Mathf.Max(scale.y, 0.01f) * 0.5f;

        GameObject root = new GameObject("PassBackBox_" + syncId);
        root.transform.SetPositionAndRotation(rootPos, Quaternion.Euler(0f, yRot, 0f));

        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = "BoardVisual";
        visual.transform.SetParent(root.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = scale;
        Object.Destroy(visual.GetComponent<Collider>());

        if (role == BuildRole.ClientVisual || !Application.isBatchMode)
            TrainingMaterialFix.ApplyPrimitiveRenderer(visual, PasserNeonGreen);
        else
        {
            Renderer renderer = visual.GetComponent<Renderer>();
            if (renderer != null)
                renderer.enabled = false;
        }

        if (role == BuildRole.ServerAuthority)
        {
            // Seat to true rink ice before clients mirror — layout y=0 is only approximate.
            if (!positionIsFinalWorldCenter)
                SeatPasserOnRinkIce(root.transform, scale);

            CollisionHelper.SetSlidablePhysicsLayer(root);

            // Shooter-facing neon face (-local Z). Thin trigger slab on that plane — not a deep
            // volume in front of the board (that made passes fire "too far forward").
            float halfDepth = 0.5f * Mathf.Max(scale.z, 0.2f);
            const float triggerDepth = 0.22f;
            GameObject hitFace = new GameObject("HitFace");
            hitFace.transform.SetParent(root.transform, false);
            hitFace.transform.localPosition = new Vector3(0f, 0f, -halfDepth);
            hitFace.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            hitFace.layer = root.layer;

            BoxCollider trigger = hitFace.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            // Centered on the neon face: half into the board, half a skin toward the shooter.
            trigger.size = new Vector3(scale.x * 1.05f, scale.y * 1.05f, triggerDepth);
            trigger.center = Vector3.zero;

            ApplyBumperMaterial(trigger);

            PuckPasser passer = root.AddComponent<PuckPasser>();
            passer.passSpeed = speed;
            passer.hitFace = hitFace.transform;
            hitFace.AddComponent<PassBumperHitRelay>().passer = passer;

            SlidableObstacleSetup.ConfigurePasserServer(root, syncId, scale);
        }
        else
        {
            SlidableObstacleSetup.ConfigurePasserClient(root, syncId, scale);
        }

        var marker = root.AddComponent<TrainingSyncMarker>();
        marker.SyncId = syncId;
        return root;
    }

    /// <summary>Move board center so the bottom face sits on rink ice (not hive Ice props).</summary>
    private static void SeatPasserOnRinkIce(Transform root, Vector3 scale)
    {
        if (root == null)
            return;

        float halfH = Mathf.Max(scale.y, 0.01f) * 0.5f;
        float fallbackIceY = root.position.y - halfH;
        float iceY = fallbackIceY;

        Vector3 origin = new Vector3(root.position.x, fallbackIceY + 3f, root.position.z);
        int iceLayer = LayerMask.NameToLayer("Ice");
        int mask = iceLayer >= 0 ? (1 << iceLayer) : Physics.DefaultRaycastLayers;
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 20f, mask, QueryTriggerInteraction.Ignore);

        float best = float.PositiveInfinity;
        bool found = false;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || hit.collider.isTrigger)
                continue;
            if (Vector3.Dot(hit.normal, Vector3.up) < 0.7f)
                continue;
            if (hit.point.y > fallbackIceY + 0.75f || hit.point.y < fallbackIceY - 2.5f)
                continue;

            // Skip training-hive Ice meshes (slightly high vs true rink ice).
            Transform t = hit.collider.transform;
            bool training = false;
            while (t != null)
            {
                string n = t.name;
                if (n.StartsWith("Training_", System.StringComparison.Ordinal) ||
                    n.StartsWith("Train_", System.StringComparison.Ordinal) ||
                    n.IndexOf("trainingprefab", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    training = true;
                    break;
                }

                t = t.parent;
            }

            if (training)
                continue;

            if (hit.point.y < best)
            {
                best = hit.point.y;
                found = true;
            }
        }

        if (found)
            iceY = best;

        Vector3 p = root.position;
        p.y = iceY + halfH;
        root.position = p;
    }

    private static void ApplyBumperMaterial(Collider collider)
    {
        if (collider == null)
            return;

        PhysicsMaterial bumper = new PhysicsMaterial("FlamiePrac_PasserBumper")
        {
            bounciness = 0.85f,
            dynamicFriction = 0.05f,
            staticFriction = 0.05f,
            bounceCombine = PhysicsMaterialCombine.Maximum,
            frictionCombine = PhysicsMaterialCombine.Minimum
        };
        collider.material = bumper;
    }

    /// <summary>Flat pushable sheet — wide thin blanket on the slidable prop layer (~22).</summary>
    public static GameObject BuildSlidableSheet(
        Vector3 position,
        float yRot,
        Vector3 scale,
        int syncId,
        BuildRole role,
        bool positionIsFinalWorldCenter = false)
    {
        Vector3 rootPos = position;
        if (!positionIsFinalWorldCenter)
            rootPos.y += Mathf.Max(scale.y, 0.01f) * 0.5f;

        GameObject root = new GameObject("SlidableSheet_" + syncId);
        root.transform.SetPositionAndRotation(rootPos, Quaternion.Euler(0f, yRot, 0f));

        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = "BoardVisual";
        visual.transform.SetParent(root.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = scale;
        Object.Destroy(visual.GetComponent<Collider>());

        if (role == BuildRole.ClientVisual || !Application.isBatchMode)
            TrainingMaterialFix.ApplyPrimitiveRenderer(visual, SheetWhite);
        else
        {
            Renderer renderer = visual.GetComponent<Renderer>();
            if (renderer != null)
                renderer.enabled = false;
        }

        if (role == BuildRole.ServerAuthority)
        {
            if (!positionIsFinalWorldCenter)
                SeatPasserOnRinkIce(root.transform, scale);

            SlidableObstacleSetup.ConfigureSlidableSheetServer(root, syncId, scale);
        }
        else
            SlidableObstacleSetup.ConfigureSlidableSheetClient(root, syncId, scale);

        var marker = root.AddComponent<TrainingSyncMarker>();
        marker.SyncId = syncId;
        return root;
    }

    public static GameObject BuildCircularTarget(Vector3 position, int syncId, BuildRole role)
    {
        GameObject targetObj = new GameObject("CircularTarget_" + syncId);
        targetObj.transform.position = position;

        var target = targetObj.AddComponent<CircularMovingTarget>();
        target.Init(position, role == BuildRole.ServerAuthority ? TrainingObjectManager.Instance : null);

        var marker = targetObj.AddComponent<TrainingSyncMarker>();
        marker.SyncId = syncId;

        TrainingMotionSync.RegisterFromRoot(targetObj, syncId, role == BuildRole.ServerAuthority);

        return targetObj;
    }

    private static void WirePrefabComponents(GameObject obj, BuildRole role)
    {
        Transform[] transforms = obj.GetComponentsInChildren<Transform>(true);

        Transform spinner = transforms.FirstOrDefault(t => t.name == "Spinner");
        if (spinner != null && spinner.GetComponent<ConstantRotator>() == null)
            spinner.gameObject.AddComponent<ConstantRotator>();

        // GoalieDecor stripped in StripGoalieDecorFromPrefabInstance. PracticePlayer stays solid.

        foreach (Transform stick in transforms.Where(t =>
                     t.name == TrainingPrefabNames.RotatingStickRight ||
                     t.name == TrainingPrefabNames.RotatingStickLeft ||
                     t.name == "RotatingStick" ||
                     t.name == "RotatingStick2"))
        {
            if (stick.GetComponent<ConstantRotator>() == null)
            {
                ConstantRotator rotator = stick.gameObject.AddComponent<ConstantRotator>();
                rotator.direction = 1f;
            }
        }

        Transform movingStick = transforms.FirstOrDefault(t =>
            t.name == TrainingPrefabNames.PracticePlayer || t.name == "PlayerWithStick");
        if (movingStick != null && movingStick.GetComponent<ConstantMover>() == null)
            movingStick.gameObject.AddComponent<ConstantMover>();
    }

    private static void WireRadioSpeakers(GameObject obj, BuildRole role)
    {
        if (!ShouldAttachRadio(role) || obj == null)
            return;

        Transform[] transforms = obj.GetComponentsInChildren<Transform>(true);
        List<Transform> speakerRoots = transforms
            .Where(t => TrainingPrefabNames.IsSpeakerName(t.name))
            .Where(t => t.GetComponentsInChildren<Renderer>(true).Length > 0)
            .ToList();

        foreach (Transform speaker in speakerRoots)
            RegisterRadioSpeaker(speaker.gameObject, role);
    }

    internal static void RegisterRadioSpeaker(GameObject speakerGo, BuildRole role)
    {
        if (!ShouldAttachRadio(role) || speakerGo == null)
            return;

        RadioController controller = RadioController.Instance;
        if (controller == null)
        {
            Debug.LogWarning("[FlamiePrac] Radio speaker '" + speakerGo.name +
                             "' not registered — RadioController missing.");
            return;
        }

        controller.RegisterSpeaker(speakerGo);
    }

    private static bool ShouldAttachRadio(BuildRole role)
    {
        if (Application.isBatchMode)
            return false;

        if (role == BuildRole.ClientVisual)
            return true;

        if (role != BuildRole.ServerAuthority)
            return false;

        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm != null && nm.IsServer && nm.IsClient;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureClientRenderers(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null)
                renderer.enabled = true;
        }
    }
}

public sealed class TrainingSyncMarker : MonoBehaviour
{
    public int SyncId;
}
