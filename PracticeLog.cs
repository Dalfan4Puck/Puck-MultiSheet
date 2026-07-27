using UnityEngine;

/// <summary>
/// Info-log gate for MultiSheet. Warnings and errors always go straight to
/// Debug.LogWarning/LogError; informational logging routes through here and is
/// silent unless VerboseLogging is enabled in multi_rink.json (server) — the only
/// unconditional lines left are the enable-success line and real failures.
/// Global namespace so the vendored PuckLargeLevel sources can use it too.
/// </summary>
public static class PracticeLog
{
    public static bool Verbose;

    public static void Info(string message)
    {
        if (Verbose) Debug.Log(message);
    }
}
