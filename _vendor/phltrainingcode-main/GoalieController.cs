using System;
using UnityEngine;

/// <summary>
/// Decorative training hive goalie — body movement/intercept ported from AIGoaliesStandalone.GoalieAI.
/// </summary>
public class GoalieController : MonoBehaviour
{
    public float moveSpeed = 12f;
    public float creaseDepth = 1.2f;
    public float goalWidth = 1.5f;
    public float updateInterval = 0.0675f;

    private Vector3 goalPos;
    private Vector3 creaseHome;
    private Vector3 faceDirection;
    private Vector3 prefabSpawnHint;
    private bool aligned;

    private Puck trackedPuck;
    private float nextUpdateTime;

    private void Awake()
    {
        // Prefab author placed GoalieModel at the correct net — keep that as the anchor hint.
        prefabSpawnHint = transform.position;
    }

    private void Start()
    {
        AlignToNet();
    }

    private void LateUpdate()
    {
        if (!aligned)
            AlignToNet();

        if (Time.time < nextUpdateTime)
            return;

        nextUpdateTime = Time.time + updateInterval;
        TickGoalie();
    }

    public void AlignToNet()
    {
        Transform trainingRoot = FindTrainingRoot();
        if (trainingRoot == null)
        {
            CachePose(transform.position, transform.forward);
            aligned = true;
            return;
        }

        if (!GoalieNetAlign.TryGetCreasePose(
                trainingRoot,
                transform,
                prefabSpawnHint,
                creaseDepth,
                out Vector3 creaseCenter,
                out Vector3 faceDir,
                out Vector3 netCenter))
        {
            CachePose(transform.position, transform.forward);
            aligned = true;
            FlamieLog.Warn("[FlamiePrac] Goalie net anchor not found — using prefab pose.");
            return;
        }

        faceDir = TrainingGoalieLogic.ResolveFaceTowardIce(trainingRoot, netCenter);
        CachePose(netCenter, faceDir);
        creaseHome = creaseCenter;
        transform.position = creaseHome;
        transform.rotation = Quaternion.LookRotation(faceDir, Vector3.up);

        aligned = true;
        FlamieLog.Info("[FlamiePrac] Goalie aligned: net=" + goalPos + " crease=" + creaseHome);
    }

    private void TickGoalie()
    {
        trackedPuck = TrainingGoalieLogic.GetBestPuck(goalPos, faceDirection);

        if (trackedPuck == null)
        {
            ReturnToCrease();
            return;
        }

        Rigidbody puckBody = trackedPuck.GetComponent<Rigidbody>();
        Vector3 puckPos = trackedPuck.transform.position;
        Vector3 puckVel = puckBody != null ? puckBody.linearVelocity : Vector3.zero;

        if (!TrainingGoalieLogic.TryComputeIntercept(
                goalPos,
                faceDirection,
                transform.position,
                puckPos,
                puckVel,
                goalWidth,
                out Vector3 intercept))
        {
            ReturnToCrease();
            SquareToPuck(puckPos, puckVel, inCrease: false);
            return;
        }

        Vector3 toIntercept = intercept - transform.position;
        toIntercept.y = 0f;
        transform.position = Vector3.MoveTowards(
            transform.position,
            intercept,
            moveSpeed * updateInterval);

        SquareToPuck(puckPos, puckVel, inCrease: Vector3.Distance(puckPos, goalPos) < 3f);
    }

