using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flamie logging gate. Dedicated servers stay quiet for chatty Info lines (they were
/// drowning Puck.log / NetPerf). Warnings and errors always emit. Use InfoOnce for
/// important one-shot lifecycle lines that should still appear on dedicated boots.
/// Setup() is verbose-only detail (collider/slidable init). WarnOnce dedupes hot warnings.
/// </summary>
public static class FlamieLog
{
    public static bool Verbose;

    private static readonly HashSet<string> OnceKeys = new HashSet<string>();
    private static readonly Dictionary<string, float> NextAllowed = new Dictionary<string, float>();

    public static void Info(string message)
    {
        if (Application.isBatchMode && !Verbose)
            return;
        Debug.Log(message);
    }

    public static void InfoOnce(string key, string message)
    {
        if (string.IsNullOrEmpty(key) || !OnceKeys.Add(key))
            return;
        Debug.Log(message);
    }

    /// <summary>At most once per <paramref name="intervalSeconds"/> (both client and dedicated).</summary>
    public static void InfoThrottled(string key, string message, float intervalSeconds = 5f)
    {
        if (string.IsNullOrEmpty(key))
            return;
        float now = Time.unscaledTime;
        if (NextAllowed.TryGetValue(key, out float next) && now < next)
            return;
        NextAllowed[key] = now + Mathf.Max(0.25f, intervalSeconds);
        if (Application.isBatchMode && !Verbose)
            return;
        Debug.Log(message);
    }

    public static void Warn(string message) => Debug.LogWarning(message);

    public static void WarnThrottled(string key, string message, float intervalSeconds = 5f)
    {
        if (string.IsNullOrEmpty(key))
        {
            Debug.LogWarning(message);
            return;
        }

        float now = Time.unscaledTime;
        if (NextAllowed.TryGetValue(key, out float next) && now < next)
            return;
        NextAllowed[key] = now + Mathf.Max(0.25f, intervalSeconds);
        Debug.LogWarning(message);
    }

    public static void WarnOnce(string key, string message)
    {
        if (string.IsNullOrEmpty(key) || !OnceKeys.Add(key))
            return;
        Debug.LogWarning(message);
    }

    /// <summary>Collider / slidable setup detail — only when Verbose.</summary>
    public static void Setup(string message)
    {
        if (!Verbose)
            return;
        Debug.Log(message);
    }

    /// <summary>Training snapshot / sync — always visible on dedicated servers.</summary>
    public static void ServerSync(string message) => Debug.Log(message);

    /// <summary>Throttled snapshot / sync — always visible on dedicated servers.</summary>
    public static void ServerSyncThrottled(string key, string message, float intervalSeconds = 5f)
    {
        if (string.IsNullOrEmpty(key))
        {
            Debug.Log(message);
            return;
        }

        float now = Time.unscaledTime;
        if (NextAllowed.TryGetValue(key, out float next) && now < next)
            return;
        NextAllowed[key] = now + Mathf.Max(0.25f, intervalSeconds);
        Debug.Log(message);
    }

    public static void Error(string message) => Debug.LogError(message);

    public static void Reset()
    {
        OnceKeys.Clear();
        NextAllowed.Clear();
    }
}
