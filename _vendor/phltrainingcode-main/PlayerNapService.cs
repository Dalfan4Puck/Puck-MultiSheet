using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Voluntary /nap pose — fallen state until /nap again or jump (space).
/// Blocks vanilla and CPT auto-stand (balance recovery → upright → OnStandUp).
/// Natural slips/falls stay locked down; jump does not free them.
/// </summary>
public static class PlayerNapService
{
    private static readonly HashSet<ulong> VoluntaryNapClientIds = new HashSet<ulong>();
    private static readonly FieldInfo BalanceRecoveryTweenField =
        AccessTools.Field(typeof(PlayerBody), "balanceRecoveryTween");
    private static MethodInfo _tweenKillMethod;

    private const float NapDownTorque = 32f;
    private const float NapDownImpulse = 5f;
    private const float SeatedPitchDegrees = -88f;
    private const float SidewaysUpwardness = 0.22f;
    private const float NapTransitionSeconds = 0.55f;

    public static bool IsVoluntary(ulong clientId) => VoluntaryNapClientIds.Contains(clientId);

    public static void ClearClient(ulong clientId) => VoluntaryNapClientIds.Remove(clientId);

    /// <summary>Toggle voluntary nap for a connected player. Server-only.</summary>
    public static string ToggleNap(ulong clientId, PlayerBody body)
    {
        if (body == null)
            return "No player body found.";

        if (IsVoluntary(clientId))
        {
            StandUp(clientId, body);
            return "Stood up.";
        }

        VoluntaryNapClientIds.Add(clientId);
        body.StartCoroutine(NapDownRoutine(clientId, body));

        return "Napping — type /nap again or press space to stand.";
    }

    public static void StandUp(ulong clientId, PlayerBody body)
    {
        VoluntaryNapClientIds.Remove(clientId);
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
    /// Kill CPT/vanilla balance recovery so KeepUpright cannot rotate the body upright.
    /// </summary>
    public static void SuppressAutoStand(PlayerBody body)
    {
        if (body == null)
            return;

        if (BalanceRecoveryTweenField != null)
        {
            object tween = BalanceRecoveryTweenField.GetValue(body);
            if (tween != null)
            {
                if (_tweenKillMethod == null)
                {
                    _tweenKillMethod = AccessTools.Method("DG.Tweening.TweenExtensions:Kill",
                        new[] { tween.GetType(), typeof(bool) });
                }

                _tweenKillMethod?.Invoke(null, new object[] { tween, false });
                BalanceRecoveryTweenField.SetValue(body, null);
            }
        }

        if (body.KeepUpright != null)
            body.KeepUpright.Balance = 0f;
    }

    private static IEnumerator NapDownRoutine(ulong clientId, PlayerBody body)
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

            rb.AddTorque(body.transform.right * NapDownTorque, ForceMode.Impulse);
            rb.AddForce(Vector3.down * NapDownImpulse, ForceMode.Impulse);
        }

        float deadline = Time.time + NapTransitionSeconds;
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

[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.OnFall))]
public static class PlayerNap_OnFallPatch
{
    public static void Prefix(PlayerBody __instance, ref bool __state)
    {
        __state = PlayerNapService.IsVoluntary(__instance.OwnerClientId);
    }

    public static void Postfix(PlayerBody __instance, bool __state)
    {
        if (!__state)
            return;

        PlayerNapService.SuppressAutoStand(__instance);
    }
}

[HarmonyPatch(typeof(PlayerBody), "FixedUpdate")]
public static class PlayerNap_FixedUpdatePatch
{
    public static void Postfix(PlayerBody __instance)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
            return;

        if (!PlayerNapService.IsVoluntary(__instance.OwnerClientId))
            return;

        PlayerNapService.SuppressAutoStand(__instance);
    }
}

[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.Jump))]
public static class PlayerNap_JumpPatch
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
            if (PlayerNapService.IsVoluntary(clientId))
                PlayerNapService.StandUp(clientId, __instance);

            return false;
        }
        catch
        {
            return true;
        }
    }
}

[HarmonyPatch(typeof(PlayerBody), nameof(PlayerBody.OnStandUp))]
public static class PlayerNap_OnStandUpPatch
{
    public static bool Prefix(PlayerBody __instance)
    {
        try
        {
            if (__instance == null)
                return true;

            return !PlayerNapService.IsVoluntary(__instance.OwnerClientId);
        }
        catch
        {
            return true;
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.OnNetworkDespawn))]
public static class PlayerNap_OnNetworkDespawnPatch
{
    public static void Postfix(Player __instance)
    {
        try
        {
            if (__instance != null)
                PlayerNapService.ClearClient(__instance.OwnerClientId);
        }
        catch { }
    }
}
