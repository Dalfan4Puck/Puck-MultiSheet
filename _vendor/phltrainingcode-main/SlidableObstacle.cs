using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Pushable training prop — server physics authority with shape-matched colliders.
/// </summary>
public class SlidableObstacle : MonoBehaviour
{
    public float mass = 25f;
    public float linearDrag = 0.35f;
    public float angularDrag = 0.8f;
    public float stickPushForceScale = 32f;
    public float stickLiftForceScale = 0f;
    public bool settleFlatOnIce = true;
    public bool freezeRotation = false;
    public bool allowStickPush = true;
    public float maxLinearSpeed = 8f;
    public float maxAngularSpeed = 2.5f;
    public float maxTipAngularSpeed = 5f;
    public bool keepOnIce = false;
    public bool freezePitchRoll = false;
    public bool useCabinetMeshColliders = false;

    public int ParentSyncId { get; private set; }
    public string RelativePath { get; private set; }

    private Rigidbody rb;
    private Collider[] bodyColliders;
    private Transform rootTransform;
    private GameObject ownedAnchor;
    private float wakeTime;
    private bool isAwake;
    private bool collidersFitted;

    private const float JoinSettleSeconds = 0.35f;
    private const float SpeakerJoinSettleSeconds = 0.85f;
    private const float IceClearance = 0f;
    private const float SpinDampThreshold = 0.4f;
    private const float MaxStickBladeSpeed = 12f;
    private const float SettleSpeed = 0.35f;
    private const float SettleAngularSpeed = 0.45f;
    private const float SettleHoldSeconds = 0.45f;
    private const float DynamicStickSeconds = 1.25f;
    private int lastStickPushFrame = -1;
    private float lastStickPushTime = -10f;
    private float settleQuietTime;
    private float edgeStuckTime;
    private float lastBodyWipeTime = -10f;
    private static readonly List<SlidableObstacle> Active = new List<SlidableObstacle>();
    private const float EdgeSnapSeconds = 0.35f;
    private const float BodyWipeCooldown = 0.85f;
    private const float SkateIntoPlayerSpeed = 4.25f;
    private const float PropIntoPlayerSpeed = 2.35f;
    private const float BodyHitClosingSpeed = 3.25f;
    private const float BodyHitBounceScale = 0.45f;
    private const float BodyHitBounceMax = 9f;

    public static IReadOnlyList<SlidableObstacle> ActiveObstacles => Active;

    public void Initialize(Transform trainingRoot, int syncId, string relativePath, GameObject anchorToOwn = null)
    {
        ParentSyncId = syncId;
        RelativePath = relativePath ?? string.Empty;
        rootTransform = trainingRoot;
        ownedAnchor = anchorToOwn;

        rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();

        RefreshBodyColliders();

        rb.mass = mass;
        rb.linearDamping = linearDrag;
        rb.angularDamping = angularDrag;
        rb.useGravity = true;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.constraints = RigidbodyConstraints.None;
        rb.centerOfMass = Vector3.zero;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.sleepThreshold = 0.05f;
        float settle = useCabinetMeshColliders ? SpeakerJoinSettleSeconds : JoinSettleSeconds;
        wakeTime = Time.time + settle;
        isAwake = false;
        collidersFitted = false;

        SlidableBoardCollision.Ensure();
        SnapRestPose(forSpawn: true);
        SlidableObstacleSetup.ApplySlideMaterial(gameObject);
        RefreshBodyColliders();
        AlignMassToColliders();

        if (!Active.Contains(this))
            Active.Add(this);

        string boundsText = TryGetCombinedColliderBounds(out Bounds bounds)
            ? bounds.size.ToString("F2")
            : "unknown";
        Debug.Log("[FlamiePrac] Slidable obstacle ready: " + RelativePath +
                  " mass=" + mass + " colliders=" + (bodyColliders?.Length ?? 0) +
                  " bounds=" + boundsText +
                  (allowStickPush ? "" : " body-push-only") +
                  (keepOnIce ? " ice-locked" : ""));
    }

    private void RefreshBodyColliders()
    {
        var cols = new List<Collider>();
        foreach (Collider col in GetComponentsInChildren<Collider>(true))
        {
            if (col != null && !col.isTrigger)
                cols.Add(col);
        }

        bodyColliders = cols.ToArray();
    }

    private void OnDestroy()
    {
        Active.Remove(this);

        if (ownedAnchor != null)
            Destroy(ownedAnchor);

        SlidablePuckFilter.Unregister(gameObject);
    }

    private void FixedUpdate()
    {
        if (!IsServerSide() || rb == null)
            return;

        // Rink/Puck may flip Ice↔Ice off after load — keep slidables solid to each other.
        SlidableBoardCollision.Ensure();

        if (!isAwake)
        {
            if (Time.time < wakeTime)
                return;

            // Start kinematic (standable platform). Stick contact wakes dynamic briefly.
            PlaceOnIceSurface(preferColliderHull: settleFlatOnIce || freezePitchRoll);
            AlignMassToColliders();
            ApplyRotationConstraints();
            Physics.SyncTransforms();
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            // Stay dynamic+sleep so beam/speakers can collide; kinematic only under a player.
            SettleQuietOnIce();
            isAwake = true;
        }

        if (!rb.isKinematic)
        {
            // Ice↔Ice floor contact is unreliable — keep dynamic props from falling through.
            // Skip while kinematic: Constrain uses the collider hull and was lifting locked
            // props after PlaceOnIce seated the visible mesh flush.
            ConstrainAboveIce();

            Vector3 vel = rb.linearVelocity;
            if (vel.magnitude > maxLinearSpeed)
                rb.linearVelocity = vel.normalized * maxLinearSpeed;

            DampRunawaySpin();
            // Nudge off corners/edges, then freeze only on a face.
            ResolveUnstableRestPose();
            TrySettleToStandablePlatform();
        }
    }

