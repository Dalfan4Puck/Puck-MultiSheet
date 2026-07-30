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

        internal static GoalieShotStyle PickShotStyle(float distanceT)
        {
            float roll = UnityEngine.Random.value;
            if (distanceT > 0.45f && roll < 0.42f)
                return GoalieShotStyle.Rainbow;
            if (roll < 0.52f)
                return GoalieShotStyle.Rising;
            return GoalieShotStyle.Direct;
        }

        internal static float[] PickReleaseSpeedCandidatesMph(bool preferFast)
        {
            float slow = UnityEngine.Random.Range(52f, 70f);
            float medium = UnityEngine.Random.Range(70f, 90f);
            float fast = UnityEngine.Random.Range(90f, 108f);

            if (preferFast)
                return new[] { fast, fast + UnityEngine.Random.Range(-6f, 4f), medium, slow + 8f };

            float primary = UnityEngine.Random.value < 0.33f ? slow
                : UnityEngine.Random.value < 0.5f ? medium
                : fast;
            return new[] { primary, slow, medium, fast, primary + UnityEngine.Random.Range(-14f, 14f) };
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

            Vector3 flat = aimPoint - spawnPos;
            flat.y = 0f;
            float horizontalDist = flat.magnitude;
            if (horizontalDist < 0.01f)
                flat = shootFromPositiveZ ? Vector3.back : Vector3.forward;
            else
                flat /= horizontalDist;

            GetStyleAngleRange(style, horizontalDist, out float minDeg, out float maxDeg);
            float distanceT = Mathf.InverseLerp(8f, 52f, horizontalDist);

            float bestError = float.MaxValue;
            Vector3 bestVelocity = Vector3.zero;
            float bestTime = 0f;

            float[] speedCandidates = PickReleaseSpeedCandidatesMph(preferFast);
            for (int s = 0; s < speedCandidates.Length; s++)
            {
                float speedMps = PracticeHelpers.MphToMps(Mathf.Clamp(speedCandidates[s], 48f, 112f));
                for (int step = 0; step <= 40; step++)
                {
                    float angleDeg = Mathf.Lerp(minDeg, maxDeg, step / 40f);
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    Vector3 velocity = flat * (Mathf.Cos(angleRad) * speedMps)
                        + Vector3.up * (Mathf.Sin(angleRad) * speedMps);

                    if (!SimulateToGoalPlane(
                            spawnPos,
                            velocity,
                            goalPlaneZ,
                            shootFromPositiveZ,
                            model,
                            out Vector3 hitPoint,
                            out float travelTime,
                            out Vector3 hitVelocity))
                    {
                        continue;
                    }

                    float error = HorizontalTargetError(hitPoint, aimPoint, style, hitVelocity, distanceT);
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
                    distanceT,
                    ref bestError,
                    ref bestVelocity,
                    ref bestTime);

            if (bestError > GoalHitTolerance || bestVelocity.sqrMagnitude < 0.01f || bestTime < 0.05f)
                return TryBallisticFallback(spawnPos, aimPoint, style, preferFast, flat, out launch);

            launch = new GoalieShotLaunch
            {
                Velocity = bestVelocity,
                TravelTime = bestTime,
            };
            return true;
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
            float distanceT,
            ref float bestError,
            ref Vector3 bestVelocity,
            ref float bestTime)
        {
            float lo = minDeg;
            float hi = maxDeg;
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
                        model,
                        out Vector3 hitPoint,
                        out float travelTime,
                        out Vector3 hitVelocity))
                {
                    continue;
                }

                float error = HorizontalTargetError(hitPoint, aimPoint, style, hitVelocity, distanceT);
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
            GoalieShotStyle style,
            bool preferFast,
            Vector3 flatDir,
            out GoalieShotLaunch launch)
        {
            launch = default;
            float mph = preferFast
                ? UnityEngine.Random.Range(88f, 105f)
                : UnityEngine.Random.Range(55f, 82f);
            float speed = PracticeHelpers.MphToMps(mph);
            bool highArc = style == GoalieShotStyle.Rainbow;

            Vector3? velocity = CalculateBallisticVelocity(spawnPos, aimPoint, speed, highArc);
            if (!velocity.HasValue)
                return false;

            launch = new GoalieShotLaunch
            {
                Velocity = velocity.Value,
                TravelTime = EstimateTravelTime(spawnPos, aimPoint, velocity.Value),
            };
            return true;
        }

        private static float HorizontalTargetError(
            Vector3 hitPoint,
            Vector3 aimPoint,
            GoalieShotStyle style,
            Vector3 hitVelocity,
            float distanceT)
        {
            float planar = Vector2.Distance(
                new Vector2(hitPoint.x, hitPoint.y),
                new Vector2(aimPoint.x, aimPoint.y));

            switch (style)
            {
                case GoalieShotStyle.Rising:
                    if (hitVelocity.y < 0.2f)
                        planar += 0.25f + distanceT * 0.15f;
                    break;
                case GoalieShotStyle.Rainbow:
                    if (hitVelocity.y > -0.5f)
                        planar += 0.12f;
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
                    minDeg = Mathf.Lerp(24f, 32f, distT);
                    maxDeg = Mathf.Lerp(48f, 58f, distT);
                    break;
                case GoalieShotStyle.Rising:
                    minDeg = Mathf.Lerp(10f, 8f, distT);
                    maxDeg = Mathf.Lerp(28f, 34f, distT);
                    break;
                default:
                    minDeg = 4f;
                    maxDeg = Mathf.Lerp(16f, 22f, distT);
                    break;
            }
        }

        internal static bool SimulateToGoalPlane(
            Vector3 spawnPos,
            Vector3 velocity,
            float goalPlaneZ,
            bool shootFromPositiveZ,
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

            while (time < MaxSimSeconds)
            {
                if (CrossedGoalPlane(prevZ, pos.z, goalPlaneZ, shootFromPositiveZ, out float fraction))
                {
                    Vector3 prevPos = pos - vel * (model.FixedDt * fraction);
                    hitPoint = Vector3.Lerp(prevPos, pos, fraction);
                    hitVelocity = vel;
                    travelTime = time - model.FixedDt * (1f - fraction);
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
        private static Vector3? CalculateBallisticVelocity(Vector3 start, Vector3 target, float speed, bool highArc)
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
                return CalculateBallisticVelocity(start, target, speed * 1.25f, highArc);

            float sqrtDisc = Mathf.Sqrt(discriminant);
            float tanTheta = highArc
                ? (v2 + sqrtDisc) / gx
                : (v2 - sqrtDisc) / gx;

            float theta = Mathf.Atan(tanTheta);
            theta = Mathf.Clamp(theta, -Mathf.PI / 4f, Mathf.PI / 2.5f);

            float horizontalSpeed = speed * Mathf.Cos(theta);
            float verticalSpeed = speed * Mathf.Sin(theta);
            Vector3 horizontalDir = horizontalDisp.normalized;

            return horizontalDir * horizontalSpeed + Vector3.up * verticalSpeed;
        }
    }
}
