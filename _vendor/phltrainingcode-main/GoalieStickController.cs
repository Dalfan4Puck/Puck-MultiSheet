using UnityEngine;

/// <summary>
/// Applies AIGoalies-style stick raycast angles to the decorative training goalie stick mesh.
/// </summary>
public class GoalieStickController : MonoBehaviour
{
    [Tooltip("Extra local euler offset if the prefab stick rest pose differs from Puck's goalie stick.")]
    public Vector3 stickRestOffsetEuler = new Vector3(-90f, 0f, 0f);

    public float rotationSpeed = 18f;

    private GoalieController goalie;
    private Transform goalieRoot;
    private Vector3 localStartPos;
    private Quaternion localStartRot;

    private void Start()
    {
        goalieRoot = FindGoalieRoot();
        goalie = goalieRoot != null ? goalieRoot.GetComponent<GoalieController>() : null;
        localStartPos = transform.localPosition;
        localStartRot = transform.localRotation;
    }

    private void LateUpdate()
    {
        if (goalieRoot == null)
            goalieRoot = FindGoalieRoot();

        if (goalie == null && goalieRoot != null)
            goalie = goalieRoot.GetComponent<GoalieController>();

        if (goalieRoot == null)
            return;

        TrainingGoalieLogic.StickAim aim = TrainingGoalieLogic.IdleStickAim();

        if (goalie != null &&
            goalie.TryGetTrackedPuck(out _, out Vector3 puckPos, out _))
        {
            aim = TrainingGoalieLogic.ComputeStickAim(
                goalieRoot.forward,
                goalieRoot.position,
                puckPos);
        }

        ApplyStickAim(aim);
    }

    private void ApplyStickAim(TrainingGoalieLogic.StickAim aim)
    {
        // Match Puck goalie StickRaycastOriginAngleInput: x = pitch, y = yaw from body forward.
        Quaternion aimRot = Quaternion.Euler(aim.VerticalAngle, aim.HorizontalAngle, 0f);
        Quaternion targetLocal = localStartRot * Quaternion.Euler(stickRestOffsetEuler) * aimRot;

        transform.localRotation = Quaternion.Slerp(
            transform.localRotation,
            targetLocal,
            rotationSpeed * Time.deltaTime);

        transform.localPosition = localStartPos;
    }

    private Transform FindGoalieRoot()
    {
        Transform current = transform;
        while (current != null)
        {
            if (current.name == "GoalieModel")
                return current;
            current = current.parent;
        }

        return transform.parent;
    }
}
