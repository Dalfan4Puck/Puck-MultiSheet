using System;
using System.Collections;
using System.Collections.Generic;
using MaxPractice;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Per-rink persistent save/tip practice (MaxPractice /saveprac + /tipprac logic).
    /// Runs until the rink strip mode changes or tools are cleared — no 2-minute timer.
    /// </summary>
    internal static class RinkPracticeDrills
    {
        private const float NetBehindCrease = 1.85f;
        private const float RinkXMin = -26f;
        private const float RinkXMax = 26f;
        private const float RinkZMin = -42f;
        private const float RinkZMax = 42f;

        // Goalie strip mode — wide release-speed spread; arrival cadence fixed at the net.
        private const float GoalieFastShotChance = 0.55f;
        /// <summary>Fixed cadence at the net — releases are scheduled backwards from this interval.</summary>
        private const float GoalieSaveInterval = 2f;
        /// <summary>Pucks sit at the spawn point this long before the first release.</summary>
        private const float GoalieQueueVisibleLeadTime = 2.75f;
        /// <summary>Always two pucks visible: shooter + on-deck holder (A/B, then B/C, …).</summary>
        private const int GoalieHoldQueueDepth = 2;
        private const float GoaliePastGoalDespawnMargin = 0.35f;
        /// <summary>Visible time in the net area after crossing the goal plane.</summary>
        private const float GoaliePostGoalDespawnGrace = 0.85f;
        /// <summary>Fallback despawn for shots that miss the net entirely.</summary>
        private const float GoalieMissedShotSafetySeconds = 3.5f;
        private const int GoalieMaxFlyingPucks = 4;
        private const float GoalieShotMinDist = 8f;
        /// <summary>Most of the sheet in front of the defended net (~94-length rink).</summary>
        private const float GoalieShotMaxDist = 52f;
        private const float GoalieSpawnBehindGoalMargin = 0.75f;
        private const float NetHalfWidth = 0.91f;
        private const float NetHeight = 1.22f;

        private struct PendingGoalieShot
        {
            internal Puck Puck;
            internal Vector3 SpawnPos;
            internal Vector3 AimPoint;
            internal bool IsFastShot;
            internal Vector3 LaunchVelocity;
            internal float TravelTime;
            internal float GoalArrivalAt;
            internal float FireAt;
        }

        private static readonly Dictionary<int, Coroutine> saveLoops = new Dictionary<int, Coroutine>();
        private static readonly Dictionary<int, Coroutine> tipLoops = new Dictionary<int, Coroutine>();
        private static readonly Dictionary<int, List<Puck>> rinkPucks = new Dictionary<int, List<Puck>>();
        private static readonly Dictionary<int, List<PendingGoalieShot>> goaliePendingShots =
            new Dictionary<int, List<PendingGoalieShot>>();
        /// <summary>Next planned goal-plane arrival (server time) per rink — drives dynamic release times.</summary>
        private static readonly Dictionary<int, float> goalieNextGoalArrival =
            new Dictionary<int, float>();
        private static readonly Dictionary<Puck, float> goaliePuckSafetyExpireAt = new Dictionary<Puck, float>();
        private static readonly Dictionary<Puck, float> goaliePuckCrossedGoalAt = new Dictionary<Puck, float>();

        internal static void ApplyMode(int rinkIndex, RinkStripMode mode)
        {
            StopRink(rinkIndex);
            if (mode == RinkStripMode.GoaliePractice)
                StartSaveLoop(rinkIndex);
            else if (mode == RinkStripMode.TipPractice)
                StartTipLoop(rinkIndex);
        }

        internal static void StopAll()
        {
            var indices = new HashSet<int>();
            foreach (int k in saveLoops.Keys) indices.Add(k);
            foreach (int k in tipLoops.Keys) indices.Add(k);
            foreach (int k in rinkPucks.Keys) indices.Add(k);
            foreach (int k in indices)
                StopRink(k);
        }

        internal static void TickReconcile()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            int count = MultiRinkConfig.Current.Rinks?.Count ?? 0;
            if (count <= 0) count = 6;

            for (int i = 0; i < count; i++)
            {
                RinkStripMode mode = RinkStripVote.GetServerMode(i);
                if (mode == RinkStripMode.GoaliePractice && !saveLoops.ContainsKey(i))
                    StartSaveLoop(i);
                else if (mode == RinkStripMode.TipPractice && !tipLoops.ContainsKey(i))
                    StartTipLoop(i);
            }
        }

        private static void StopRink(int rinkIndex)
        {
            MonoBehaviour host = CoroutineHost;
            if (host != null)
            {
                if (saveLoops.TryGetValue(rinkIndex, out Coroutine save) && save != null)
                    host.StopCoroutine(save);
                if (tipLoops.TryGetValue(rinkIndex, out Coroutine tip) && tip != null)
                    host.StopCoroutine(tip);
            }

            saveLoops.Remove(rinkIndex);
            tipLoops.Remove(rinkIndex);
            goalieNextGoalArrival.Remove(rinkIndex);
            CleanupPendingGoalieShots(rinkIndex);
            CleanupRinkPucks(rinkIndex);
        }

        private static void StartSaveLoop(int rinkIndex)
        {
            MonoBehaviour host = CoroutineHost;
            if (host == null) return;
            if (saveLoops.ContainsKey(rinkIndex)) return;

            Coroutine loop = host.StartCoroutine(SavePracticeLoop(rinkIndex));
            saveLoops[rinkIndex] = loop;
            PracticeLog.Info("[PHLPractice] Rink " + (rinkIndex + 1) + " save practice started (persistent).");
        }

        private static void StartTipLoop(int rinkIndex)
        {
            MonoBehaviour host = CoroutineHost;
            if (host == null) return;
            if (tipLoops.ContainsKey(rinkIndex)) return;

            Coroutine loop = host.StartCoroutine(TipPracticeLoop(rinkIndex));
            tipLoops[rinkIndex] = loop;
            PracticeLog.Info("[PHLPractice] Rink " + (rinkIndex + 1) + " tip practice started (persistent).");
        }

        private static bool ShouldRun(int rinkIndex, RinkStripMode expected)
        {
            return RinkStripVote.GetServerMode(rinkIndex) == expected;
        }

        private static MonoBehaviour CoroutineHost
        {
            get
            {
                try { return NetworkBehaviourSingleton<GameManager>.Instance; }
                catch { return null; }
            }
        }

        private static IEnumerator SavePracticeLoop(int rinkIndex)
        {
            const bool isShootingAtRedGoal = false;

            try
            {
                while (ShouldRun(rinkIndex, RinkStripMode.GoaliePractice))
                {
                    if (!TryGetRinkSlot(rinkIndex, out RinkSlot slot)
                        || !TryGetNetWorld(slot, PlayerTeam.Blue, out Vector3 targetGoalPos))
                    {
                        yield return new WaitForSeconds(1f);
                        continue;
                    }

                    List<PendingGoalieShot> queue = GetGoaliePendingList(rinkIndex);

                    while (queue.Count < GoalieHoldQueueDepth
                           && ShouldRun(rinkIndex, RinkStripMode.GoaliePractice))
                    {
                        if (!TryEnqueueGoalieShot(rinkIndex, slot, targetGoalPos, queue))
                            break;
                    }

                    if (queue.Count == 0)
                    {
                        yield return new WaitForSeconds(0.5f);
                        continue;
                    }

                    float releaseAt = queue[0].FireAt;
                    while (Time.time < releaseAt && ShouldRun(rinkIndex, RinkStripMode.GoaliePractice))
                    {
                        yield return new WaitForSeconds(0.05f);
                        CheckAndCleanupGoaliePucks(rinkIndex, isShootingAtRedGoal, targetGoalPos);
                    }

                    if (!ShouldRun(rinkIndex, RinkStripMode.GoaliePractice))
                        break;

                    PendingGoalieShot shot = queue[0];
                    queue.RemoveAt(0);

                    FireGoalieShot(shot);
                    if (shot.Puck != null)
                        TrackGoaliePuck(rinkIndex, shot.Puck, shot.FireAt, shot.TravelTime);

                    if (queue.Count < GoalieHoldQueueDepth
                        && ShouldRun(rinkIndex, RinkStripMode.GoaliePractice))
                        TryEnqueueGoalieShot(rinkIndex, slot, targetGoalPos, queue);

                    CheckAndCleanupGoaliePucks(rinkIndex, isShootingAtRedGoal, targetGoalPos);
                }
            }
            finally
            {
                saveLoops.Remove(rinkIndex);
                goalieNextGoalArrival.Remove(rinkIndex);
                CleanupPendingGoalieShots(rinkIndex);
                CleanupRinkPucks(rinkIndex);
            }
        }

        private static List<PendingGoalieShot> GetGoaliePendingList(int rinkIndex)
        {
            if (!goaliePendingShots.TryGetValue(rinkIndex, out List<PendingGoalieShot> list))
            {
                list = new List<PendingGoalieShot>(GoalieHoldQueueDepth);
                goaliePendingShots[rinkIndex] = list;
            }
            return list;
        }

        private static void CleanupPendingGoalieShots(int rinkIndex)
        {
            if (!goaliePendingShots.TryGetValue(rinkIndex, out List<PendingGoalieShot> list))
                return;

            for (int i = 0; i < list.Count; i++)
            {
                Puck p = list[i].Puck;
                if (p != null && p.gameObject != null)
                {
                    try { UnityEngine.Object.Destroy(p.gameObject); }
                    catch { }
                }
            }

            goaliePendingShots.Remove(rinkIndex);
        }

        private static void PlanGoalieShot(
            RinkSlot slot,
            Vector3 targetGoalPos,
            out Vector3 spawnPos,
            out Vector3 aimPoint,
            out bool isFastShot)
        {
            isFastShot = UnityEngine.Random.value < GoalieFastShotChance;
            float iceY = targetGoalPos.y;
            bool blueEndGoal = targetGoalPos.z >= slot.Origin.z;
            Vector3 attackDir = blueEndGoal ? Vector3.back : Vector3.forward;
            float goalLineZ = targetGoalPos.z;

            spawnPos = BuildFallbackGoalieSpawn(targetGoalPos, attackDir, iceY);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                float dist = UnityEngine.Random.Range(GoalieShotMinDist, GoalieShotMaxDist);
                float yaw = UnityEngine.Random.Range(-88f, 88f);
                Vector3 offset = Quaternion.Euler(0f, yaw, 0f) * attackDir * dist;
                Vector3 candidate = targetGoalPos + offset;
                candidate.y = iceY;
                candidate = ClampToRinkLocal(candidate, slot);

                if (!IsGoalieSpawnInFrontOfNet(candidate, goalLineZ, blueEndGoal))
                    continue;

                Vector3 netFlat = new Vector3(targetGoalPos.x, 0f, targetGoalPos.z);
                Vector3 spawnFlat = new Vector3(candidate.x, 0f, candidate.z);
                float actualDist = Vector3.Distance(spawnFlat, netFlat);
                if (actualDist < GoalieShotMinDist * 0.85f || actualDist > GoalieShotMaxDist * 1.05f)
                    continue;

                spawnPos = candidate;
                break;
            }

            spawnPos.y = iceY;
            aimPoint = targetGoalPos + RandomNetTargetOffset();
        }

        private static Vector3 BuildFallbackGoalieSpawn(Vector3 targetGoalPos, Vector3 attackDir, float iceY)
        {
            float dist = UnityEngine.Random.Range(GoalieShotMinDist, GoalieShotMaxDist * 0.65f);
            Vector3 spawn = targetGoalPos + attackDir * dist;
            spawn.y = iceY;
            return spawn;
        }

        /// <summary>Reject spawns on the wrong side of the defended goal line / net.</summary>
        private static bool IsGoalieSpawnInFrontOfNet(Vector3 spawn, float goalLineZ, bool blueEndGoal)
        {
            if (blueEndGoal)
                return spawn.z <= goalLineZ - GoalieSpawnBehindGoalMargin;
            return spawn.z >= goalLineZ + GoalieSpawnBehindGoalMargin;
        }

        private static bool TryEnqueueGoalieShot(
            int rinkIndex,
            RinkSlot slot,
            Vector3 targetGoalPos,
            List<PendingGoalieShot> queue)
        {
            PlanGoalieShot(slot, targetGoalPos, out Vector3 spawnPos, out Vector3 aimPoint, out bool isFastShot);

            Puck puck = PracticeHelpers.SpawnPuckWithCleanup(
                spawnPos, Quaternion.identity, Vector3.zero, false);
            if (puck == null || puck.Rigidbody == null)
                return false;

            puck.Rigidbody.linearVelocity = Vector3.zero;
            puck.Rigidbody.angularVelocity = Vector3.zero;
            puck.Rigidbody.isKinematic = true;

            Vector3 flat = aimPoint - spawnPos;
            flat.y = 0f;
            float horizontalDist = flat.magnitude;
            float distT = Mathf.InverseLerp(GoalieShotMinDist, GoalieShotMaxDist, horizontalDist);
            GoalieShotPhysics.GoalieShotStyle style = GoalieShotPhysics.PickShotStyle(distT);
            bool shootFromPositiveZ = spawnPos.z > targetGoalPos.z;

            if (!GoalieShotPhysics.TryBuildLaunch(
                    spawnPos,
                    aimPoint,
                    targetGoalPos.z,
                    shootFromPositiveZ,
                    style,
                    isFastShot,
                    puck,
                    out GoalieShotPhysics.GoalieShotLaunch launch))
            {
                try { UnityEngine.Object.Destroy(puck.gameObject); }
                catch { }
                return false;
            }

            float travelTime = Mathf.Max(launch.TravelTime, 0.12f);

            if (!goalieNextGoalArrival.TryGetValue(rinkIndex, out float nextArrival)
                || nextArrival <= Time.time)
            {
                nextArrival = Time.time + GoalieQueueVisibleLeadTime + travelTime;
            }

            float arrivalAt = nextArrival;
            float fireAt = arrivalAt - travelTime;
            fireAt = Mathf.Max(fireAt, Time.time + 0.15f);

            goalieNextGoalArrival[rinkIndex] = arrivalAt + GoalieSaveInterval;

            queue.Add(new PendingGoalieShot
            {
                Puck = puck,
                SpawnPos = spawnPos,
                AimPoint = aimPoint,
                IsFastShot = isFastShot,
                LaunchVelocity = launch.Velocity,
                TravelTime = travelTime,
                GoalArrivalAt = arrivalAt,
                FireAt = fireAt,
            });
            return true;
        }

        private static void FireGoalieShot(PendingGoalieShot shot)
        {
            if (shot.Puck == null || shot.Puck.Rigidbody == null)
                return;

            shot.Puck.Rigidbody.isKinematic = false;
            shot.Puck.Rigidbody.linearVelocity = shot.LaunchVelocity;
        }

        private static IEnumerator TipPracticeLoop(int rinkIndex)
        {
            while (ShouldRun(rinkIndex, RinkStripMode.TipPractice))
            {
                if (!TryGetRinkSlot(rinkIndex, out RinkSlot slot))
                {
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                Player tipper = FindSkaterOnRink(rinkIndex, slot);
                if (tipper == null || tipper.PlayerBody == null)
                {
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                Vector3 tipperPos = tipper.PlayerBody.transform.position;

                if (!TryGetNetWorld(slot, PlayerTeam.Blue, out Vector3 targetGoalPos))
                {
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                const bool isRedGoal = false;

                float tipH = UnityEngine.Random.value < 0.10f
                    ? UnityEngine.Random.Range(0.05f, 0.22f)
                    : UnityEngine.Random.Range(0.40f, 1.55f);

                Vector3 passTarget = new Vector3(
                    tipperPos.x + UnityEngine.Random.Range(-0.8f, 0.8f),
                    tipH,
                    tipperPos.z + UnityEngine.Random.Range(-0.8f, 0.8f));

                int shotType = UnityEngine.Random.Range(0, 6);
                Vector3 spawnPos;
                const float inFront = -1f;

                switch (shotType)
                {
                    case 0:
                    {
                        float back = UnityEngine.Random.Range(12f, 22f);
                        spawnPos = new Vector3(
                            UnityEngine.Random.Range(-4f, 4f), 0.05f,
                            tipperPos.z + inFront * back);
                        break;
                    }
                    case 1:
                    {
                        float side = UnityEngine.Random.Range(8f, 16f);
                        float back = UnityEngine.Random.Range(4f, 14f);
                        spawnPos = new Vector3(-side, 0.05f, tipperPos.z + inFront * back);
                        passTarget.x = UnityEngine.Random.Range(-0.9f, 0.2f);
                        break;
                    }
                    case 2:
                    {
                        float side = UnityEngine.Random.Range(8f, 16f);
                        float back = UnityEngine.Random.Range(4f, 14f);
                        spawnPos = new Vector3(side, 0.05f, tipperPos.z + inFront * back);
                        passTarget.x = UnityEngine.Random.Range(-0.2f, 0.9f);
                        break;
                    }
                    case 3:
                    {
                        float back = UnityEngine.Random.Range(16f, 26f);
                        spawnPos = new Vector3(
                            -UnityEngine.Random.Range(4f, 10f), 0.05f,
                            tipperPos.z + inFront * back);
                        tipH = Mathf.Max(tipH, UnityEngine.Random.Range(0.6f, 1.5f));
                        passTarget.y = tipH;
                        break;
                    }
                    case 4:
                    {
                        float back = UnityEngine.Random.Range(16f, 26f);
                        spawnPos = new Vector3(
                            UnityEngine.Random.Range(4f, 10f), 0.05f,
                            tipperPos.z + inFront * back);
                        tipH = Mathf.Max(tipH, UnityEngine.Random.Range(0.6f, 1.5f));
                        passTarget.y = tipH;
                        break;
                    }
                    default:
                    {
                        float side = UnityEngine.Random.value > 0.5f ? 1f : -1f;
                        spawnPos = new Vector3(
                            side * UnityEngine.Random.Range(12f, 20f), 0.05f,
                            tipperPos.z + inFront * UnityEngine.Random.Range(2f, 8f));
                        passTarget.x = UnityEngine.Random.Range(-side * 1.0f, side * 0.2f);
                        break;
                    }
                }

                spawnPos = ClampToRinkLocal(spawnPos, slot);

                Puck spawnedPuck = PracticeHelpers.SpawnPuckWithCleanup(
                    spawnPos, Quaternion.identity, Vector3.zero, false);

                if (spawnedPuck != null && spawnedPuck.Rigidbody != null)
                {
                    spawnedPuck.Rigidbody.linearVelocity = Vector3.zero;
                    spawnedPuck.Rigidbody.isKinematic = true;
                }

                yield return new WaitForSeconds(PracticeConstants.TipPuckRestTime);

                if (!ShouldRun(rinkIndex, RinkStripMode.TipPractice))
                    break;

                if (spawnedPuck != null && spawnedPuck.Rigidbody != null)
                {
                    spawnedPuck.Rigidbody.isKinematic = false;

                    float speedMph = UnityEngine.Random.Range(
                        PracticeConstants.TipShotMinSpeedMph,
                        PracticeConstants.TipShotMaxSpeedMph);
                    float horizontalSpeed = PracticeHelpers.MphToMps(speedMph);

                    Vector3 horizontal = passTarget - spawnPos;
                    horizontal.y = 0f;
                    float hDist = Mathf.Max(horizontal.magnitude, 0.01f);
                    float t = hDist / horizontalSpeed;
                    float dyTarget = passTarget.y - spawnPos.y;
                    float g = Mathf.Abs(Physics.gravity.y);
                    float vy = (dyTarget + 0.5f * g * t * t) / t;
                    if (passTarget.y < 0.25f) vy = Mathf.Clamp(vy, 0f, 3.5f);

                    spawnedPuck.Rigidbody.linearVelocity = horizontal.normalized * horizontalSpeed + Vector3.up * vy;
                }

                if (spawnedPuck != null)
                    TrackPuck(rinkIndex, spawnedPuck);
                else
                    continue;

                float waitTime = UnityEngine.Random.Range(
                    PracticeConstants.TipMinTimeBetweenShots,
                    PracticeConstants.TipMaxTimeBetweenShots);

                float waitedTime = 0f;
                while (waitedTime < waitTime && ShouldRun(rinkIndex, RinkStripMode.TipPractice))
                {
                    yield return new WaitForSeconds(0.5f);
                    waitedTime += 0.5f;
                    CheckAndCleanupPucks(rinkIndex, isRedGoal, targetGoalPos);
                }
            }

            tipLoops.Remove(rinkIndex);
            CleanupRinkPucks(rinkIndex);
        }

        private static Vector3 ClampToRinkLocal(Vector3 worldPos, RinkSlot slot)
        {
            Vector3 local = worldPos - slot.Origin;
            local.x = Mathf.Clamp(local.x, RinkXMin, RinkXMax);
            local.z = Mathf.Clamp(local.z, RinkZMin, RinkZMax);
            return slot.Origin + local;
        }

        private static bool TryGetRinkSlot(int rinkIndex, out RinkSlot slot)
        {
            slot = null;
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || rinkIndex < 0 || rinkIndex >= cfg.Rinks.Count)
                return false;
            slot = cfg.Rinks[rinkIndex];
            return slot != null;
        }

        private static bool TryGetNetWorld(RinkSlot slot, PlayerTeam team, out Vector3 netPos)
        {
            netPos = default;
            if (slot == null) return false;
            if (!PracticeGoalieSpawn.TryGetGoaliePose(slot, team, out Vector3 crease, out _))
                return false;

            netPos = crease;
            if (team == PlayerTeam.Red)
                netPos.z -= NetBehindCrease;
            else
                netPos.z += NetBehindCrease;

            netPos.y = slot.Origin.y + VanillaRinkCloner.IceSurfaceY + 0.05f;
            return true;
        }

        private static Player FindSkaterOnRink(int rinkIndex, RinkSlot slot)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg == null || slot == null) return null;

            try
            {
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm == null) return null;

                Player best = null;
                float bestDist = float.MaxValue;
                Vector3 center = slot.Origin;

                foreach (Player player in pm.GetPlayers())
                {
                    if (player == null || player.IsReplay.Value) continue;
                    if (player.Role == PlayerRole.Goalie) continue;
                    if (player.PlayerBody == null) continue;
                    if (FakePlayerDetector.IsMaxPracticeFakePlayer(player)) continue;
                    if (!IsPlayerOnRink(player, slot, cfg, rinkIndex)) continue;

                    float dist = Vector3.SqrMagnitude(player.PlayerBody.transform.position - center);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = player;
                    }
                }

                return best;
            }
            catch { return null; }
        }

        private static bool IsPlayerOnRink(Player player, RinkSlot slot, MultiRinkConfig cfg, int rinkIndex)
        {
            string assigned = MultiRinkService.GetActiveRinkId(player.OwnerClientId);
            if (assigned != null && assigned == slot.Id) return true;

            int nearest = RinkLocator.NearestRink(cfg, player.PlayerBody.transform.position);
            return nearest == rinkIndex;
        }

        private static void TrackPuck(int rinkIndex, Puck puck)
        {
            if (!rinkPucks.TryGetValue(rinkIndex, out List<Puck> list))
            {
                list = new List<Puck>();
                rinkPucks[rinkIndex] = list;
            }
            list.Add(puck);
        }

        private static void TrackGoaliePuck(int rinkIndex, Puck puck, float fireAt, float travelTime)
        {
            TrackPuck(rinkIndex, puck);
            if (puck == null)
                return;

            float expectedArrival = fireAt + travelTime;
            goaliePuckSafetyExpireAt[puck] = expectedArrival + GoalieMissedShotSafetySeconds;
            goaliePuckCrossedGoalAt.Remove(puck);
        }

        private static void CleanupRinkPucks(int rinkIndex)
        {
            if (!rinkPucks.TryGetValue(rinkIndex, out List<Puck> list))
                return;

            for (int i = 0; i < list.Count; i++)
            {
                Puck p = list[i];
                if (p != null)
                {
                    goaliePuckSafetyExpireAt.Remove(p);
                    goaliePuckCrossedGoalAt.Remove(p);
                }
                if (p != null && p.gameObject != null)
                {
                    try { UnityEngine.Object.Destroy(p.gameObject); }
                    catch { }
                }
            }

            rinkPucks.Remove(rinkIndex);
        }

        private static void CheckAndCleanupGoaliePucks(int rinkIndex, bool isShootingAtRedGoal, Vector3 targetGoalPos)
        {
            if (!rinkPucks.TryGetValue(rinkIndex, out List<Puck> puckList))
                return;

            float now = Time.time;
            for (int i = puckList.Count - 1; i >= 0; i--)
            {
                Puck p = puckList[i];
                if (p == null || p.gameObject == null)
                {
                    puckList.RemoveAt(i);
                    continue;
                }

                try
                {
                    Vector3 puckPos = p.transform.position;
                    bool puckPastGoalLine = isShootingAtRedGoal
                        ? puckPos.z < targetGoalPos.z - GoaliePastGoalDespawnMargin
                        : puckPos.z > targetGoalPos.z + GoaliePastGoalDespawnMargin;

                    if (puckPastGoalLine)
                    {
                        if (!goaliePuckCrossedGoalAt.TryGetValue(p, out float crossedAt))
                        {
                            goaliePuckCrossedGoalAt[p] = now;
                            continue;
                        }

                        if (now >= crossedAt + GoaliePostGoalDespawnGrace)
                            DestroyGoaliePuck(p, puckList, i);
                        continue;
                    }

                    goaliePuckCrossedGoalAt.Remove(p);

                    if (goaliePuckSafetyExpireAt.TryGetValue(p, out float safetyExpireAt)
                        && now >= safetyExpireAt)
                    {
                        DestroyGoaliePuck(p, puckList, i);
                    }
                }
                catch { puckList.RemoveAt(i); }
            }

            while (puckList.Count > GoalieMaxFlyingPucks)
            {
                Puck oldest = puckList[0];
                if (oldest != null)
                    DestroyGoaliePuck(oldest, puckList, 0);
                else
                    puckList.RemoveAt(0);
            }
        }

        private static void DestroyGoaliePuck(Puck puck, List<Puck> puckList, int index)
        {
            if (puck != null)
            {
                goaliePuckSafetyExpireAt.Remove(puck);
                goaliePuckCrossedGoalAt.Remove(puck);
            }
            if (puck != null && puck.gameObject != null)
            {
                try { UnityEngine.Object.Destroy(puck.gameObject); }
                catch { }
            }
            if (index >= 0 && index < puckList.Count)
                puckList.RemoveAt(index);
        }

        private static void CheckAndCleanupPucks(int rinkIndex, bool isShootingAtRedGoal, Vector3 targetGoalPos)
        {
            if (!rinkPucks.TryGetValue(rinkIndex, out List<Puck> puckList))
                return;

            for (int i = puckList.Count - 1; i >= 0; i--)
            {
                Puck p = puckList[i];
                if (p == null || p.gameObject == null)
                {
                    puckList.RemoveAt(i);
                    continue;
                }

                try
                {
                    Vector3 puckPos = p.transform.position;
                    bool puckPastGoalLine = isShootingAtRedGoal
                        ? puckPos.z < targetGoalPos.z - 2f
                        : puckPos.z > targetGoalPos.z + 2f;

                    if (puckPastGoalLine)
                    {
                        UnityEngine.Object.Destroy(p.gameObject);
                        puckList.RemoveAt(i);
                        continue;
                    }

                    if (p.Rigidbody != null
                        && p.Rigidbody.linearVelocity.magnitude < PracticeConstants.SettledPuckVelocity)
                    {
                        UnityEngine.Object.Destroy(p.gameObject);
                        puckList.RemoveAt(i);
                    }
                }
                catch { puckList.RemoveAt(i); }
            }

            while (puckList.Count > 6)
            {
                if (puckList[0] != null)
                    UnityEngine.Object.Destroy(puckList[0].gameObject);
                puckList.RemoveAt(0);
            }
        }

        /// <summary>Uniform net target — low, mid, and high slots equally likely.</summary>
        private static Vector3 RandomNetTargetOffset()
        {
            return new Vector3(
                UnityEngine.Random.Range(-NetHalfWidth, NetHalfWidth),
                UnityEngine.Random.Range(0.28f, NetHeight + 0.06f),
                0f);
        }
    }
}
