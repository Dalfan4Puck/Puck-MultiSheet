using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Puck selection + stick/intercept math ported from AIGoaliesStandalone.GoalieAI for the training prefab goalie.
/// </summary>
public static class TrainingGoalieLogic
{
    public struct StickAim
    {
        public float VerticalAngle;
        public float HorizontalAngle;
    }

    public const float IdleVerticalAngle = 30f;
    public const float ShotLeadTime = 0.2f;
    public const float SquaringDegPerSec = 220f;
    public const float AggressionRange = 22f;

    public static Puck GetBestPuck(Vector3 goalPos, Vector3 defendDirection)
    {
        try
        {
            PuckManager puckManager = PuckManager.Instance;
            if (puckManager == null)
                return null;

            IReadOnlyList<Puck> pucks = puckManager.GetPucks(false);
            if (pucks == null || pucks.Count == 0)
                return null;

            Puck bestPuck = null;
            float bestScore = float.MinValue;
            Vector3 goalFlat = goalPos;
            goalFlat.y = 0f;

            foreach (Puck puck in pucks)
            {
                if (puck == null)
                    continue;

                try
                {
                    if (puck.gameObject == null || puck.transform == null)
                        continue;

                    if (puck.IsReplay != null && puck.IsReplay.Value)
                        continue;

                    Vector3 puckPos = puck.transform.position;
                    Vector3 toGoal = goalFlat - puckPos;
                    toGoal.y = 0f;

                    // Skip pucks clearly behind the goal line relative to the net.
                    if (Vector3.Dot(toGoal.normalized, defendDirection.normalized) < -0.15f)
                        continue;

                    float lateralDist = Mathf.Abs(puckPos.x - goalPos.x);
                    float depthDist = Vector3.Distance(
                        new Vector3(puckPos.x, 0f, puckPos.z),
                        new Vector3(goalPos.x, 0f, goalPos.z));
                    float effectiveDist = Mathf.Sqrt(lateralDist * lateralDist * 4f + depthDist * depthDist);

                    float puckSpeed = 0f;
                    float approachFactor = 0f;
                    Rigidbody rb = puck.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        Vector3 vel = rb.linearVelocity;
                        puckSpeed = vel.magnitude;
                        if (toGoal.sqrMagnitude > 0.01f)
                            approachFactor = Mathf.Max(0f, Vector3.Dot(vel.normalized, toGoal.normalized));
                    }

                    float distScore = 100f / Mathf.Max(effectiveDist, 1f);
                    float speedScore = puckSpeed * approachFactor * 5f;
                    float score = distScore + speedScore;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPuck = puck;
                    }
                }
                catch
                {
                    continue;
                }
            }

            return bestPuck;
        }
        catch
        {
            return GameObject.FindFirstObjectByType<Puck>();
        }
    }

    public static StickAim ComputeStickAim(Vector3 bodyForward, Vector3 bodyPos, Vector3 puckPos)
    {
        Vector3 flatForward = bodyForward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.001f)
            flatForward = Vector3.forward;
        flatForward.Normalize();

        Vector3 toPuck = puckPos - bodyPos;
        float horizontalAngle = Vector3.SignedAngle(
            flatForward,
            new Vector3(toPuck.x, 0f, toPuck.z),
            Vector3.up);
        horizontalAngle = Mathf.Clamp(horizontalAngle, -90f, 90f);

        float puckHeight = puckPos.y;
        float verticalAngle;
        if (puckHeight < 0.1f)
            verticalAngle = 35f;
        else if (puckHeight > 1.5f)
            verticalAngle = -20f;
        else
            verticalAngle = Mathf.Lerp(35f, -20f, puckHeight / 1.5f);

        return new StickAim
        {
            VerticalAngle = verticalAngle,
            HorizontalAngle = horizontalAngle
        };
    }

    public static StickAim IdleStickAim()
    {
        return new StickAim { VerticalAngle = IdleVerticalAngle, HorizontalAngle = 0f };
    }

    public static bool TryComputeIntercept(
        Vector3 goalPos,
        Vector3 faceDirection,
        Vector3 bodyPos,
        Vector3 puckPos,
        Vector3 puckVelocity,
        float goalWidth,
        out Vector3 interceptPos)
    {
        interceptPos = bodyPos;

        Vector3 flatFace = faceDirection;
        flatFace.y = 0f;
        if (flatFace.sqrMagnitude < 0.001f)
            return false;
        flatFace.Normalize();

        Vector3 goalCenter = goalPos;
        goalCenter.y = bodyPos.y;

        Vector3 anticipated = puckPos + puckVelocity * ShotLeadTime;
        anticipated.y = bodyPos.y;

        Vector3 puckToGoal = goalCenter - anticipated;
        puckToGoal.y = 0f;
        float puckToGoalDist = Mathf.Max(puckToGoal.magnitude, 0.1f);

        float distToGoal = Vector3.Distance(
            new Vector3(puckPos.x, 0f, puckPos.z),
            new Vector3(goalPos.x, 0f, goalPos.z));

        float comeOutDistance = Mathf.Clamp(3.6f - distToGoal * 0.11f, 1.2f, 3.2f);
        float ratio = Mathf.Clamp01(comeOutDistance / puckToGoalDist);
        Vector3 interceptOnLine = Vector3.Lerp(goalCenter, anticipated, ratio);
        interceptPos = goalCenter + flatFace * comeOutDistance;
        interceptPos.x = interceptOnLine.x;
        interceptPos.y = bodyPos.y;

        float depthOut = comeOutDistance;
        float maxLateral = goalWidth + depthOut * 0.35f;
        interceptPos.x = Mathf.Clamp(interceptPos.x, goalCenter.x - maxLateral, goalCenter.x + maxLateral);

        return Vector3.Distance(
            new Vector3(bodyPos.x, 0f, bodyPos.z),
            new Vector3(puckPos.x, 0f, puckPos.z)) < AggressionRange;
    }

    public static Vector3 ResolveFaceTowardIce(Transform trainingRoot, Vector3 netCenter)
    {
        if (trainingRoot == null)
            return Vector3.forward;

        Vector3 toHive = trainingRoot.position - netCenter;
        toHive.y = 0f;
        if (toHive.sqrMagnitude > 0.25f)
            return toHive.normalized;

        Vector3 forward = trainingRoot.forward;
        forward.y = 0f;
        return forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
    }
}