    private void ReturnToCrease()
    {
        transform.position = Vector3.MoveTowards(
            transform.position,
            creaseHome,
            moveSpeed * updateInterval);

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            Quaternion.LookRotation(faceDirection, Vector3.up),
            TrainingGoalieLogic.SquaringDegPerSec * updateInterval);
    }

    private void SquareToPuck(Vector3 puckPos, Vector3 puckVel, bool inCrease)
    {
        Vector3 squareTarget = puckPos + puckVel * TrainingGoalieLogic.ShotLeadTime;
        Vector3 toPuck = squareTarget - transform.position;
        toPuck.y = 0f;
        if (toPuck.sqrMagnitude < 0.01f)
            return;

        Quaternion targetRot;
        if (inCrease)
        {
            targetRot = Quaternion.LookRotation(toPuck.normalized, Vector3.up);
        }
        else
        {
            Vector3 toPuckFromGoal = squareTarget - goalPos;
            toPuckFromGoal.y = 0f;
            if (toPuckFromGoal.sqrMagnitude < 0.01f)
                return;

            float angle = Vector3.SignedAngle(faceDirection, toPuckFromGoal.normalized, Vector3.up);
            angle = Mathf.Clamp(angle, -55f, 55f);
            targetRot = Quaternion.LookRotation(
                Quaternion.AngleAxis(angle, Vector3.up) * faceDirection,
                Vector3.up);
        }

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRot,
            TrainingGoalieLogic.SquaringDegPerSec * updateInterval);
    }

    private void CachePose(Vector3 netCenter, Vector3 faceDir)
    {
        goalPos = netCenter;
        faceDirection = faceDir.sqrMagnitude > 0.001f ? faceDir.normalized : Vector3.forward;
        creaseHome = goalPos + faceDirection * creaseDepth;
        creaseHome.y = transform.position.y;
        goalPos.y = transform.position.y;
    }

    public Vector3 FaceDirection => faceDirection;

    public bool TryGetTrackedPuck(out Puck puck, out Vector3 puckPos, out Vector3 puckVel)
    {
        puck = trackedPuck;
        puckPos = Vector3.zero;
        puckVel = Vector3.zero;

        if (trackedPuck == null)
            return false;

        puckPos = trackedPuck.transform.position;
        Rigidbody rb = trackedPuck.GetComponent<Rigidbody>();
        puckVel = rb != null ? rb.linearVelocity : Vector3.zero;
        return true;
    }

    private Transform FindTrainingRoot()
    {
        Transform current = transform;
        while (current.parent != null)
        {
            if (current.name.StartsWith("Training_", StringComparison.OrdinalIgnoreCase))
                return current;
            current = current.parent;
        }

        return transform.root;
    }
}

internal static class GoalieNetAlign
{
    private static readonly string[] PreferredNetRoots =
    {
        "goaltarp", "goal_tarp", "goaltarpv1", "shooter", "tutor", "walltarget"
    };

    private static readonly string[] NetNameHints =
    {
        "goaltarp", "goal_tarp", "goaltarget", "goalnet", "hockeynet", "netframe", "goalframe",
        "goalpost", "crossbar", "netting", "shooter", "tutor", "walltarget"
    };

    private static readonly string[] NetLooseHints = { "net", "tarp", "post", "frame" };

    private const float ClusterMergeDistance = 4f;

    public static bool TryGetCreasePose(
        Transform trainingRoot,
        Transform goalie,
        Vector3 goalieHintWorld,
        float creaseDepth,
        out Vector3 creaseCenter,
        out Vector3 faceDirection,
        out Vector3 netCenter)
    {
        creaseCenter = goalieHintWorld;
        faceDirection = Vector3.forward;
        netCenter = goalieHintWorld;

        if (trainingRoot == null)
            return false;

        goalieHintWorld.y = goalie.position.y;

        if (TryGetSubtreeBounds(trainingRoot, goalie, FindPreferredNetRoot(trainingRoot, goalie), out Bounds preferredBounds))
        {
        ApplyBounds(preferredBounds, trainingRoot, goalie, creaseDepth, out creaseCenter, out faceDirection, out netCenter);
        creaseCenter = PreferPrefabCrease(goalieHintWorld, netCenter, creaseCenter);
        FlamieLog.Info("[FlamiePrac] Goalie net: using preferred Goaltarp/shooter root at " + netCenter);
        return true;
        }

        if (!TryGetNearestNetCluster(trainingRoot, goalie, goalieHintWorld, out Bounds clusterBounds))
            return false;

        ApplyBounds(clusterBounds, trainingRoot, goalie, creaseDepth, out creaseCenter, out faceDirection, out netCenter);
        creaseCenter = PreferPrefabCrease(goalieHintWorld, netCenter, creaseCenter);
        FlamieLog.Info("[FlamiePrac] Goalie net: using nearest cluster to prefab hint at " + netCenter);
        return true;
    }

    private static Vector3 PreferPrefabCrease(Vector3 goalieHintWorld, Vector3 netCenter, Vector3 computedCrease)
    {
        Vector3 hintFlat = goalieHintWorld;
        Vector3 netFlat = netCenter;
        hintFlat.y = 0f;
        netFlat.y = 0f;

        if (Vector3.Distance(hintFlat, netFlat) <= 4f)
            return goalieHintWorld;

        return computedCrease;
    }

