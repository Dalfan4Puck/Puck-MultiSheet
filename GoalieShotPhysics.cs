using System;
using System.Reflection;
using MaxPractice;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Simulates airborne puck flight using the same drag model as Competitive Puck Tweaks
    /// (linear damping + optional speed-cubic and height drag), then searches launch angles
    /// so shots land on the chosen net point at the goal plane.
    /// </summary>
    internal static class GoalieShotPhysics
    {
        internal enum GoalieShotStyle
        {
            Direct,
            Rising,
            Rainbow
        }

        internal struct PuckFlightModel
        {
            internal float Mass;
            internal float BaseDrag;
            internal float MaxSpeed;
            internal float FixedDt;
            internal bool SpeedDependentDrag;
            internal float NominalSpeed;
            internal float DragFactor;
            internal bool HeightDependentDrag;
            internal float HeightLimit;
            internal float HeightDragFactor;

            internal static PuckFlightModel FromPuck(Puck puck)
            {
                Rigidbody rb = puck != null ? puck.Rigidbody : null;
                float dt = Time.fixedDeltaTime > 0.0001f ? Time.fixedDeltaTime : 0.01f;

                return new PuckFlightModel
                {
                    Mass = rb != null ? rb.mass : 0.375f,
                    BaseDrag = rb != null ? rb.linearDamping : 0.3f,
                    MaxSpeed = ReadPuckMaxSpeed(puck, 50f),
                    FixedDt = dt,
                    // CPT defaults when server runs CompetitivePuckTweaks.
                    SpeedDependentDrag = true,
                    NominalSpeed = 20f,
                    DragFactor = 0.0014f,
                    HeightDependentDrag = false,
                    HeightLimit = 2f,
                    HeightDragFactor = 0f,
                };
            }
        }

        internal struct GoalieShotLaunch
        {
            internal Vector3 Velocity;
            internal float TravelTime;
        }

        private const float MaxSimSeconds = 8f;
        private const float GoalHitTolerance = 0.45f;
        /// <summary>NHL-ish crossbar height above ice — reject goal-plane hits above this.</summary>
        private const float CrossbarHeight = 1.22f;
        private const float CrossbarClearance = 0.04f;
        /// <summary>Keep arcing shots under the arena shell (Hangar / glass ceiling colliders).</summary>
        private const float CeilingProbeHeight = 80f;
        private const float CeilingClearance = 0.45f;
        private const float FallbackCeilingAboveIce = 14.5f;
        /// <summary>Puck center at or below this is treated as ice contact.</summary>
        private const float IceContactMargin = 0.11f;
        /// <summary>Arcing shots must still be moving when they reach the goal plane.</summary>
        private const float MinArcGoalSpeedMps = 12f;
        private const float MinArcDescentMps = 1.25f;

        private static float cachedCeilingY = float.NaN;
        private static Vector3 cachedCeilingSample = new Vector3(float.NaN, float.NaN, float.NaN);

        internal static void ResetCeilingCache()
        {
            cachedCeilingY = float.NaN;
            cachedCeilingSample = new Vector3(float.NaN, float.NaN, float.NaN);
        }

        internal static GoalieShotStyle PickShotStyle(float distanceT)
        {
            float roll = UnityEngine.Random.value;
            if (distanceT > 0.45f && roll < 0.42f)
                return GoalieShotStyle.Rainbow;

            // Close-range loft clears the crossbar too easily — prefer flat / mild rise.
            if (distanceT < 0.25f)
                return roll < 0.22f ? GoalieShotStyle.Rising : GoalieShotStyle.Direct;

            if (roll < 0.52f)
                return GoalieShotStyle.Rising;
            return GoalieShotStyle.Direct;
        }

        internal static float[] PickReleaseSpeedCandidatesMph(bool preferFast)
        {
            // Slightly slower than prior bands so goalies have a beat more track time.
            float slow = UnityEngine.Random.Range(46f, 62f);
            float medium = UnityEngine.Random.Range(62f, 80f);
            float fast = UnityEngine.Random.Range(80f, 96f);

            if (preferFast)
                return new[] { fast, fast + UnityEngine.Random.Range(-6f, 4f), medium, slow + 8f };

            float primary = UnityEngine.Random.value < 0.33f ? slow
                : UnityEngine.Random.value < 0.5f ? medium
                : fast;
            return new[] { primary, slow, medium, fast, primary + UnityEngine.Random.Range(-12f, 12f) };
        }

        internal static bool TryBuildLaunch(
            Vector3 spawnPos,
            Vector3 aimPoint,
            float goalPlaneZ,
            bool shootFromPositiveZ,
            GoalieShotStyle style,
            bool preferFast,
            Puck puck,
            out GoalieShotLaunch launch)
        {
            launch = default;
            PuckFlightModel model = PuckFlightModel.FromPuck(puck);
            float ceilingY = ResolveCeilingY(spawnPos);

            Vector3 flat = aimPoint - spawnPos;
            flat.y = 0f;
            float horizontalDist = flat.magnitude;
            if (horizontalDist < 0.01f)
                flat = shootFromPositiveZ ? Vector3.back : Vector3.forward;
            else
                flat /= horizontalDist;

            GetStyleAngleRange(style, horizontalDist, out float minDeg, out float maxDeg);
            float distanceT = Mathf.InverseLerp(8f, 52f, horizontalDist);
            float iceY = spawnPos.y;

            float bestError = float.MaxValue;
            Vector3 bestVelocity = Vector3.zero;
            float bestTime = 0f;

            float[] speedCandidates = PickReleaseSpeedCandidatesMph(preferFast);
            for (int s = 0; s < speedCandidates.Length; s++)
            {
                float speedMps = PracticeHelpers.MphToMps(Mathf.Clamp(speedCandidates[s], 44f, 100f));
                float searchMinDeg = minDeg;
                float searchMaxDeg = maxDeg;
                CapAngleRangeForCeiling(spawnPos, ceilingY, speedMps, ref searchMinDeg, ref searchMaxDeg);
                for (int step = 0; step <= 40; step++)
                {
                    float angleDeg = Mathf.Lerp(searchMinDeg, searchMaxDeg, step / 40f);
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    Vector3 velocity = flat * (Mathf.Cos(angleRad) * speedMps)
                        + Vector3.up * (Mathf.Sin(angleRad) * speedMps);

                    if (!SimulateToGoalPlane(
                            spawnPos,
                            velocity,
                            goalPlaneZ,
                            shootFromPositiveZ,
                            ceilingY,
                            iceY,
                            aimPoint.y,
                            style,
                            model,
                            out Vector3 hitPoint,
                            out float travelTime,
                            out Vector3 hitVelocity))
                    {
                        continue;
                    }

                    float error = ShotQualityError(hitPoint, aimPoint, hitVelocity, style, iceY, distanceT);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestVelocity = velocity;
                        bestTime = travelTime;
                    }
                }
            }

            if (bestError > GoalHitTolerance * 0.6f && bestVelocity.sqrMagnitude > 0.01f)
                RefineAngleSearch(
                    spawnPos,
                    aimPoint,
                    goalPlaneZ,
                    shootFromPositiveZ,
                    style,
                    model,
                    flat,
                    bestVelocity.magnitude,
                    minDeg,
                    maxDeg,
                    ceilingY,
                    iceY,
                    distanceT,
                    ref bestError,
                    ref bestVelocity,
                    ref bestTime);

            if (bestError > GoalHitTolerance || bestVelocity.sqrMagnitude < 0.01f || bestTime < 0.05f)
            {
                if (style != GoalieShotStyle.Direct
                    && TryArcingSpeedRetry(
                        spawnPos,
                        aimPoint,
                        goalPlaneZ,
                        shootFromPositiveZ,
                        style,
                        model,
                        flat,
                        minDeg,
                        maxDeg,
                        ceilingY,
                        iceY,
                        distanceT,
                        ref bestError,
                        ref bestVelocity,
                        ref bestTime))
                {
                    // High-speed pass found a valid airborne arc.
                }
            }

            if (bestError > GoalHitTolerance || bestVelocity.sqrMagnitude < 0.01f || bestTime < 0.05f)
                return TryBallisticFallback(
                    spawnPos,
                    aimPoint,
                    goalPlaneZ,
                    shootFromPositiveZ,
                    style,
                    preferFast,
                    flat,
                    ceilingY,
                    iceY,
                    model,
                    out launch);

            launch = new GoalieShotLaunch
            {
                Velocity = bestVelocity,
                TravelTime = bestTime,
            };
            return true;
        }

        private static bool TryArcingSpeedRetry(
            Vector3 spawnPos,
            Vector3 aimPoint,
            float goalPlaneZ,
            bool shootFromPositiveZ,
            GoalieShotStyle style,
            PuckFlightModel model,
            Vector3 flat,
            float minDeg,
            float maxDeg,
            float ceilingY,
            float iceY,
            float distanceT,
            ref float bestError,
            ref Vector3 bestVelocity,
            ref float bestTime)
        {
            bool improved = false;
            for (int s = 0; s < 6; s++)
            {
                float mph = Mathf.Lerp(78f, 100f, s / 5f);
                float speedMps = PracticeHelpers.MphToMps(mph);
                float searchMinDeg = minDeg;
                float searchMaxDeg = maxDeg;
                CapAngleRangeForCeiling(spawnPos, ceilingY, speedMps, ref searchMinDeg, ref searchMaxDeg);

                for (int step = 0; step <= 32; step++)
                {
                    float angleDeg = Mathf.Lerp(searchMinDeg, searchMaxDeg, step / 32f);
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    Vector3 velocity = flat * (Mathf.Cos(angleRad) * speedMps)
                        + Vector3.up * (Mathf.Sin(angleRad) * speedMps);

                    if (!SimulateToGoalPlane(
                            spawnPos,
                            velocity,
                            goalPlaneZ,
                            shootFromPositiveZ,
                            ceilingY,
                            iceY,
                            aimPoint.y,
                            style,
                            model,
                            out Vector3 hitPoint,
                            out float travelTime,
                            out Vector3 hitVelocity))
                    {
                        continue;
                    }

                    float error = ShotQualityError(hitPoint, aimPoint, hitVelocity, style, iceY, distanceT);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestVelocity = velocity;
                        bestTime = travelTime;
                        improved = true;
                    }
                }
            }

            return improved && bestError <= GoalHitTolerance;
        }

        private static void RefineAngleSearch(
            Vector3 spawnPos,
            Vector3 aimPoint,
            float goalPlaneZ,
            bool shootFromPositiveZ,
            GoalieShotStyle style,
            PuckFlightModel model,
            Vector3 flatDir,
            float speed,
            float minDeg,
            float maxDeg,
            float ceilingY,
            float iceY,
            float distanceT,
            ref float bestError,
            ref Vector3 bestVelocity,
            ref float bestTime)
        {
            float lo = minDeg;
            float hi = maxDeg;
            CapAngleRangeForCeiling(spawnPos, ceilingY, speed, ref lo, ref hi);
            float centerAngle = Mathf.Atan2(bestVelocity.y, new Vector3(bestVelocity.x, 0f, bestVelocity.z).magnitude)
                * Mathf.Rad2Deg;
            lo = Mathf.Max(minDeg, centerAngle - 8f);
            hi = Mathf.Min(maxDeg, centerAngle + 8f);

            for (int step = 0; step <= 24; step++)
            {
                float angleDeg = Mathf.Lerp(lo, hi, step / 24f);
                float angleRad = angleDeg * Mathf.Deg2Rad;
                Vector3 velocity = flatDir * (Mathf.Cos(angleRad) * speed)
                    + Vector3.up * (Mathf.Sin(angleRad) * speed);

                if (!SimulateToGoalPlane(
                        spawnPos,
                        velocity,
                        goalPlaneZ,
                        shootFromPositiveZ,
                        ceilingY,
                        iceY,
                        aimPoint.y,
                        style,
                        model,
                        out Vector3 hitPoint,
                        out float travelTime,
                        out Vector3 hitVelocity))
                {
                    continue;
                }

                float error = ShotQualityError(hitPoint, aimPoint, hitVelocity, style, iceY, distanceT);
                if (error < bestError)
                {
                    bestError = error;
                    bestVelocity = velocity;
                    bestTime = travelTime;
                }
            }
        }

        private static bool TryBallisticFallback(
            Vector3 spawnPos,
            Vector3 aimPoint,
            float goalPlaneZ,
            bool shootFromPositiveZ,
            GoalieShotStyle style,
            bool preferFast,
            Vector3 flatDir,
            float ceilingY,
            float iceY,
            PuckFlightModel model,
            out GoalieShotLaunch launch)
        {
            launch = default;
            float mph = preferFast
                ? UnityEngine.Random.Range(88f, 105f)
                : UnityEngine.Random.Range(55f, 82f);
            float speed = PracticeHelpers.MphToMps(mph);
            bool highArc = style == GoalieShotStyle.Rainbow;

            Vector3? velocity = CalculateBallisticVelocity(spawnPos, aimPoint, speed, highArc, ceilingY);
            if (!velocity.HasValue)
                return false;

            if (!SimulateToGoalPlane(
                    spawnPos,
                    velocity.Value,
                    goalPlaneZ,
                    shootFromPositiveZ,
                    ceilingY,
                    iceY,
                    aimPoint.y,
                    style,
                    model,
                    out _,
                    out float travelTime,
                    out _))
            {
                return false;
            }

            launch = new GoalieShotLaunch
            {
                Velocity = velocity.Value,
                TravelTime = travelTime,
            };
            return true;
        }

        private static float ShotQualityError(
            Vector3 hitPoint,
            Vector3 aimPoint,
            Vector3 hitVelocity,
            GoalieShotStyle style,
            float iceY,
            float distanceT)
        {
            float planar = Vector2.Distance(
                new Vector2(hitPoint.x, hitPoint.y),
                new Vector2(aimPoint.x, aimPoint.y));

            float crossbarY = iceY + CrossbarHeight;
            if (hitPoint.y > crossbarY + CrossbarClearance)
                return planar + 8f + (hitPoint.y - crossbarY) * 6f;

            if (style == GoalieShotStyle.Direct)
                return planar;

            float heightErr = Mathf.Abs(hitPoint.y - aimPoint.y);
            planar += heightErr * 1.75f;

            if (hitPoint.y < iceY + 0.32f)
                planar += 2.5f + distanceT;

            float speed = hitVelocity.magnitude;
            if (speed < MinArcGoalSpeedMps)
                planar += (MinArcGoalSpeedMps - speed) * 0.35f;

            switch (style)
            {
                case GoalieShotStyle.Rising:
                    // Prefer still-rising through the net, but not a close-range skyball.
                    if (hitVelocity.y < -MinArcDescentMps)
                        planar += 0.45f + distanceT * 0.15f;
                    else if (hitVelocity.y > 6f && distanceT < 0.3f)
                        planar += 1.25f;
                    break;
                case GoalieShotStyle.Rainbow:
                    if (hitVelocity.y > -MinArcDescentMps)
                        planar += 1.5f + distanceT * 0.35f;
                    break;
            }

            return planar;
        }

        private static void GetStyleAngleRange(GoalieShotStyle style, float horizontalDist, out float minDeg, out float maxDeg)
        {
            float distT = Mathf.InverseLerp(8f, 52f, horizontalDist);
            switch (style)
            {
                case GoalieShotStyle.Rainbow:
                    // Rainbow only used past mid-range; keep apex usable but under crossbar path.
                    minDeg = Mathf.Lerp(20f, 32f, distT);
                    maxDeg = Mathf.Lerp(40f, 56f, distT);
                    break;
                case GoalieShotStyle.Rising:
                    // Close: mild lift only. Far: allow a steeper rise into the net.
                    minDeg = Mathf.Lerp(5f, 8f, distT);
                    maxDeg = Mathf.Lerp(14f, 32f, distT);
                    break;
                default:
                    minDeg = 3f;
                    maxDeg = Mathf.Lerp(12f, 20f, distT);
                    break;
            }
        }

        internal static bool SimulateToGoalPlane(
            Vector3 spawnPos,
            Vector3 velocity,
            float goalPlaneZ,
            bool shootFromPositiveZ,
            float ceilingY,
            float iceY,
            float aimY,
            GoalieShotStyle style,
            PuckFlightModel model,
            out Vector3 hitPoint,
            out float travelTime,
            out Vector3 hitVelocity)
        {
            hitPoint = default;
            travelTime = 0f;
            hitVelocity = default;

            Vector3 pos = spawnPos;
            Vector3 vel = velocity;
            float time = 0f;
            float prevZ = pos.z;
            float ceilingLimit = ceilingY - CeilingClearance;
            float iceLimit = iceY + IceContactMargin;
            float launchClearance = iceY + IceContactMargin + 0.12f;
            bool requireAirborne = style != GoalieShotStyle.Direct;
            bool leftLaunchBand = false;
            bool touchedIce = false;

            while (time < MaxSimSeconds)
            {
                if (pos.y >= ceilingLimit)
                    return false;

                if (pos.y > launchClearance)
                    leftLaunchBand = true;

                if (requireAirborne && leftLaunchBand && pos.y <= iceLimit)
                    touchedIce = true;

                if (CrossedGoalPlane(prevZ, pos.z, goalPlaneZ, shootFromPositiveZ, out float fraction))
                {
                    Vector3 prevPos = pos - vel * (model.FixedDt * fraction);
                    hitPoint = Vector3.Lerp(prevPos, pos, fraction);
                    hitVelocity = vel;
                    travelTime = time - model.FixedDt * (1f - fraction);

                    // Hard reject over the crossbar — close rising/rainbow sims were clearing net.
                    if (hitPoint.y > iceY + CrossbarHeight + CrossbarClearance)
                        return false;

                    if (requireAirborne)
                    {
                        if (touchedIce)
                            return false;

                        if (hitPoint.y < Mathf.Max(iceY + 0.28f, aimY - 0.35f))
                            return false;

                        if (hitVelocity.magnitude < MinArcGoalSpeedMps)
                            return false;

                        if (style == GoalieShotStyle.Rainbow && hitVelocity.y > -MinArcDescentMps)
                            return false;

                        // Close rising shots that are still climbing hard will sail over in practice.
                        float planarToGoal = Vector2.Distance(
                            new Vector2(hitPoint.x, hitPoint.z),
                            new Vector2(spawnPos.x, spawnPos.z));
                        if (style == GoalieShotStyle.Rising
                            && hitVelocity.y > 5.5f
                            && planarToGoal < 16f)
                        {
                            return false;
                        }
                    }

                    return true;
                }

                prevZ = pos.z;
                IntegrateStep(ref pos, ref vel, model);
                time += model.FixedDt;

                if (pos.y < spawnPos.y - 2f)
                    return false;
            }

            return false;
        }

        private static bool CrossedGoalPlane(
            float prevZ,
            float currZ,
            float goalPlaneZ,
            bool shootFromPositiveZ,
            out float fraction)
        {
            fraction = 0f;
            if (shootFromPositiveZ)
            {
                if (prevZ <= goalPlaneZ || currZ > goalPlaneZ)
                    return false;
            }
            else
            {
                if (prevZ >= goalPlaneZ || currZ < goalPlaneZ)
                    return false;
            }

            float delta = currZ - prevZ;
            if (Mathf.Abs(delta) < 0.00001f)
                return false;

            fraction = Mathf.Clamp01((goalPlaneZ - prevZ) / delta);
            return true;
        }

        private static void IntegrateStep(ref Vector3 pos, ref Vector3 vel, PuckFlightModel model)
        {
            float dt = model.FixedDt;

            vel += Physics.gravity * dt;

            if (model.HeightDependentDrag
                && pos.y > model.HeightLimit
                && vel.y > 0f)
            {
                float overheight = pos.y - model.HeightLimit;
                vel.y -= model.HeightDragFactor * overheight;
            }

            pos += vel * dt;

            float drag = model.BaseDrag;
            if (model.SpeedDependentDrag)
            {
                float speed = vel.magnitude;
                float delta = speed - model.NominalSpeed;
                drag = model.BaseDrag * (1f + model.DragFactor * delta * delta * delta);
                drag = Mathf.Max(model.BaseDrag, drag);
            }

            vel *= 1f / (1f + drag * dt);

            float maxSpeed = model.MaxSpeed;
            if (maxSpeed > 0.01f && vel.sqrMagnitude > maxSpeed * maxSpeed)
                vel = vel.normalized * maxSpeed;
        }

        private static float ReadPuckMaxSpeed(Puck puck, float fallback)
        {
            if (puck == null)
                return fallback;

            try
            {
                FieldInfo field = typeof(Puck).GetField(
                    "maxSpeed",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null && field.FieldType == typeof(float))
                    return (float)field.GetValue(puck);
            }
            catch
            {
                // ignore — fall back to CPT default
            }

            return fallback;
        }

        private static float EstimateTravelTime(Vector3 spawn, Vector3 target, Vector3 velocity)
        {
            Vector3 flatDisp = target - spawn;
            flatDisp.y = 0f;
            Vector3 flatVel = velocity;
            flatVel.y = 0f;
            float speed = flatVel.magnitude;
            if (speed < 0.01f)
                return 0.5f;
            return Mathf.Max(flatDisp.magnitude / speed, 0.12f);
        }

        /// <summary>Ballistic fallback (PuckPasser-style) when drag simulation cannot converge.</summary>
        private static Vector3? CalculateBallisticVelocity(
            Vector3 start,
            Vector3 target,
            float speed,
            bool highArc,
            float ceilingY)
        {
            float gravity = Mathf.Abs(Physics.gravity.y);

            Vector3 horizontalDisp = new Vector3(target.x - start.x, 0f, target.z - start.z);
            float horizontalDist = horizontalDisp.magnitude;
            float verticalDist = target.y - start.y;

            if (horizontalDist < 0.75f)
                return (target - start).normalized * speed;

            float v2 = speed * speed;
            float v4 = v2 * v2;
            float gx = gravity * horizontalDist;
            float gx2 = gravity * horizontalDist * horizontalDist;

            float discriminant = v4 - gravity * (gx2 + 2f * verticalDist * v2);
            if (discriminant < 0f)
                return CalculateBallisticVelocity(start, target, speed * 1.25f, highArc, ceilingY);

            float sqrtDisc = Mathf.Sqrt(discriminant);
            float tanTheta = highArc
                ? (v2 + sqrtDisc) / gx
                : (v2 - sqrtDisc) / gx;

            float theta = Mathf.Atan(tanTheta);
            float maxTheta = MaxLaunchAngleRadForCeiling(start.y, ceilingY, speed);
            theta = Mathf.Clamp(theta, -Mathf.PI / 4f, maxTheta);

            float horizontalSpeed = speed * Mathf.Cos(theta);
            float verticalSpeed = speed * Mathf.Sin(theta);
            Vector3 horizontalDir = horizontalDisp.normalized;

            return horizontalDir * horizontalSpeed + Vector3.up * verticalSpeed;
        }

        internal static float ResolveCeilingY(Vector3 spawnPos)
        {
            if (!float.IsNaN(cachedCeilingY)
                && (spawnPos - cachedCeilingSample).sqrMagnitude < 64f)
            {
                return cachedCeilingY;
            }

            float iceY = spawnPos.y;
            float lowest = iceY + FallbackCeilingAboveIce;

            Vector3 probeOrigin = new Vector3(spawnPos.x, iceY + 0.35f, spawnPos.z);
            RaycastHit[] hits = Physics.RaycastAll(
                probeOrigin,
                Vector3.up,
                CeilingProbeHeight,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == null || hit.collider.isTrigger)
                    continue;

                if (hit.point.y < iceY + 3f)
                    continue;

                // Underside of roof / glass — normal points down, or a flat slab above the rink.
                bool underside = hit.normal.y < -0.25f;
                bool flatOverhead = hit.normal.y > 0.65f && hit.point.y > iceY + 6f;
                if (!underside && !flatOverhead)
                    continue;

                if (IsTrainingPropCollider(hit.collider))
                    continue;

                lowest = Mathf.Min(lowest, hit.point.y);
            }

            Level level = UnityEngine.Object.FindFirstObjectByType<Level>();
            if (level != null)
            {
                foreach (Collider col in level.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null || col.isTrigger || !col.enabled)
                        continue;

                    if (!ColliderLooksLikeArenaCeiling(col, iceY, out float undersideY))
                        continue;

                    lowest = Mathf.Min(lowest, undersideY);
                }
            }

            cachedCeilingY = lowest - CeilingClearance;
            cachedCeilingSample = spawnPos;
            PracticeLog.Info("[PHLPractice] Goalie ceiling Y=" + cachedCeilingY.ToString("F2") +
                             " (probe @" + spawnPos + ", clearance=" + CeilingClearance.ToString("F2") + ").");
            return cachedCeilingY;
        }

        private static bool ColliderLooksLikeArenaCeiling(Collider col, float iceY, out float undersideY)
        {
            undersideY = float.MaxValue;
            if (col == null)
                return false;

            Bounds b = col.bounds;
            if (b.max.y < iceY + 5f)
                return false;

            bool wideSlab = b.size.y <= 8f && b.size.x >= 10f && b.size.z >= 10f && b.min.y > iceY + 6f;
            if (wideSlab)
            {
                undersideY = b.min.y;
                return true;
            }

            Transform t = col.transform;
            while (t != null)
            {
                string n = t.name;
                if (n.IndexOf("Hangar", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Sky", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Ceiling", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Arena", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    undersideY = b.min.y;
                    return undersideY > iceY + 5f;
                }

                t = t.parent;
            }

            return false;
        }

        private static bool IsTrainingPropCollider(Collider col)
        {
            if (col == null)
                return false;

            Transform t = col.transform;
            while (t != null)
            {
                string n = t.name;
                if (n.StartsWith("Training_", StringComparison.Ordinal)
                    || n.StartsWith("Train_", StringComparison.Ordinal)
                    || n.StartsWith("PassBack", StringComparison.Ordinal)
                    || n.StartsWith("PHL_", StringComparison.Ordinal))
                {
                    return true;
                }

                t = t.parent;
            }

            return false;
        }

        private static void CapAngleRangeForCeiling(
            Vector3 spawnPos,
            float ceilingY,
            float speedMps,
            ref float minDeg,
            ref float maxDeg)
        {
            float capped = MaxLaunchAngleDegForCeiling(spawnPos.y, ceilingY, speedMps);
            maxDeg = Mathf.Min(maxDeg, capped);
            if (maxDeg < minDeg + 1f)
                maxDeg = minDeg + 1f;
        }

        private static float MaxLaunchAngleDegForCeiling(float spawnY, float ceilingY, float speedMps)
        {
            return MaxLaunchAngleRadForCeiling(spawnY, ceilingY, speedMps) * Mathf.Rad2Deg;
        }

        private static float MaxLaunchAngleRadForCeiling(float spawnY, float ceilingY, float speedMps)
        {
            if (speedMps < 0.01f)
                return Mathf.PI / 2.5f;

            float clearance = ceilingY - spawnY;
            if (clearance <= 0.75f)
                return 12f * Mathf.Deg2Rad;

            float gravity = Mathf.Abs(Physics.gravity.y);
            float sinTheta = Mathf.Sqrt(2f * gravity * clearance) / speedMps;
            if (sinTheta >= 0.999f)
                return 55f * Mathf.Deg2Rad;

            float angle = Mathf.Asin(Mathf.Clamp01(sinTheta));
            // Drag lowers apex vs vacuum — allow a hair more loft, but stay under the collider.
            return Mathf.Clamp(angle + 2f * Mathf.Deg2Rad, 8f * Mathf.Deg2Rad, 58f * Mathf.Deg2Rad);
        }

        // ── Tip practice (feeds, loopers, on-net looks) ───────────────────────

        internal enum TipFeedKind
        {
            AtTipper,
            LongStraight,
            WideTipper,
            HighLooperTipper,
            HighLooperNet,
            OnNet,
        }

        private enum TipShotStyle
        {
            Ice,
            Direct,
            SoftLift,
            HighArc,
        }

        /// <summary>
        /// Build a tip-practice launch — straight feeds, wide passes, high loopers, and on-net looks.
        /// </summary>
        internal static bool TryBuildTipLaunch(
            Vector3 spawnPos,
            Vector3 tipTarget,
            float tipPlaneZ,
            bool shootFromPositiveZ,
            Puck puck,
            TipFeedKind feedKind,
            out GoalieShotLaunch launch)
        {
            launch = default;
            PuckFlightModel model = PuckFlightModel.FromPuck(puck);
            float ceilingY = ResolveCeilingY(spawnPos);
            float iceY = spawnPos.y;

            Vector3 flat = tipTarget - spawnPos;
            flat.y = 0f;
            float horizontalDist = flat.magnitude;
            if (horizontalDist < 0.01f)
                flat = shootFromPositiveZ ? Vector3.back : Vector3.forward;
            else
                flat /= horizontalDist;

            TipShotStyle style = PickTipShotStyle(feedKind);
            GetTipAngleRange(style, horizontalDist, feedKind, out float minDeg, out float maxDeg);

            float maxPeak = iceY + ResolveTipMaxPeak(feedKind, style);
            float maxTipHeight = iceY + ResolveTipMaxArrivalHeight(feedKind, style);

            float bestError = float.MaxValue;
            Vector3 bestVelocity = Vector3.zero;
            float bestTime = 0f;

            float[] speeds = PickTipSpeedCandidatesMph(feedKind);
            for (int s = 0; s < speeds.Length; s++)
            {
                float speedMps = PracticeHelpers.MphToMps(Mathf.Clamp(speeds[s], 40f, 82f));
                float searchMin = minDeg;
                float searchMax = maxDeg;
                CapAngleRangeForCeiling(spawnPos, ceilingY, speedMps, ref searchMin, ref searchMax);
                searchMax = Mathf.Min(searchMax, ResolveTipMaxSearchAngleDeg(feedKind, style));
                if (searchMax < searchMin + 0.5f)
                    searchMax = searchMin + 0.5f;

                for (int step = 0; step <= 28; step++)
                {
                    float angleDeg = Mathf.Lerp(searchMin, searchMax, step / 28f);
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    Vector3 velocity = flat * (Mathf.Cos(angleRad) * speedMps)
                        + Vector3.up * (Mathf.Sin(angleRad) * speedMps);

                    if (!SimulateToTipPlane(
                            spawnPos,
                            velocity,
                            tipPlaneZ,
                            shootFromPositiveZ,
                            ceilingY,
                            iceY,
                            maxPeak,
                            maxTipHeight,
                            style,
                            model,
                            out Vector3 hitPoint,
                            out float travelTime,
                            out _))
                    {
                        continue;
                    }

                    float error = TipQualityError(hitPoint, tipTarget, style, feedKind, iceY);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestVelocity = velocity;
                        bestTime = travelTime;
                    }
                }
            }

            if (bestError > 2.8f || bestVelocity.sqrMagnitude < 0.01f)
            {
                float mph = UnityEngine.Random.Range(
                    feedKind == TipFeedKind.LongStraight || feedKind == TipFeedKind.OnNet
                        ? 58f : PracticeConstants.TipShotMinSpeedMph,
                    PracticeConstants.TipShotMaxSpeedMph);
                bool highArc = feedKind == TipFeedKind.HighLooperTipper
                    || feedKind == TipFeedKind.HighLooperNet;
                Vector3? ballistic = CalculateBallisticVelocity(
                    spawnPos, tipTarget, PracticeHelpers.MphToMps(mph), highArc, ceilingY);
                if (!ballistic.HasValue)
                    return false;

                bestVelocity = ballistic.Value;
                bestTime = EstimateTravelTime(spawnPos, tipTarget, bestVelocity);
            }

            launch = new GoalieShotLaunch
            {
                Velocity = bestVelocity,
                TravelTime = Mathf.Max(bestTime, 0.12f),
            };
            return true;
        }

        private static TipShotStyle PickTipShotStyle(TipFeedKind feedKind)
        {
            switch (feedKind)
            {
                case TipFeedKind.OnNet:
                    return UnityEngine.Random.value < 0.35f ? TipShotStyle.Ice : TipShotStyle.Direct;
                case TipFeedKind.LongStraight:
                    return TipShotStyle.Direct;
                case TipFeedKind.WideTipper:
                    return TipShotStyle.Direct;
                case TipFeedKind.HighLooperTipper:
                case TipFeedKind.HighLooperNet:
                    return TipShotStyle.HighArc;
                default:
                    return PickTipShotStyleRandom();
            }
        }

        private static TipShotStyle PickTipShotStyleRandom()
        {
            float roll = UnityEngine.Random.value;
            if (roll < 0.22f)
                return TipShotStyle.Ice;
            if (roll < 0.72f)
                return TipShotStyle.Direct;
            return TipShotStyle.SoftLift;
        }

        private static float ResolveTipMaxPeak(TipFeedKind feedKind, TipShotStyle style)
        {
            if (feedKind == TipFeedKind.HighLooperTipper || feedKind == TipFeedKind.HighLooperNet)
                return 3.35f;
            if (style == TipShotStyle.SoftLift)
                return 1.85f;
            return 1.55f;
        }

        private static float ResolveTipMaxArrivalHeight(TipFeedKind feedKind, TipShotStyle style)
        {
            if (feedKind == TipFeedKind.HighLooperTipper || feedKind == TipFeedKind.HighLooperNet)
                return 1.85f;
            if (feedKind == TipFeedKind.OnNet)
                return 1.35f;
            if (style == TipShotStyle.Ice)
                return 0.35f;
            if (style == TipShotStyle.SoftLift)
                return 1.45f;
            return 1.25f;
        }

        private static float ResolveTipMaxSearchAngleDeg(TipFeedKind feedKind, TipShotStyle style)
        {
            if (feedKind == TipFeedKind.HighLooperTipper || feedKind == TipFeedKind.HighLooperNet)
                return 34f;
            if (feedKind == TipFeedKind.LongStraight)
                return 9f;
            if (feedKind == TipFeedKind.OnNet)
                return 16f;
            if (style == TipShotStyle.SoftLift)
                return 20f;
            return 14f;
        }

        private static float[] PickTipSpeedCandidatesMph(TipFeedKind feedKind)
        {
            float slow = UnityEngine.Random.Range(44f, 56f);
            float medium = UnityEngine.Random.Range(54f, 66f);
            float firm = UnityEngine.Random.Range(64f, 78f);
            float bullet = UnityEngine.Random.Range(72f, 82f);

            if (feedKind == TipFeedKind.LongStraight
                || feedKind == TipFeedKind.HighLooperNet
                || feedKind == TipFeedKind.OnNet)
            {
                float primary = UnityEngine.Random.value < 0.55f ? firm : bullet;
                return new[] { primary, medium, firm, bullet };
            }

            if (feedKind == TipFeedKind.HighLooperTipper)
            {
                float primary = UnityEngine.Random.value < 0.5f ? medium : firm;
                return new[] { primary, slow, medium, firm };
            }

            float defaultPrimary = UnityEngine.Random.value < 0.45f ? medium
                : UnityEngine.Random.value < 0.5f ? slow
                : firm;
            return new[] { defaultPrimary, slow, medium, firm };
        }

        private static void GetTipAngleRange(
            TipShotStyle style,
            float horizontalDist,
            TipFeedKind feedKind,
            out float minDeg,
            out float maxDeg)
        {
            float distT = Mathf.InverseLerp(8f, 42f, horizontalDist);
            switch (style)
            {
                case TipShotStyle.Ice:
                    minDeg = 0.5f;
                    maxDeg = Mathf.Lerp(4f, 8f, distT);
                    break;
                case TipShotStyle.HighArc:
                    minDeg = Mathf.Lerp(14f, 18f, distT);
                    maxDeg = Mathf.Lerp(26f, 32f, distT);
                    break;
                case TipShotStyle.SoftLift:
                    minDeg = Mathf.Lerp(5f, 8f, distT);
                    maxDeg = Mathf.Lerp(14f, 20f, distT);
                    break;
                default:
                    minDeg = Mathf.Lerp(1f, 2.5f, distT);
                    maxDeg = Mathf.Lerp(6f, 11f, distT);
                    break;
            }

            if (feedKind == TipFeedKind.LongStraight)
            {
                minDeg = 0.8f;
                maxDeg = Mathf.Lerp(4f, 8f, distT);
            }
        }

        private static float TipQualityError(
            Vector3 hitPoint,
            Vector3 tipTarget,
            TipShotStyle style,
            TipFeedKind feedKind,
            float iceY)
        {
            float planar = Vector2.Distance(
                new Vector2(hitPoint.x, hitPoint.y),
                new Vector2(tipTarget.x, tipTarget.y));

            float heightErr = Mathf.Abs(hitPoint.y - tipTarget.y);
            planar += heightErr * 1.35f;

            if (feedKind == TipFeedKind.OnNet || feedKind == TipFeedKind.HighLooperNet)
            {
                planar += Mathf.Abs(hitPoint.z - tipTarget.z) * 0.35f;
                return planar;
            }

            switch (style)
            {
                case TipShotStyle.Ice:
                    if (hitPoint.y > iceY + 0.4f)
                        planar += 1.2f;
                    break;
                case TipShotStyle.Direct:
                    if (hitPoint.y < iceY + 0.25f)
                        planar += 0.55f;
                    if (hitPoint.y > iceY + 1.35f)
                        planar += 1.6f;
                    break;
                case TipShotStyle.HighArc:
                    if (hitPoint.y < iceY + 0.35f)
                        planar += 0.45f;
                    break;
                case TipShotStyle.SoftLift:
                    if (hitPoint.y < iceY + 0.45f)
                        planar += 0.7f;
                    if (hitPoint.y > iceY + 1.65f)
                        planar += 1.4f;
                    break;
            }

            return planar;
        }

        private static bool SimulateToTipPlane(
            Vector3 spawnPos,
            Vector3 velocity,
            float tipPlaneZ,
            bool shootFromPositiveZ,
            float ceilingY,
            float iceY,
            float maxPeakY,
            float maxTipHeightY,
            TipShotStyle style,
            PuckFlightModel model,
            out Vector3 hitPoint,
            out float travelTime,
            out Vector3 hitVelocity)
        {
            hitPoint = default;
            travelTime = 0f;
            hitVelocity = default;

            Vector3 pos = spawnPos;
            Vector3 vel = velocity;
            float time = 0f;
            float prevZ = pos.z;
            float peakY = pos.y;
            float ceilingLimit = ceilingY - CeilingClearance;
            float iceLimit = iceY + IceContactMargin;
            bool requireAirborne = style == TipShotStyle.SoftLift || style == TipShotStyle.HighArc;
            bool leftLaunchBand = false;
            bool touchedIce = false;

            while (time < MaxSimSeconds)
            {
                if (pos.y >= ceilingLimit || pos.y > maxPeakY)
                    return false;

                if (pos.y > peakY)
                    peakY = pos.y;

                if (pos.y > iceY + IceContactMargin + 0.12f)
                    leftLaunchBand = true;

                if (requireAirborne && leftLaunchBand && pos.y <= iceLimit)
                    touchedIce = true;

                if (CrossedGoalPlane(prevZ, pos.z, tipPlaneZ, shootFromPositiveZ, out float fraction))
                {
                    Vector3 prevPos = pos - vel * (model.FixedDt * fraction);
                    hitPoint = Vector3.Lerp(prevPos, pos, fraction);
                    hitVelocity = vel;
                    travelTime = time - model.FixedDt * (1f - fraction);

                    if (hitPoint.y > maxTipHeightY)
                        return false;

                    if (requireAirborne && touchedIce)
                        return false;

                    if (style == TipShotStyle.Ice && hitPoint.y > iceY + 0.45f)
                        return false;

                    return true;
                }

                prevZ = pos.z;
                IntegrateStep(ref pos, ref vel, model);
                time += model.FixedDt;

                if (pos.y < spawnPos.y - 2f)
                    return false;
            }

            return false;
        }
    }
}
