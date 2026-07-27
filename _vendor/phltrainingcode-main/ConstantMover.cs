using Unity.Netcode;
using UnityEngine;

public class ConstantMover : MonoBehaviour
{
    public static float globalSpeed = 2f;
    public static float globalDistance = 5f;

    /// <summary>
    /// When true, pose is driven from synchronized <see cref="NetworkManager.ServerTime"/> so
    /// server hitboxes and client visuals share the same phase.
    /// </summary>
    public bool simulateLocally = true;

    private Vector3 startPosition;
    private bool hasStart;

    private void Start()
    {
        startPosition = transform.position;
        hasStart = true;
    }

    private void FixedUpdate()
    {
        if (!simulateLocally)
            return;

        if (!hasStart)
        {
            startPosition = transform.position;
            hasStart = true;
        }

        float t = (float)ConstantRotator.GetSimTimeSeconds();
        float offset = Mathf.Sin(t * globalSpeed) * globalDistance;

        // Move left/right relative to where it spawned
        transform.position = startPosition + transform.right * offset;
        Physics.SyncTransforms();
    }
}
