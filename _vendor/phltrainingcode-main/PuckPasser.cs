using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Bumper pass-back board — returns the puck toward the shooter with random aim
/// between skates (body) and stick blade, led by player speed + distance from bumper.
/// </summary>
public class PuckPasser : MonoBehaviour
{
    public float passSpeed = 18f;
    public float hitCooldown = 0.3f;
    public Transform hitFace;

    private float lastHitTime = -1f;

    private const float MaxTargetRange = 55f;
    private const float LeadDamping = 0.85f;
    private const float MinIncomingSpeed = 1.5f;
    private const float FeetIceY = 0.08f;

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServerSide())
            return;

        TryPassPuck(other);
    }

    internal void TryPassPuck(Collider other, Transform face = null)
    {
        if (other == null || Time.time < lastHitTime + hitCooldown)
            return;

        Puck puck = other.GetComponentInParent<Puck>();
        if (puck == null)
            return;

        Rigidbody puckBody = puck.GetComponent<Rigidbody>();
        if (puckBody == null)
            return;

        Vector3 puckPos = puckBody.position;
        Vector3 puckVel = puckBody.linearVelocity;

        Transform aimFace = face != null ? face : hitFace;
        Player target = FindTargetPlayer(puckPos, puckVel, aimFace);
        if (target == null || target.Stick == null || target.PlayerBody == null)
            return;

        lastHitTime = Time.time;

        Vector3 playerVel = GetPlayerVelocity(target);
        Vector3 targetPoint = PredictPassTarget(puckPos, target, playerVel, passSpeed);
        Vector3? launchVel = CalculateBallisticVelocity(puckPos, targetPoint, passSpeed, highArc: false);

        if (launchVel.HasValue)
        {
            puckBody.linearVelocity = launchVel.Value;
            puckBody.useGravity = true;
        }
        else
        {
            Vector3 flatDir = targetPoint - puckPos;
            flatDir.y = 0f;
            if (flatDir.sqrMagnitude < 0.01f)
                flatDir = (targetPoint - puckPos).normalized;
            else
                flatDir.Normalize();

            puckBody.linearVelocity = flatDir * passSpeed + Vector3.up * 0.35f;
            puckBody.useGravity = true;
        }

        puckBody.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// Random aim between skates (body/feet) and stick blade, with intercept lead on both endpoints.
    /// </summary>
    private static Vector3 PredictPassTarget(
        Vector3 bumperPos,
        Player player,
        Vector3 playerVel,
        float passSpeed)
    {
        Vector3 bladeNow = GetBladePosition(player);
        Vector3 feetNow = GetFeetPosition(player);

        Vector3 ledBlade = PredictLeadPosition(bumperPos, bladeNow, playerVel, passSpeed);
        Vector3 ledFeet = PredictLeadPosition(bumperPos, feetNow, playerVel, passSpeed);

        float blend = UnityEngine.Random.Range(0f, 1f);
        Vector3 target = Vector3.Lerp(ledFeet, ledBlade, blend);

        try
        {
            FlamieLog.Info("[PuckPasser] Pass to " + player.Username.Value +
                      " blend=" + blend.ToString("F2") + " (0=feet,1=blade) → " + target);
        }
        catch { }

        return target;
    }

    private static Vector3 GetBladePosition(Player player)
    {
        try
        {
            return player.Stick.BladeHandlePosition;
        }
        catch
        {
            return GetFeetPosition(player);
        }
    }

    private static Vector3 GetFeetPosition(Player player)
    {
        if (player?.PlayerBody == null)
            return Vector3.zero;

        Vector3 feet = player.PlayerBody.transform.position;
        feet.y = FeetIceY;
        return feet;
    }

    private Player FindTargetPlayer(Vector3 puckPos, Vector3 puckVel, Transform aimFace)
    {
        Vector3 incoming = ResolveIncomingDirection(puckPos, puckVel, aimFace);
        Vector3 rayOrigin = puckPos + Vector3.up * 0.12f;

        Player fromRay = RaycastForPlayer(rayOrigin, incoming, MaxTargetRange);
        if (fromRay != null)
            return fromRay;

        if (aimFace != null)
        {
            fromRay = RaycastForPlayer(aimFace.position, aimFace.forward, MaxTargetRange);
            if (fromRay != null)
                return fromRay;
        }

        return ScoreBestPlayer(puckPos, incoming);
    }

    private Vector3 ResolveIncomingDirection(Vector3 puckPos, Vector3 puckVel, Transform aimFace)
    {
        if (puckVel.sqrMagnitude >= MinIncomingSpeed * MinIncomingSpeed)
            return -puckVel.normalized;

        if (aimFace != null)
            return -aimFace.forward;

        return -GetFaceForward();
    }

    private static Player RaycastForPlayer(Vector3 origin, Vector3 direction, float maxDist)
    {
        if (direction.sqrMagnitude < 0.0001f)
            return null;

        direction.Normalize();
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            direction,
            maxDist,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);

        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            Player player = ResolvePlayerFromCollider(hit.collider);
            if (player != null)
                return player;
        }

        return null;
    }

    private static Player ResolvePlayerFromCollider(Collider col)
    {
        if (col == null)
            return null;

        Stick stick = col.GetComponentInParent<Stick>();
        if (stick != null)
        {
            try
            {
                Player stickOwner = stick.Player;
                if (IsRealHumanPlayer(stickOwner))
                    return stickOwner;
            }
            catch { }
        }

        PlayerBody body = col.GetComponentInParent<PlayerBody>();
        if (body != null)
        {
            try
            {
                Player bodyOwner = body.Player;
                if (IsRealHumanPlayer(bodyOwner))
                    return bodyOwner;
            }
            catch { }
        }

        return null;
    }

    private static Player ScoreBestPlayer(Vector3 puckPos, Vector3 incoming)
    {
        PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
        if (pm == null)
            return null;

        Player best = null;
        float bestScore = float.MaxValue;

        foreach (Player player in pm.GetSpawnedPlayers(false))
        {
            if (!IsRealHumanPlayer(player) || player.Stick == null)
                continue;

            Vector3 bladePos;
            try
            {
                bladePos = player.Stick.BladeHandlePosition;
            }
            catch
            {
                continue;
            }

            Vector3 toBlade = bladePos - puckPos;
            float along = Vector3.Dot(toBlade, incoming);
            if (along < 0.5f)
                continue;

            Vector3 closestOnRay = puckPos + incoming * along;
            float lateral = Vector3.Distance(closestOnRay, bladePos);
            float dist = Vector3.Distance(puckPos, bladePos);
            if (dist > MaxTargetRange)
                continue;

            float score = lateral + dist * 0.05f;
            if (score < bestScore)
            {
                bestScore = score;
                best = player;
            }
        }

        return best;
    }

    private static bool IsRealHumanPlayer(Player player)
    {
        if (player == null)
            return false;

        try
        {
            if (FakePlayerDetector.IsMaxPracticeFakePlayer(player))
                return false;
        }
        catch { }

        try
        {
            if (player.IsReplay != null && player.IsReplay.Value)
                return false;
        }
        catch { }

        return true;
    }

    private static Vector3 GetPlayerVelocity(Player player)
    {
        if (player?.PlayerBody == null)
            return Vector3.zero;

        Rigidbody bodyRb = player.PlayerBody.GetComponent<Rigidbody>();
        if (bodyRb == null)
            return Vector3.zero;

        Vector3 vel = bodyRb.linearVelocity;
        vel.y = 0f;
        return vel;
    }

    /// <summary>
    /// Predict where a target point will be at puck arrival (intercept time from bumper distance + player speed).
    /// </summary>
    private static Vector3 PredictLeadPosition(
        Vector3 bumperPos,
        Vector3 targetNow,
        Vector3 playerVel,
        float passSpeed)
    {
        float targetY = targetNow.y;
        Vector3 aim = targetNow;

        for (int i = 0; i < 3; i++)
        {
            float travelTime = SolveInterceptTime(bumperPos, aim, playerVel, passSpeed);
            aim = targetNow + playerVel * (travelTime * LeadDamping);
            aim.y = targetY;
        }

        return aim;
    }

    /// <summary>
    /// Smallest positive time T such that |blade + playerVel*T - bumper| ≈ passSpeed * T.
    /// Falls back to distance/passSpeed when no valid intercept exists.
    /// </summary>
    private static float SolveInterceptTime(
        Vector3 bumperPos,
        Vector3 bladePos,
        Vector3 playerVel,
        float passSpeed)
    {
        Vector3 delta = bladePos - bumperPos;
        delta.y = 0f;

        Vector3 vel = playerVel;
        vel.y = 0f;

        float distance = delta.magnitude;
        if (distance < 0.05f || passSpeed < 0.1f)
            return 0.1f;

        float fallback = distance / passSpeed;

        float playerSpeedSq = Vector3.Dot(vel, vel);
        float puckSpeedSq = passSpeed * passSpeed;
        float a = playerSpeedSq - puckSpeedSq;
        float b = 2f * Vector3.Dot(delta, vel);
        float c = Vector3.Dot(delta, delta);

        if (Mathf.Abs(a) < 0.0001f)
        {
            if (Mathf.Abs(b) < 0.0001f)
                return fallback;

            float linearT = -c / b;
            return linearT > 0.01f ? linearT : fallback;
        }

        float discriminant = b * b - 4f * a * c;
        if (discriminant < 0f)
            return fallback;

        float sqrtDisc = Mathf.Sqrt(discriminant);
        float t1 = (-b - sqrtDisc) / (2f * a);
        float t2 = (-b + sqrtDisc) / (2f * a);

        float best = -1f;
        if (t1 > 0.01f)
            best = t1;
        if (t2 > 0.01f && (best < 0f || t2 < best))
            best = t2;

        if (best > 0f)
            return best;

        return fallback;
    }

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

    private Vector3 GetFaceForward()
    {
        if (hitFace != null)
            return hitFace.forward;

        return transform.forward;
    }

    private static bool IsServerSide()
    {
        try
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm == null || nm.IsServer;
        }
        catch
        {
            return true;
        }
    }
}