    private void SetPlatformKinematic(bool kinematic)
    {
        if (rb == null)
            return;

        rb.isKinematic = kinematic;
        if (kinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            // Rectangle props: always lock on a full face flush to ice — never freeze on a corner/edge
            // unless something else is supporting a lean.
            bool propped = IsRestingOnOtherObject();
            if (!propped && !freezeRotation)
                ForceSnapNearestFaceFlat();

            Physics.SyncTransforms();
            // Flat beams: seat on fitted box face. Mesh AABB corners on a long diagonal beam
            // hang below the contact face and were floating it until stick wake + gravity.
            bool seatOnCollider = settleFlatOnIce || freezePitchRoll || !propped;
            PlaceOnIceSurface(preferColliderHull: seatOnCollider);
            Physics.SyncTransforms();
        }
    }

    private void WakeDynamicFromStick()
    {
        lastStickPushTime = Time.time;
        settleQuietTime = 0f;
        if (rb == null)
            return;

        if (rb.isKinematic)
            rb.isKinematic = false;
        rb.WakeUp();
    }

    private void TrySettleToStandablePlatform()
    {
        if (Time.time < lastStickPushTime + DynamicStickSeconds)
            return;

        // Corner/edge rest looks "quiet" but must NOT freeze kinematic — that locks the screenshot pose.
        if (!IsRestingOnFace(0.96f))
        {
            settleQuietTime = 0f;
            return;
        }

        float speed = rb.linearVelocity.magnitude;
        float spin = rb.angularVelocity.magnitude;
        if (speed > SettleSpeed || spin > SettleAngularSpeed)
        {
            settleQuietTime = 0f;
            return;
        }

        settleQuietTime += Time.fixedDeltaTime;
        if (settleQuietTime < SettleHoldSeconds)
            return;

        // Sleeping dynamic (not kinematic) so props still block each other via Ice↔Ice.
        SettleQuietOnIce();
        settleQuietTime = 0f;
    }

    /// <summary>
    /// Lock pose without kinematic — Unity kinematic↔kinematic never collides, which let
    /// settled beam/speakers phase through each other even with Ice↔Ice enabled.
    /// </summary>
    private void SettleQuietOnIce()
    {
        if (rb == null)
            return;

        bool propped = IsRestingOnOtherObject();
        if (!propped && !freezeRotation)
            ForceSnapNearestFaceFlat();

        Physics.SyncTransforms();
        PlaceOnIceSurface(preferColliderHull: settleFlatOnIce || freezePitchRoll || !propped);
        Physics.SyncTransforms();

        rb.isKinematic = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        ApplyRotationConstraints();
        rb.Sleep();
    }

    /// <summary>
    /// Y-clamp (ConstrainAboveIce) can balance a box on an edge/corner with zero velocity.
    /// Unity then auto-sleeps — looks "frozen on an edge". Upright faces pass IsRestingOnFace
    /// and get ForceSnapNearestFaceFlat; edges must stay awake and tip (or hard-snap) to a face.
    /// </summary>
    private void ResolveUnstableRestPose()
    {
        if (freezeRotation || freezePitchRoll)
            return;

        if (IsRestingOnFace(0.96f))
        {
            edgeStuckTime = 0f;
            return;
        }

        // Never let PhysX sleep mid-tip — sleeping skips torque and locks the edge pose.
        rb.WakeUp();

        if (!TryGetNearestFaceUpAxis(out Vector3 faceUp))
            return;

        float alignment = Vector3.Dot(faceUp, Vector3.up);
        float speed = rb.linearVelocity.magnitude;
        float spin = rb.angularVelocity.magnitude;
        if (speed < SettleSpeed && spin < SettleAngularSpeed)
            edgeStuckTime += Time.fixedDeltaTime;
        else
            edgeStuckTime = 0f;

        // Quiet on an edge/corner — snap nearest face flat (same end state as upright settle).
        if (edgeStuckTime >= EdgeSnapSeconds)
        {
            ForceSnapNearestFaceFlat();
            PlaceOnIceSurface(preferColliderHull: true);
            edgeStuckTime = 0f;
            return;
        }

        Vector3 axis = Vector3.Cross(faceUp, Vector3.up);
        if (axis.sqrMagnitude < 0.0001f)
            return;

        // Stronger when closer to a corner (~0.58) so gravity "wins" the metastable pose.
        float strength = Mathf.Lerp(14f, 40f, 1f - Mathf.Clamp01(alignment));
        rb.AddTorque(axis.normalized * strength, ForceMode.Acceleration);
    }

    private bool IsRestingOnFace(float minDot)
    {
        if (!TryGetNearestFaceUpAxis(out Vector3 faceUp))
            return false;

        return Vector3.Dot(faceUp, Vector3.up) >= minDot;
    }

    /// <summary>When nearly face-down and slow, snap that face flush so long sides don't rock on AABB corners.</summary>
    private void SnapNearestFaceFlatIfSettled()
    {
        if (freezeRotation)
            return;

        if (!TryGetNearestFaceUpAxis(out Vector3 faceUp))
            return;

        float upright = Vector3.Dot(faceUp, Vector3.up);
        if (upright < 0.92f)
            return;

        if (upright > 0.999f)
            return;

        rb.rotation = Quaternion.FromToRotation(faceUp, Vector3.up) * rb.rotation;
    }

    /// <summary>Hard snap: nearest box face → world up. Used on lock so rectangles never freeze tilted.</summary>
    private void ForceSnapNearestFaceFlat()
    {
        if (!TryGetNearestFaceUpAxis(out Vector3 faceUp))
            return;

        if (Vector3.Dot(faceUp, Vector3.up) > 0.9995f)
            return;

        rb.rotation = Quaternion.FromToRotation(faceUp, Vector3.up) * rb.rotation;
    }

