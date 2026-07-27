using System;
using Unity.Netcode;
using UnityEngine;

public class ConstantRotator : MonoBehaviour
{
    // Global speed changed by /speed (synced to clients via TrainingMotionSync params).
    public static float globalSpeed = 200f;

    // Direction for this specific object
    public float direction = 1f;

    /// <summary>
    /// When true, pose is driven from synchronized <see cref="NetworkManager.ServerTime"/> so
    /// server hitboxes and client visuals share the same phase (no pose-lag desync).
    /// </summary>
    public bool simulateLocally = true;

    private Quaternion restLocalRotation = Quaternion.identity;
    private bool hasRest;

    private void Awake()
    {
        CaptureRestPose();
    }

    private void OnEnable()
    {
        if (!hasRest)
            CaptureRestPose();
    }

    private void CaptureRestPose()
    {
        restLocalRotation = transform.localRotation;
        hasRest = true;
    }

    private void FixedUpdate()
    {
        if (!simulateLocally)
            return;

        if (!hasRest)
            CaptureRestPose();

        float angle = AngleDegreesAt(GetSimTimeSeconds());
        transform.localRotation = restLocalRotation * Quaternion.Euler(0f, angle, 0f);
        Physics.SyncTransforms();
    }

    /// <summary>Y-axis degrees at synchronized simulation time (matches server + all clients).</summary>
    public float AngleDegreesAt(double simTimeSeconds)
    {
        double deg = direction * globalSpeed * simTimeSeconds;
        double turns = deg / 360.0;
        turns -= Math.Floor(turns);
        if (turns < 0.0)
            turns += 1.0;
        return (float)(turns * 360.0);
    }

    public static double GetSimTimeSeconds()
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null)
                return nm.ServerTime.Time;
        }
        catch
        {
            // Fall through
        }

        return Time.timeAsDouble;
    }
}