    private static void ApplyBounds(
        Bounds netBounds,
        Transform trainingRoot,
        Transform goalie,
        float creaseDepth,
        out Vector3 creaseCenter,
        out Vector3 faceDirection,
        out Vector3 netCenter)
    {
        netCenter = netBounds.center;
        netCenter.y = goalie.position.y;
        faceDirection = TrainingGoalieLogic.ResolveFaceTowardIce(trainingRoot, netCenter);
        creaseCenter = netCenter + faceDirection * creaseDepth;
        creaseCenter.y = goalie.position.y;
    }

    private static Transform FindPreferredNetRoot(Transform trainingRoot, Transform goalie)
    {
        Transform best = null;
        int bestScore = int.MinValue;

        foreach (Transform child in trainingRoot.GetComponentsInChildren<Transform>(true))
        {
            if (child == null || IsUnderGoalie(child, goalie))
                continue;

            string lower = child.name.ToLowerInvariant();
            int score = 0;

            foreach (string hint in PreferredNetRoots)
            {
                if (lower.Contains(hint))
                    score += 200;
            }

            foreach (string hint in NetNameHints)
            {
                if (lower.Contains(hint))
                    score += 80;
            }

            if (score <= 0)
                continue;

            // Prefer roots higher in the hierarchy (shorter path from training root).
            score -= child.GetComponentsInParent<Transform>(true).Length;

            if (score > bestScore)
            {
                bestScore = score;
                best = child;
            }
        }

        return best;
    }

    private static bool TryGetSubtreeBounds(
        Transform trainingRoot,
        Transform goalie,
        Transform subtreeRoot,
        out Bounds bounds)
    {
        bounds = default;
        if (subtreeRoot == null)
            return false;

        bool hasBounds = false;
        foreach (Renderer renderer in subtreeRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || IsUnderGoalie(renderer.transform, goalie))
                continue;

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private struct NetCluster
    {
        public Bounds Bounds;
        public int NameScore;
    }

    private static bool TryGetNearestNetCluster(
        Transform trainingRoot,
        Transform goalie,
        Vector3 goalieHintWorld,
        out Bounds bounds)
    {
        bounds = default;
        var clusters = new System.Collections.Generic.List<NetCluster>();

        foreach (Renderer renderer in trainingRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || IsUnderGoalie(renderer.transform, goalie))
                continue;

            int nameScore = ScoreNetName(renderer.gameObject.name);
            if (nameScore <= 0)
                continue;

            Vector3 center = renderer.bounds.center;
            bool merged = false;

            for (int i = 0; i < clusters.Count; i++)
            {
                NetCluster cluster = clusters[i];
                if (Vector3.Distance(cluster.Bounds.center, center) > ClusterMergeDistance)
                    continue;

                cluster.Bounds.Encapsulate(renderer.bounds);
                cluster.NameScore = Mathf.Max(cluster.NameScore, nameScore);
                clusters[i] = cluster;
                merged = true;
                break;
            }

            if (!merged)
            {
                clusters.Add(new NetCluster
                {
                    Bounds = renderer.bounds,
                    NameScore = nameScore
                });
            }
        }

        if (clusters.Count == 0)
            return false;

        NetCluster best = clusters[0];
        float bestRank = RankCluster(best, goalieHintWorld);

        for (int i = 1; i < clusters.Count; i++)
        {
            float rank = RankCluster(clusters[i], goalieHintWorld);
            if (rank > bestRank)
            {
                bestRank = rank;
                best = clusters[i];
            }
        }

        bounds = best.Bounds;
        return true;
    }

    private static float RankCluster(NetCluster cluster, Vector3 goalieHintWorld)
    {
        Vector3 flatHint = goalieHintWorld;
        Vector3 flatCenter = cluster.Bounds.center;
        flatHint.y = 0f;
        flatCenter.y = 0f;

        float distance = Vector3.Distance(flatHint, flatCenter);
        // Closer to prefab goalie placement wins; name score breaks ties.
        return cluster.NameScore * 10f - distance;
    }

    private static int ScoreNetName(string name)
    {
        string lower = (name ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("goalie"))
            return -100;

        foreach (string hint in NetNameHints)
        {
            if (lower.Contains(hint))
                return 100;
        }

        foreach (string hint in NetLooseHints)
        {
            if (lower.Contains(hint))
                return 40;
        }

        return 0;
    }

    private static bool IsUnderGoalie(Transform candidate, Transform goalie)
    {
        if (candidate == null || goalie == null)
            return false;

        return candidate == goalie || candidate.IsChildOf(goalie);
    }
}