    /// <summary>True if another solid prop/board is under us — allow a lean only then.</summary>
    private bool IsRestingOnOtherObject()
    {
        if (bodyColliders == null || bodyColliders.Length == 0)
            return false;

        foreach (Collider col in bodyColliders)
        {
            if (col == null)
                continue;

            Bounds b = col.bounds;
            Vector3 half = b.extents;
            half.x *= 0.92f;
            half.z *= 0.92f;
            half.y = Mathf.Max(half.y * 0.5f, 0.08f);

            Collider[] hits = Physics.OverlapBox(
                b.center - Vector3.up * 0.02f,
                half,
                Quaternion.identity,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                if (hit == null || IsOwnCollider(hit))
                    continue;

                if (IsPlayerOrStickCollider(hit))
                    continue;

                // Other slidables share Ice — check before skipping rink ice tiles.
                if (hit.GetComponentInParent<SlidableObstacle>() != null)
                    return true;

                string layer = LayerMask.LayerToName(hit.gameObject.layer);
                if (layer == "Ice")
                    continue;

                if (layer == "Boards" || layer == "Barrier" || layer == "Goal Post" || layer == "Default")
                {
                    // Only count if contact is roughly underneath (supporting a lean).
                    Vector3 toUs = b.center - hit.ClosestPoint(b.center);
                    if (toUs.y > 0.02f && toUs.y < 1.5f)
                        return true;
                }
            }
        }

        return false;
    }

    private void ConstrainAboveIce()
    {
        if (rb == null)
            return;

        float iceY = ResolveIceSurfaceY();

        if (!TryGetLowestColliderY(out float lowestY))
            return;

        float minLowest = iceY + IceClearance;
        float maxHighest = iceY + 6f;

        // Pulled into the sky (bad ice hit / launch) — snap back down to the rink.
        if (lowestY > maxHighest)
        {
            Vector3 pos = rb.position;
            pos.y -= lowestY - (iceY + 0.35f);
            rb.position = pos;
            if (!rb.isKinematic)
            {
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                rb.linearVelocity = vel;
            }

            return;
        }

        if (lowestY >= minLowest - 0.0005f)
            return;

        Vector3 grounded = rb.position;
        grounded.y += minLowest - lowestY;
        rb.position = grounded;

        if (!rb.isKinematic)
        {
            Vector3 vel = rb.linearVelocity;
            if (vel.y < 0f)
                vel.y = 0f;
            rb.linearVelocity = vel;
        }
    }

    private void DampRunawaySpin()
    {
        Vector3 angVel = rb.angularVelocity;
        if (angVel.sqrMagnitude <= 0.0001f)
            return;

        // Damp ice vortex (fast yaw while sliding) but allow pitch/roll tipping from gravity.
        Vector3 horizontalVel = rb.linearVelocity;
        horizontalVel.y = 0f;
        float slideSpeed = horizontalVel.magnitude;
        float yawSpin = angVel.y;
        if (slideSpeed > 1.2f && Mathf.Abs(yawSpin) > 0.35f)
        {
            float damp = Mathf.Clamp01((slideSpeed - 1.2f) / 3f) * 12f * Time.fixedDeltaTime;
            yawSpin *= 1f - damp;
        }

        Vector3 tiltSpin = new Vector3(angVel.x, 0f, angVel.z);
        float tiltMag = tiltSpin.magnitude;
        float yawCap = Mathf.Max(maxAngularSpeed, 0.5f);
        float tipCap = Mathf.Max(maxTipAngularSpeed, yawCap);

        if (Mathf.Abs(yawSpin) > yawCap)
            yawSpin = Mathf.Sign(yawSpin) * yawCap;

        if (tiltMag > tipCap)
            tiltSpin = tiltSpin.normalized * tipCap;

        angVel = new Vector3(tiltSpin.x, yawSpin, tiltSpin.z);

        float totalSpin = angVel.magnitude;
        if (totalSpin > SpinDampThreshold && slideSpeed > 2f && tiltMag < 0.2f)
        {
            float excess = (totalSpin - SpinDampThreshold) / yawCap;
            float damp = Mathf.Clamp01(excess) * 6f * Time.fixedDeltaTime;
            angVel *= 1f - damp;
        }

        rb.angularVelocity = angVel;
    }

    /// <summary>COM from collider geometry — matches cabinet appearance, no fake local-Y bias.</summary>
    private void AlignMassToColliders()
    {
        if (rb == null)
            return;

        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null && !box.isTrigger)
        {
            rb.centerOfMass = box.center;
            rb.ResetInertiaTensor();
            return;
        }

