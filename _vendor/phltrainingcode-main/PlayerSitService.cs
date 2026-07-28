using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Voluntary /sit pose — fallen state until /sit again or jump (space). Natural slips/falls stay locked down.
/// </summary>
public static class PlayerSitService
{
    private static readonly HashSet<ulong> VoluntarySitClientIds = new HashSet<ulong>();

    private const float SitDownTorque = 32f;
    private const float SitDownImpulse = 5f;
    private const float SeatedPitchDegrees = -88f;
    private const float SidewaysUpwardness = 0.22f;
    private const float SitTransitionSeconds = 0.55f;

    public static bool IsVoluntary(ulong clientId) => VoluntarySitClientIds.Contains(clientId);

    public static void ClearClient(ulong clientId) => VoluntarySitClientIds.Remove(clientId);

    /// <summary>Toggle voluntary sit for a connected player. Server-only.</summary>
    public static string ToggleSit(ulong clientId, PlayerBody body)
    {
        if (body == null)
            return "No player body found.";

        if (IsVoluntary(clientId))
        {
            StandUp(clientId, body);
            return "Stood up.";
        }

        VoluntarySitClientIds.Add(clientId);
        body.StartCoroutine(SitDownRoutine(clientId, body));

        return "Sitting — type /sit again or press jump to stand.";
    }

    public static void StandUp(ulong clientId, PlayerBody body)
    {
        VoluntarySitClientIds.Remove(clientId);
        if (body == null)
            return;

        Rigidbody rb = body.Rigidbody;
        if (rb != null)
        {
            rb.angularVelocity = Vector3.zero;
            Vector3 vel = rb.linearVelocity;
            vel.y = Mathf.Max(vel.y, 0f);
            rb.linearVelocity = vel;
        }

        body.OnStandUp();
    }

    /// <summary>
    /// Mimic natural slip→fall: drain balance, tip backward, drive onto the ice, then enter fallen state.
    /// Calling OnFall() while still upright only toggles the flag and leaves hover fighting upright PID.
    /// </summary>
    private static IEnumerator SitDownRoutine(ulong clientId, PlayerBody body)
    {
        if (body == null)
            yield break;

        body.OnSlip();
        if (body.KeepUpright != null)
            body.KeepUpright.Balance = 0f;

        Rigidbody rb = body.Rigidbody;
        if (rb != null)
        {
            Vector3 vel = rb.linearVelocity;
            vel.x *= 0.2f;
            vel.z *= 0.2f;
            rb.linearVelocity = vel;

            rb.AddTorque(body.transform.right * SitDownTorque, ForceMode.Impulse);
            rb.AddForce(Vector3.down * SitDownImpulse, ForceMode.Impulse);
        }

        float deadline = Time.time + SitTransitionSeconds;
        while (Time.time < deadline && body != null && IsVoluntary(clientId))
        {
            if (body.Upwardness <= SidewaysUpwardness)
                break;

            yield return null;
        }

        if (body == null || !IsVoluntary(clientId))
            yield break;

        if (body.Upwardness > SidewaysUpwardness)
            SnapToSeatedOnIce(body);

        if (!body.HasFallen.Value)
            body.OnFall();

        if (body.KeepUpright != null)
            body.KeepUpright.Balance = 0f;

        if (body.Rigidbody != null)
            body.Rigidbody.AddForce(Vector3.down * 2f, ForceMode.Impulse);
    }

    private static void SnapToSeatedOnIce(PlayerBody body)
    {
        Rigidbody rb = body.Rigidbody;
        if (rb == null)
            return;

        Vector3 forward = body.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f)
            forward = body.transform.right;
        forward.Normalize();

        Quaternion rot = Quaternion.LookRotation(forward, Vector3.up);
        rot *= Quaternion.Euler(SeatedPitchDegrees, 0f, 0f);
        rb.rotation = rot;
        rb.angularVelocity = Vector3.zero;

        Vector3 vel = rb.linearVelocity;
        vel.y = Mathf.Min(vel.y, -1.25f);
        rb.linearVelocity = vel;
    }
}

[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.Jump))]
public static class PlayerSit_JumpPatch
{
    public static bool Prefix(PlayerBody __instance)
    {
        try
        {
            if (__instance == null)
                return true;

            if (!__instance.HasFallen.Value && !__instance.HasSlipped)
                return true;

            ulong clientId = __instance.OwnerClientId;
            if (PlayerSitService.IsVoluntary(clientId))
                PlayerSitService.StandUp(clientId, __instance);

            return false;
        }
        catch
        {
            return true;
        }
    }
}

[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.OnStandUp))]
public static class PlayerSit_OnStandUpPatch
{
    public static bool Prefix(PlayerBody __instance)
    {
        try
        {
            if (__instance == null)
                return true;

            return !PlayerSitService.IsVoluntary(__instance.OwnerClientId);
        }
        catch
        {
            return true;
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.OnNetworkDespawn))]
public static class PlayerSit_OnNetworkDespawnPatch
{
    public static void Postfix(Player __instance)
    {
        try
        {
            if (__instance != null)
                PlayerSitService.ClearClient(__instance.OwnerClientId);
        }
        catch { }
    }
}