        rb.ResetCenterOfMass();
        rb.ResetInertiaTensor();
    }

    private RigidbodyConstraints BuildRotationConstraints()
    {
        if (freezeRotation)
            return RigidbodyConstraints.FreezeRotation;

        if (freezePitchRoll)
            return RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        return RigidbodyConstraints.None;
    }

    private void ApplyRotationConstraints()
    {
        rb.constraints = BuildRotationConstraints();
    }

    /// <summary>
    /// One-shot pose while kinematic — flatten onto the ice before constraints lock rotation.
    /// </summary>
    public void SnapRestPose() => SnapRestPose(forSpawn: false);

    public void SnapRestPose(bool forSpawn)
    {
        if (rb == null)
            return;

        if (settleFlatOnIce || freezePitchRoll)
            SnapFlatRotation();
        else if (forSpawn && useCabinetMeshColliders)
            SnapCabinetTallAxisUp();

        if (!collidersFitted)
        {
            if (useCabinetMeshColliders)
                SlidableObstacleSetup.FitSpeakerCabinetMeshColliders(gameObject);
            else if (settleFlatOnIce || freezePitchRoll)
            {
                // Local mesh bounds — world AABB of a long diagonal beam hangs below the face
                // and floated the prop until stick wake dropped it.
                SlidableObstacleSetup.FitBeamBoxColliderFromMeshes(gameObject);
            }
            else
                SlidableObstacleSetup.FitBoxColliderFromRenderers(gameObject);

            collidersFitted = true;
        }

        RefreshBodyColliders();
        AlignMassToColliders();
        // Flat beams: collider face after FitBox. Speakers: mesh/visual support.
        PlaceOnIceSurface(preferColliderHull: settleFlatOnIce || freezePitchRoll);
        Physics.SyncTransforms();
    }

    /// <summary>Spawn only — stand the cabinet on its tall face so gravity starts neutral.</summary>
    private void SnapCabinetTallAxisUp()
    {
        if (!SlidableObstacleSetup.TryGetCabinetLocalBounds(gameObject, out _, out Vector3 size) &&
            !TryGetLocalExtents(out size))
            return;

        Vector3 localAxis = Vector3.up;
        if (size.x >= size.y && size.x >= size.z)
            localAxis = Vector3.right;
        else if (size.z >= size.y)
            localAxis = Vector3.forward;

        Vector3 faceUp = transform.TransformDirection(localAxis).normalized;
        if (Vector3.Dot(faceUp, Vector3.up) < 0f)
            faceUp = -faceUp;

        // Already nearly upright — leave prefab lean alone so we don't fight a stable pose.
        if (Vector3.Dot(faceUp, Vector3.up) > 0.97f)
            return;

        rb.rotation = Quaternion.FromToRotation(faceUp, Vector3.up) * rb.rotation;
    }

    private void SnapFlatRotation()
    {
        if (TryGetLargestFaceUpAxis(out Vector3 faceUp))
        {
            rb.rotation = Quaternion.FromToRotation(faceUp, Vector3.up) * rb.rotation;
            return;
        }

        if (TryGetNearestFaceUpAxis(out Vector3 nearestUp))
        {
            rb.rotation = Quaternion.FromToRotation(nearestUp, Vector3.up) * rb.rotation;
            return;
        }

        if (!freezePitchRoll)
            return;

        Vector3 flatForward = transform.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.0001f)
            flatForward = transform.right;

        rb.rotation = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
    }

    private bool TryGetLargestFaceUpAxis(out Vector3 faceUp)
    {
        faceUp = transform.up;
        if (!TryGetLocalExtents(out Vector3 ext))
            return false;

        float areaX = ext.y * ext.z;
        float areaY = ext.x * ext.z;
        float areaZ = ext.x * ext.y;

        Vector3 localAxis = Vector3.up;
        if (areaX >= areaY && areaX >= areaZ)
            localAxis = Vector3.right;
        else if (areaZ >= areaY)
            localAxis = Vector3.forward;

        faceUp = transform.TransformDirection(localAxis).normalized;
        if (Vector3.Dot(faceUp, Vector3.up) < 0f)
            faceUp = -faceUp;

        return true;
    }

    private bool TryGetLocalExtents(out Vector3 extents)
    {
        extents = Vector3.zero;
        if (!SlidableObstacleSetup.TryGetCombinedRendererBounds(gameObject, out Bounds worldBounds))
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
            Vector3 local = transform.InverseTransformPoint(corner);
            min = Vector3.Min(min, local);
            max = Vector3.Max(max, local);
        }

        extents = max - min;
        extents = new Vector3(Mathf.Abs(extents.x), Mathf.Abs(extents.y), Mathf.Abs(extents.z));
        return extents.sqrMagnitude > 0.0001f;
    }

    /// <summary>Pick the local face normal already closest to world up — avoids edge balances.</summary>
    private bool TryGetNearestFaceUpAxis(out Vector3 faceUp)
    {
        faceUp = transform.up;
        float bestDot = -2f;

        Vector3[] axes =
        {
            transform.up, -transform.up,
            transform.right, -transform.right,
            transform.forward, -transform.forward
        };

        foreach (Vector3 axis in axes)
        {
            if (axis.sqrMagnitude < 0.0001f)
                continue;

            Vector3 normal = axis.normalized;
            float dot = Vector3.Dot(normal, Vector3.up);
            if (dot > bestDot)
            {
                bestDot = dot;
                faceUp = normal;
            }
        }

        return bestDot > -0.999f;
    }

    private void PlaceOnIceSurface(bool preferColliderHull = false)
    {
        float iceY = ResolveIceSurfaceY();

        float lowestY;
        bool haveLowest = false;
        if (preferColliderHull)
            haveLowest = TryGetLowestColliderY(out lowestY);
        else
            haveLowest = TryGetLowestVisualY(out lowestY);

        if (!haveLowest && !TryGetLowestVisualY(out lowestY) && !TryGetLowestColliderY(out lowestY))
            lowestY = transform.position.y - 0.25f;

        float targetLowest = iceY + IceClearance;
        float lift = targetLowest - lowestY;

        // Never yeet props into the sky if support math or a bad hit goes wrong.
        if (lift > 3f || lift < -8f)
        {
            Debug.LogWarning("[FlamiePrac] PlaceOnIce clamp on '" + name +
                             "' lift=" + lift.ToString("F2") + " iceY=" + iceY.ToString("F2") +
                             " lowest=" + lowestY.ToString("F2"));
            Vector3 safe = rb.position;
            safe.y = iceY + 0.35f;
            rb.position = safe;
            return;
        }

        if (Mathf.Abs(lift) > 0.0005f)
            rb.position += Vector3.up * lift;
    }

    private bool TryGetLowestVisualY(out float lowestY)
    {
        lowestY = float.MaxValue;
        bool found = false;

        // MeshFilters first — don't require renderer.enabled (dedicated may disable renderers).
        foreach (MeshFilter mf in GetComponentsInChildren<MeshFilter>(true))
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
                Vector3 local = c + Vector3.Scale(e, new Vector3(xi, yi, zi));
                lowestY = Mathf.Min(lowestY, mf.transform.TransformPoint(local).y);
                found = true;
            }
        }

        if (found)
            return true;

        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
                continue;

            string lower = renderer.gameObject.name.ToLowerInvariant();
            if (lower.Contains("sticker") || lower.Contains("decal") || lower.Contains("phl"))
                continue;

            lowestY = Mathf.Min(lowestY, renderer.bounds.min.y);
            found = true;
        }

        return found;
    }

    private bool TryGetLowestMeshY(out float lowestY)
    {
        lowestY = float.MaxValue;
        bool found = false;

        foreach (MeshFilter meshFilter in GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter == null || meshFilter.sharedMesh == null)
                continue;

            Bounds bounds = meshFilter.sharedMesh.bounds;
            Vector3 worldCenter = meshFilter.transform.TransformPoint(bounds.center);
            Vector3 worldExtents = meshFilter.transform.TransformVector(bounds.extents);
            float minY = worldCenter.y - Mathf.Abs(worldExtents.y);
            lowestY = Mathf.Min(lowestY, minY);
            found = true;
        }

        return found;
    }

    private float ResolveIceSurfaceY()
    {
        float fallback = rootTransform != null ? rootTransform.position.y : 0f;
        if (TryRaycastRinkIceSurface(out float rayY))
            return rayY;

        return fallback;
    }

    /// <summary>
    /// True rink ice under the prop — not glass, not hive Ice hitboxes (those sit slightly high
    /// and made movables float on the "wrong" ice texture).
    /// </summary>
    private bool TryRaycastRinkIceSurface(out float surfaceY)
    {
        float expectedY = rootTransform != null ? rootTransform.position.y : 0f;
        surfaceY = expectedY;

        Vector3 probe = transform.position;
        float originY = expectedY + 3f;
        Vector3 origin = new Vector3(probe.x, originY, probe.z);

        int iceLayer = LayerMask.NameToLayer("Ice");
        int mask = iceLayer >= 0 ? (1 << iceLayer) : Physics.DefaultRaycastLayers;

        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 20f, mask, QueryTriggerInteraction.Ignore);

        float bestRinkY = float.PositiveInfinity;
        bool foundRink = false;
        float bestAnyY = float.PositiveInfinity;
        bool foundAny = false;

        foreach (RaycastHit hit in hits)
        {
            if (!IsUsableIceHit(hit, expectedY))
                continue;

            // Hive/training Ice meshes are the wrong "texture" — slightly elevated vs rink ice.
            if (IsTrainingPropCollider(hit.collider))
            {
                if (hit.point.y < bestAnyY)
                {
                    bestAnyY = hit.point.y;
                    foundAny = true;
                }

                continue;
            }

            if (hit.point.y < bestRinkY)
            {
                bestRinkY = hit.point.y;
                foundRink = true;
            }
        }

        if (foundRink)
        {
            surfaceY = bestRinkY;
            return true;
        }

        // Fallback: any non-training hit on Default layers near expected ice.
        hits = Physics.RaycastAll(origin, Vector3.down, 20f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        foreach (RaycastHit hit in hits)
        {
            if (!IsUsableIceHit(hit, expectedY))
                continue;

            if (IsTrainingPropCollider(hit.collider))
                continue;

            if (hit.point.y < bestRinkY)
            {
                bestRinkY = hit.point.y;
                foundRink = true;
            }
        }

        if (foundRink)
        {
            surfaceY = bestRinkY;
            return true;
        }

        if (foundAny)
        {
            surfaceY = bestAnyY;
            return true;
        }

        return false;
    }

    private bool IsUsableIceHit(RaycastHit hit, float expectedY)
    {
        if (hit.collider == null || hit.collider.isTrigger)
            return false;

        if (IsOwnCollider(hit.collider))
            return false;

        if (hit.collider.GetComponentInParent<SlidableObstacle>() != null)
            return false;

        if (IsPlayerOrStickCollider(hit.collider))
            return false;

        // Reject glass / ceiling.
        if (hit.point.y > expectedY + 0.75f || hit.point.y < expectedY - 2.5f)
            return false;

        // Prefer upward-facing floor (skip vertical boards/glass sides).
        if (Vector3.Dot(hit.normal, Vector3.up) < 0.7f)
            return false;

        return true;
    }

    private static bool IsTrainingPropCollider(Collider col)
    {
        if (col == null)
            return false;

        if (col.GetComponentInParent<TrainingSyncMarker>() != null)
            return true;

        Transform t = col.transform;
        while (t != null)
        {
            string n = t.name;
            if (n.StartsWith("Training_", StringComparison.Ordinal) ||
                n.StartsWith("Train_", StringComparison.Ordinal) ||
                n.IndexOf("trainingprefab", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            t = t.parent;
        }

        return false;
    }

    private static bool IsPlayerOrStickCollider(Collider col)
    {
        if (col == null)
            return false;

        return col.GetComponentInParent<Stick>() != null ||
               col.GetComponentInParent<PlayerBody>() != null ||
               col.GetComponentInParent<Player>() != null;
    }

    private bool TryGetLowestColliderY(out float lowestY)
    {
        lowestY = float.MaxValue;
        if (bodyColliders == null || bodyColliders.Length == 0)
            return false;

        bool found = false;
        foreach (Collider col in bodyColliders)
        {
            if (col == null)
                continue;

            // World AABB.min.y is wrong for rotated boxes (corner hangs below the flat face).
            if (TryGetColliderSupportY(col, out float supportY))
            {
                lowestY = Mathf.Min(lowestY, supportY);
                found = true;
            }
            else
            {
                lowestY = Mathf.Min(lowestY, col.bounds.min.y);
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Support height for ice seating. When a box face is mostly down, use that face's
    /// four corners (not the absolute lowest AABB corner) so a slight tip doesn't pin
    /// an edge and refuse to settle flush.
    /// </summary>
    private static bool TryGetColliderSupportY(Collider col, out float supportY)
    {
        supportY = float.MaxValue;
        if (col == null)
            return false;

        BoxCollider box = col as BoxCollider;
        if (box != null)
        {
            Transform t = box.transform;
            Vector3 c = box.center;
            Vector3 h = box.size * 0.5f;

            // Pick the local axis most aligned with world down — that's the contact face.
            Vector3 downLocal = t.InverseTransformDirection(Vector3.down).normalized;
            Vector3 abs = new Vector3(Mathf.Abs(downLocal.x), Mathf.Abs(downLocal.y), Mathf.Abs(downLocal.z));
            int axis = 0;
            if (abs.y >= abs.x && abs.y >= abs.z)
                axis = 1;
            else if (abs.z >= abs.x)
                axis = 2;

            float faceSign = axis == 0 ? Mathf.Sign(downLocal.x)
                : axis == 1 ? Mathf.Sign(downLocal.y)
                : Mathf.Sign(downLocal.z);
            if (Mathf.Abs(faceSign) < 0.01f)
                faceSign = -1f;

            // Face alignment: if nearly on a face, sample only that face; else all 8 corners.
            float faceDot = axis == 0 ? abs.x : axis == 1 ? abs.y : abs.z;
            bool faceMode = faceDot >= 0.85f;

            for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
            for (int zi = -1; zi <= 1; zi += 2)
            {
                if (faceMode)
                {
                    if (axis == 0 && xi != (int)faceSign)
                        continue;
                    if (axis == 1 && yi != (int)faceSign)
                        continue;
                    if (axis == 2 && zi != (int)faceSign)
                        continue;
                }

                Vector3 local = c + Vector3.Scale(h, new Vector3(xi, yi, zi));
                supportY = Mathf.Min(supportY, t.TransformPoint(local).y);
            }

            return supportY < float.MaxValue;
        }

        // Fallback: sample ClosestPoint from below the collider.
        Bounds b = col.bounds;
        Vector3 probe = new Vector3(b.center.x, b.min.y - 2f, b.center.z);
        supportY = col.ClosestPoint(probe).y;
        return true;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServerSide() || rb == null || collision == null)
            return;

        if (SlidableBoardCollision.IsBoardLayer(collision.collider.gameObject.layer))
            CancelBoardVelocity(collision);

        if (IsPlayerBodyCollider(collision.collider))
            HandlePlayerBodyContact(collision, isEnter: true);
    }

    private void OnCollisionStay(Collision collision)
    {
        if (!IsServerSide() || rb == null || collision == null)
            return;

        if (SlidableBoardCollision.IsBoardLayer(collision.collider.gameObject.layer))
        {
            CancelBoardVelocity(collision);
            return;
        }

        if (IsPlayerBodyCollider(collision.collider))
        {
            HandlePlayerBodyContact(collision, isEnter: false);
            return;
        }

        Stick stick = collision.collider.GetComponentInParent<Stick>();
        if (stick == null || stick.Rigidbody == null)
            return;

        if (!allowStickPush)
            return;

        if (stick.Player != null && stick.Player.IsReplay.Value)
            return;

        if (lastStickPushFrame == Time.frameCount)
            return;

        Vector3 push = stick.StickPositioner != null
            ? stick.StickPositioner.BladeTargetVelocity * 1.15f
            : Vector3.zero;
        Vector3 bladeVel = stick.Rigidbody.GetPointVelocity(stick.BladeHandlePosition);
        push += bladeVel;

        Vector3 horizontal = push;
        horizontal.y = 0f;
        if (horizontal.sqrMagnitude < 0.05f)
            return;

        WakeDynamicFromStick();

        float bladeSpeed = Mathf.Min(horizontal.magnitude, MaxStickBladeSpeed);
        Vector3 slideForce = horizontal.normalized * (bladeSpeed * stickPushForceScale);

        // Center-of-mass push: slide on ice instead of vortex-spinning from off-center contact torque.
        rb.AddForce(slideForce, ForceMode.Force);

        if (!freezeRotation && !freezePitchRoll)
            ApplyGentleTipTorque(collision, slideForce);

        lastStickPushFrame = Time.frameCount;
    }

    private void CancelBoardVelocity(Collision collision)
    {
        if (!rb.isKinematic)
            SlidableBoardCollision.CancelVelocityIntoBoard(rb, collision);
    }

    private static bool IsPlayerBodyCollider(Collider col)
    {
        return col != null && col.GetComponentInParent<PlayerBody>() != null;
    }

    /// <summary>
    /// Top contact: standable platform (jump on / skate / jump off).
    /// Side contact: solid for skate-ins (don't get tossed) + wipe the player; stick still moves us.
    /// </summary>
    private void HandlePlayerBodyContact(Collision collision, bool isEnter)
    {
        if (collision.contactCount <= 0)
            return;

        PlayerBody playerBody = collision.collider.GetComponentInParent<PlayerBody>();
        if (playerBody == null)
            return;

        bool standingOnTop = false;
        Vector3 avgNormal = Vector3.zero;
        for (int i = 0; i < collision.contactCount; i++)
        {
            Vector3 n = collision.GetContact(i).normal;
            avgNormal += n;
            // Contact normal points toward us from the other collider; upward means they are on top.
            if (Vector3.Dot(n, Vector3.up) > 0.45f)
                standingOnTop = true;
        }

        if (avgNormal.sqrMagnitude > 0.0001f)
            avgNormal.Normalize();
        else
            avgNormal = Vector3.up;

        if (standingOnTop)
        {
            // Never freeze a corner-balanced speaker under the player.
            if (!IsRestingOnFace(0.92f))
                return;

            if (!rb.isKinematic)
                SetPlatformKinematic(true);
            return;
        }

        // Side/body check — stick authority still moves the prop; body skates should not yeet it.
        ResistPlayerBodyShove();
        TryWipePlayerFromBodyHit(playerBody, collision, avgNormal, isEnter);
    }

    private bool IsStickPushActive()
    {
        return Time.time < lastStickPushTime + DynamicStickSeconds;
    }

    /// <summary>Kill skate-shove momentum unless the stick recently launched us.</summary>
    private void ResistPlayerBodyShove()
    {
        if (rb == null || rb.isKinematic || IsStickPushActive())
            return;

        Vector3 vel = rb.linearVelocity;
        vel.x = 0f;
        vel.z = 0f;
        if (vel.y > 0f)
            vel.y = 0f;
        rb.linearVelocity = vel;
        rb.angularVelocity = Vector3.zero;
    }

    private void TryWipePlayerFromBodyHit(
        PlayerBody playerBody,
        Collision collision,
        Vector3 contactNormal,
        bool isEnter)
    {
        if (playerBody == null)
            return;

        if (playerBody.HasSlipped || playerBody.HasFallen.Value)
            return;

        Player player = playerBody.Player;
        if (player != null && player.IsReplay.Value)
            return;

        // MaxPractice AI goalie — don't farm slips on the fake body.
        if (FakePlayerDetector.IsAnyFakePlayerBody(playerBody))
            return;

        if (!isEnter && Time.time < lastBodyWipeTime + BodyWipeCooldown)
            return;

        float propHoriz = rb != null && !rb.isKinematic
            ? new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z).magnitude
            : 0f;
        float playerSpeed = playerBody.Speed != null ? playerBody.Speed.Value : 0f;

        Vector3 rel = collision.relativeVelocity;
        Vector3 relHoriz = new Vector3(rel.x, 0f, rel.z);
        float closing = Mathf.Abs(Vector3.Dot(relHoriz, contactNormal));
        if (closing < 0.01f)
            closing = relHoriz.magnitude;

        bool propPlowsPlayer = propHoriz >= PropIntoPlayerSpeed && IsStickPushActive();
        // Moving prop can wipe even after stick window if it still carries speed.
        if (!propPlowsPlayer && propHoriz >= PropIntoPlayerSpeed * 1.35f)
            propPlowsPlayer = true;

        bool skateIntoProp = playerSpeed >= SkateIntoPlayerSpeed && closing >= BodyHitClosingSpeed;
        float force = 0f;
        try
        {
            force = Utils.GetCollisionForce(collision);
        }
        catch
        {
            force = closing;
        }

        if (!propPlowsPlayer && !skateIntoProp && force < 6.5f)
            return;

        if (Time.time < lastBodyWipeTime + BodyWipeCooldown)
            return;

        lastBodyWipeTime = Time.time;
        playerBody.OnSlip();

        Rigidbody playerRb = playerBody.Rigidbody;
        if (playerRb == null)
            return;

        Vector3 bounce = Vector3.ClampMagnitude(relHoriz, BodyHitBounceMax);
        if (bounce.sqrMagnitude < 0.25f)
        {
            Vector3 away = playerBody.transform.position - transform.position;
            away.y = 0f;
            bounce = away.sqrMagnitude > 0.0001f
                ? away.normalized * Mathf.Max(playerSpeed, propHoriz) * 0.65f
                : -contactNormal * 4f;
            bounce.y = 0f;
            bounce = Vector3.ClampMagnitude(bounce, BodyHitBounceMax);
        }

        // Match tackle feel: impulse at chest height.
        Vector3 at = playerRb.worldCenterOfMass + playerBody.transform.up * 0.5f;
        playerRb.AddForceAtPosition(-bounce * BodyHitBounceScale, at, ForceMode.Impulse);
    }

    /// <summary>Small capped torque when the blade hits high/low — enough to tip, not helicopter.</summary>
    private void ApplyGentleTipTorque(Collision collision, Vector3 slideForce)
    {
        if (collision.contactCount <= 0 || slideForce.sqrMagnitude < 0.0001f)
            return;

        Vector3 avgContact = Vector3.zero;
        int contactCount = collision.contactCount;
        for (int i = 0; i < contactCount; i++)
            avgContact += collision.GetContact(i).point;

        avgContact /= contactCount;

        float heightDiff = avgContact.y - rb.worldCenterOfMass.y;
        if (Mathf.Abs(heightDiff) < 0.06f)
            return;

        Vector3 tipAxis = Vector3.Cross(Vector3.up, slideForce.normalized);
        if (tipAxis.sqrMagnitude < 0.0001f)
            return;

        float tipStrength = Mathf.Clamp(heightDiff * slideForce.magnitude * 0.022f, -28f, 28f);
        rb.AddTorque(tipAxis.normalized * tipStrength, ForceMode.Force);
    }

    private bool IsOwnCollider(Collider col)
    {
        if (col == null || bodyColliders == null)
            return false;

        foreach (Collider own in bodyColliders)
        {
            if (own == col)
                return true;
        }

        return false;
    }

    private bool TryGetClosestPoint(Vector3 worldPoint, out Vector3 closest)
    {
        closest = transform.position;
        if (bodyColliders == null || bodyColliders.Length == 0)
            return false;

        float bestDist = float.MaxValue;
        bool found = false;
        foreach (Collider col in bodyColliders)
        {
            if (col == null)
                continue;

            Vector3 point = col.ClosestPoint(worldPoint);
            float dist = (point - worldPoint).sqrMagnitude;
            if (dist < bestDist)
            {
                bestDist = dist;
                closest = point;
                found = true;
            }
        }

        return found;
    }

    private bool TryGetCombinedColliderBounds(out Bounds bounds)
    {
        bounds = default;
        if (bodyColliders == null || bodyColliders.Length == 0)
            return false;

        bool hasBounds = false;
        foreach (Collider col in bodyColliders)
        {
            if (col == null)
                continue;

            if (!hasBounds)
            {
                bounds = col.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(col.bounds);
            }
        }

        return hasBounds;
    }

    public void WriteState(FastBufferWriter writer)
    {
        writer.WriteValueSafe(ParentSyncId);
        WriteString(writer, SlidableObstacleSetup.CanonicalSlidablePath(RelativePath));

        // World pose — server unparents slidables after init; hive-local coords on the client
        // left the visual at prefab rest while the hitbox sat on the ice-seated server pose.
        Vector3 worldPos = transform.position;
        Quaternion worldRot = transform.rotation;
        Vector3 worldVel = rb != null && !rb.isKinematic ? rb.linearVelocity : Vector3.zero;
        Vector3 worldAng = rb != null && !rb.isKinematic ? rb.angularVelocity : Vector3.zero;

        writer.WriteValueSafe(worldPos.x);
        writer.WriteValueSafe(worldPos.y);
        writer.WriteValueSafe(worldPos.z);
        writer.WriteValueSafe(worldRot.x);
        writer.WriteValueSafe(worldRot.y);
        writer.WriteValueSafe(worldRot.z);
        writer.WriteValueSafe(worldRot.w);
        writer.WriteValueSafe(worldVel.x);
        writer.WriteValueSafe(worldVel.y);
        writer.WriteValueSafe(worldVel.z);
        writer.WriteValueSafe(worldAng.x);
        writer.WriteValueSafe(worldAng.y);
        writer.WriteValueSafe(worldAng.z);
    }

    /// <summary>True when clients need high-rate pose updates for this prop.</summary>
    public bool IsActivelyMoving()
    {
        if (rb == null || !isAwake)
            return false;

        if (!rb.isKinematic)
        {
            return rb.linearVelocity.sqrMagnitude > 0.0004f ||
                   rb.angularVelocity.sqrMagnitude > 0.0004f;
        }

        // Kinematic seated platforms still must stream the post-snap world pose every tick;
        // otherwise clients keep the prefab rest pose and walk through the mesh.
        return true;
    }

    private static void WriteString(FastBufferWriter writer, string value)
    {
        value ??= string.Empty;
        writer.WriteValueSafe(value.Length);
        for (int i = 0; i < value.Length; i++)
            writer.WriteValueSafe(value[i]);
    }

    private static bool IsServerSide()
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm == null || nm.IsServer;
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>Client-side visual follower for a slidable training prop (world-space poses).</summary>
public class SlidableObstacleVisual : MonoBehaviour
{
    // Light smoothing only — server already sends at network tick rate.
    private const float PositionSmoothTime = 0.018f;
    private const float RotationLerpRate = 45f;
    private const float SnapDistance = 1.25f;

    public int ParentSyncId { get; private set; }
    public string RelativePath { get; private set; }

    private Vector3 networkWorldPos;
    private Quaternion networkWorldRot = Quaternion.identity;
    private Vector3 networkWorldVel;
    private Vector3 networkWorldAngVel;
    private float stateTime;
    private bool hasState;
    private bool loggedFirstState;
    private Vector3 posSmoothVelocity;

    public void Initialize(int syncId, string relativePath)
    {
        ParentSyncId = syncId;
        RelativePath = relativePath ?? string.Empty;
        networkWorldPos = transform.position;
        networkWorldRot = transform.rotation;
        hasState = false;
        loggedFirstState = false;
        posSmoothVelocity = Vector3.zero;
    }

    public void ApplyState(Vector3 worldPos, Quaternion worldRot, Vector3 worldVel, Vector3 worldAngVel)
    {
        networkWorldPos = worldPos;
        networkWorldRot = worldRot;
        networkWorldVel = worldVel;
        networkWorldAngVel = worldAngVel;
        stateTime = Time.time;
        hasState = true;

        // Apply immediately — don't wait for LateUpdate (avoids one-frame stick ghosting).
        transform.SetPositionAndRotation(worldPos, worldRot);
        posSmoothVelocity = Vector3.zero;

        if (!loggedFirstState)
        {
            loggedFirstState = true;
            int renderers = GetComponentsInChildren<Renderer>(true).Length;
            Debug.Log("[FlamiePrac] Slidable visual locked to server pose: " + RelativePath +
                      " syncId=" + ParentSyncId + " pos=" + worldPos.ToString("F2") +
                      " renderers=" + renderers);
        }
    }

    private void LateUpdate()
    {
        if (!hasState)
            return;

        float age = Mathf.Clamp(Time.time - stateTime, 0f, 0.04f);
        Vector3 predictedPos = networkWorldPos + networkWorldVel * age;
        Quaternion predictedRot = networkWorldRot;
        if (networkWorldAngVel.sqrMagnitude > 0.0001f)
        {
            predictedRot = Quaternion.AngleAxis(
                networkWorldAngVel.magnitude * Mathf.Rad2Deg * age,
                networkWorldAngVel.normalized) * networkWorldRot;
        }

        float err = Vector3.Distance(transform.position, predictedPos);
        float angErr = Quaternion.Angle(transform.rotation, predictedRot);

        // Beams sit still most of the time — hard-snap small errors so the mesh can't lag
        // beside the server hitbox after ice-seat / flatten.
        if (err > 0.05f || angErr > 2f || err > SnapDistance)
        {
            transform.SetPositionAndRotation(predictedPos, predictedRot);
            posSmoothVelocity = Vector3.zero;
            return;
        }

        Vector3 smoothed = Vector3.SmoothDamp(
            transform.position,
            predictedPos,
            ref posSmoothVelocity,
            PositionSmoothTime,
            Mathf.Infinity,
            Time.deltaTime);

        float rotT = 1f - Mathf.Exp(-RotationLerpRate * Time.deltaTime);
        Quaternion smoothedRot = Quaternion.Slerp(transform.rotation, predictedRot, rotT);
        transform.SetPositionAndRotation(smoothed, smoothedRot);
    }
}
