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
    /// Runs until the rink strip mode changes or tools are cleared â€” no 2-minute timer.
    /// Multiple rinks may run Goalie or Tip practice independently (separate loops, queues, pucks).
    /// </summary>
    internal static class RinkPracticeDrills
    {
        private const float NetBehindCrease = 1.85f;
        // Match VanillaRinkCloner ice box (45 x 91.5) with a small inward margin so
        // practice pucks never spawn outside the boards / rounded corners.
        private const float IceHalfWidth = 22.5f;
        private const float IceHalfLength = 45.75f;
        private const float IceClampMargin = 0.85f;
        private const float IceCornerRadius = 7.5f;

        // Goalie strip mode â€” wide release-speed spread; arrival cadence fixed at the net.
        private const float GoalieFastShotChance = 0.55f;
        /// <summary>Fixed cadence at the net â€” releases are scheduled backwards from this interval.</summary>
        private const float GoalieSaveInterval = 2f;
        /// <summary>Pucks sit at the spawn point this long before the first release.</summary>
        private const float GoalieQueueVisibleLeadTime = 2.75f;
        /// <summary>Keep eyes on the in-flight shot until travel completes — prevents early glance at the next holder.</summary>
        private const float GoalieLookHoldGraceSeconds = 0.2f;
        /// <summary>Queued holders when room remains under the total puck cap.</summary>
        private const int GoalieHoldQueueDepth = 2;
        /// <summary>Hard cap: queued + in-flight practice pucks on a rink.</summary>
        private const int GoalieMaxTotalPucks = 3;
        private const float GoaliePastGoalDespawnMargin = 0.35f;
        /// <summary>Visible time in the net area after crossing the goal plane.</summary>
        private const float GoaliePostGoalDespawnGrace = 0.4f;
        /// <summary>Fallback despawn for shots that miss the net entirely.</summary>
        private const float GoalieMissedShotSafetySeconds = 2f;
        private const int GoalieMaxFlyingPucks = 3;
        private const float GoalieShotMinDist = 8f;
        /// <summary>Most of the sheet in front of the defended net (~94-length rink).</summary>
        private const float GoalieShotMaxDist = 52f;
        /// <summary>Tip practice: the tipper stands in front of the net â€” close spawns feel on top of them.</summary>
        private const float TipShotMinDist = 16f;
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

        private struct PendingTipShot
        {
            internal Puck Puck;
            internal Vector3 SpawnPos;
            internal Vector3 TipTarget;
            internal Vector3 LaunchVelocity;
            internal float TravelTime;
            internal float TipArrivalAt;
            internal float FireAt;
            internal GoalieShotPhysics.TipFeedKind FeedKind;
        }

        /// <summary>Tip cadence multiplier â€” 15% faster than MaxPractice defaults.</summary>
        private const float TipPaceScale = 1f / 1.15f;
        private const int TipHoldQueueDepth = 3;
        /// <summary>Queued + in-flight â€” three pucks cycle 1â†’2â†’3â†’1.</summary>
        private const int TipMaxTotalPucks = 3;
        private const int TipMaxFlyingPucks = 2;
        private const float TipQueueVisibleLeadTime = 1.35f;
        private const float TipMissedShotSafetySeconds = 3.5f;
        private const float TipHighArcSafetySeconds = 5.5f;
        private const float TipPostArrivalDespawnGrace = 0.65f;
        private const float TipNetFeedDespawnGrace = 1.15f;
        /// <summary>Sim vs physics drift â€” high rainbow feeds need extra time before cleanup.</summary>
        private const float TipHighArcTravelBuffer = 1.4f;
        private const float TipNetFeedTravelBuffer = 1.25f;
        /// <summary>Hard cap on feed spawn distance from the tipper (along-ice).</summary>
        private const float TipMaxSpawnBack = 28f;
        private const float RinkPuckClearMargin = 4f;

        private static readonly Dictionary<int, Coroutine> saveLoops = new Dictionary<int, Coroutine>();
        private static readonly Dictionary<int, Coroutine> tipLoops = new Dictionary<int, Coroutine>();
        private static readonly Dictionary<int, List<Puck>> rinkPucks = new Dictionary<int, List<Puck>>();
        private static readonly Dictionary<int, List<PendingGoalieShot>> goaliePendingShots =
            new Dictionary<int, List<PendingGoalieShot>>();
        private static readonly Dictionary<int, List<PendingTipShot>> tipPendingShots =
            new Dictionary<int, List<PendingTipShot>>();
        /// <summary>Next planned goal-plane arrival (server time) per rink â€” drives dynamic release times.</summary>
        private static readonly Dictionary<int, float> goalieNextGoalArrival =
            new Dictionary<int, float>();
        /// <summary>Next planned tipper-arrival (server time) per rink â€” tip shoot/reload cadence.</summary>
        private static readonly Dictionary<int, float> tipNextTipArrival =
            new Dictionary<int, float>();
        private static readonly Dictionary<Puck, float> goaliePuckSafetyExpireAt = new Dictionary<Puck, float>();
        private static readonly Dictionary<Puck, float> goaliePuckCrossedGoalAt = new Dictionary<Puck, float>();
        /// <summary>Planned net arrival â€” used to drop saved/settled pucks from track-look.</summary>
        private static readonly Dictionary<Puck, float> goaliePuckExpectedArrivalAt = new Dictionary<Puck, float>();
        private static readonly Dictionary<Puck, float> tipPuckSafetyExpireAt = new Dictionary<Puck, float>();
        private static readonly Dictionary<Puck, float> tipPuckArriveAt = new Dictionary<Puck, float>();
        private static readonly Dictionary<Puck, GoalieShotPhysics.TipFeedKind> tipPuckFeedKind =
            new Dictionary<Puck, GoalieShotPhysics.TipFeedKind>();
        /// <summary>Per-rink look lock for goalie/tip practice — hold active puck until flight ends.</summary>
        private static readonly Dictionary<int, float> goalieLookHoldUntil =
            new Dictionary<int, float>();

        internal static void ApplyMode(int rinkIndex, RinkStripMode mode)
        {
            StopRink(rinkIndex);

            if (ShouldClearPucksOnModeEntry(mode))
                ClearLoosePucksOnRink(rinkIndex);

            // Tip practice is a clone of goalie practice (same shot loop at the blue net);
            // the skater standing in front tips the incoming feeds.
            if (mode == RinkStripMode.GoaliePractice || mode == RinkStripMode.TipPractice)
                StartSaveLoop(rinkIndex, mode);
            else
                StretchPassPractice.Apply(rinkIndex, mode);
        }

        private static bool ShouldClearPucksOnModeEntry(RinkStripMode mode)
        {
            return mode == RinkStripMode.GoaliePractice
                || mode == RinkStripMode.TipPractice
                || mode == RinkStripMode.StretchPassing
                || mode == RinkStripMode.PointPassing
                || mode == RinkStripMode.LowCyclePassing
                || mode == RinkStripMode.PuckChasers;
        }

        /// <summary>Remove every puck on a sheet before practice modes start (no keep-closest).</summary>
        internal static void ClearLoosePucksOnRink(int rinkIndex)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || rinkIndex < 0 || rinkIndex >= cfg.Rinks.Count)
                return;

            RinkSlot slot = cfg.Rinks[rinkIndex];
            if (slot == null)
                return;

            try
            {
                PuckManager pm = MonoBehaviourSingleton<PuckManager>.Instance;

                for (int pass = 0; pass < 2; pass++)
                {
                    List<Puck> onRink = CollectAllPucksOnRink(slot, pm);
                    if (onRink.Count == 0)
                        break;

                    for (int i = 0; i < onRink.Count; i++)
                        ForceDestroyLoosePuck(pm, onRink[i]);
                }
            }
            catch (Exception ex)
            {
                PracticeLog.Info("[PHLPractice] Rink puck clear failed: " + ex.Message);
            }
        }

        private static List<Puck> CollectAllPucksOnRink(RinkSlot slot, PuckManager pm)
        {
            var found = new HashSet<Puck>();
            var list = new List<Puck>();

            if (pm != null)
            {
                List<Puck> managed = null;
                try { managed = pm.GetPucks(true); }
                catch { }

                if (managed != null)
                {
                    for (int i = 0; i < managed.Count; i++)
                        TryAddPuckOnRink(managed[i], slot, found, list);
                }
            }

            Puck[] scenePucks = UnityEngine.Object.FindObjectsByType<Puck>(FindObjectsSortMode.None);
            for (int i = 0; i < scenePucks.Length; i++)
                TryAddPuckOnRink(scenePucks[i], slot, found, list);

            return list;
        }

        private static void TryAddPuckOnRink(Puck puck, RinkSlot slot, HashSet<Puck> found, List<Puck> list)
        {
            if (puck == null || puck.gameObject == null || found.Contains(puck))
                return;
            if (!IsWorldOnRink(puck.transform.position, slot))
                return;

            found.Add(puck);
            list.Add(puck);
        }

        private static void ForceDestroyLoosePuck(PuckManager pm, Puck puck)
        {
            if (puck == null || puck.gameObject == null)
                return;

            CustomLevelPlugin.ProtectedPucks.Remove(puck);

            try
            {
                if (puck.Rigidbody != null)
                {
                    puck.Rigidbody.isKinematic = false;
                    puck.Rigidbody.linearVelocity = Vector3.zero;
                    puck.Rigidbody.angularVelocity = Vector3.zero;
                }
            }
            catch { }

            if (pm != null)
            {
                try { pm.Server_DespawnPuck(puck); }
                catch { }
            }

            if (puck != null && puck.gameObject != null)
            {
                try { UnityEngine.Object.Destroy(puck.gameObject); }
                catch { }
            }
        }

        private static bool IsWorldOnRink(Vector3 worldPos, RinkSlot slot)
        {
            Vector3 local = worldPos - slot.Origin;
            return Mathf.Abs(local.x) <= IceHalfWidth + RinkPuckClearMargin
                   && Mathf.Abs(local.z) <= IceHalfLength + RinkPuckClearMargin;
        }

        private const float GoalieLookMinClosingSpeed = 0.75f;
        /// <summary>After planned arrival, saved pucks in the crease stop winning track-look.</summary>
        private const float GoaliePostArrivalLookHandoff = 0.2f;

        /// <summary>
        /// Preferred track-look puck for goalie/tip practice: threatening in-flight shot first, else next queued.
        /// </summary>
        internal static Puck ResolveLookPuck(int rinkIndex)
        {
            if (rinkIndex < 0)
                return null;

            RinkStripMode mode = RinkStripVote.GetServerMode(rinkIndex);
            if (mode == RinkStripMode.StretchPassing || mode == RinkStripMode.PointPassing
                || mode == RinkStripMode.LowCyclePassing)
                return StretchPassPractice.ResolveLookPuck(rinkIndex);

            if (IsGoalieLookHoldActive(rinkIndex))
            {
                Puck heldFlying = ResolveHeldFlyingPuck(rinkIndex);
                if (heldFlying != null)
                    return heldFlying;
            }

            Puck flying = ResolveBestFlyingLookPuck(rinkIndex);
            if (flying != null)
                return flying;

            return ResolveQueuedLookPuck(rinkIndex);
        }

        /// <summary>Next held/queued practice puck (kinematic until release).</summary>
        internal static Puck ResolveQueuedLookPuck(int rinkIndex)
        {
            if (rinkIndex < 0)
                return null;

            RinkStripMode mode = RinkStripVote.GetServerMode(rinkIndex);
            if (mode == RinkStripMode.StretchPassing || mode == RinkStripMode.PointPassing
                || mode == RinkStripMode.LowCyclePassing)
                return StretchPassPractice.ResolveQueuedLookPuck(rinkIndex);

            Puck flying = IsGoalieLookHoldActive(rinkIndex)
                ? ResolveHeldFlyingPuck(rinkIndex)
                : ResolveBestFlyingLookPuck(rinkIndex);

            if (flying != null && IsGoalieLookHoldActive(rinkIndex))
            {
                if (goaliePendingShots.TryGetValue(rinkIndex, out List<PendingGoalieShot> heldQueue)
                    && heldQueue != null
                    && heldQueue.Count > 0
                    && heldQueue[0].Puck != null
                    && heldQueue[0].Puck.gameObject != null)
                {
                    return heldQueue[0].Puck;
                }

                return null;
            }

            if (flying != null)
                return flying;

            if ((mode == RinkStripMode.GoaliePractice || mode == RinkStripMode.TipPractice)
                && goaliePendingShots.TryGetValue(rinkIndex, out List<PendingGoalieShot> queue)
                && queue != null
                && queue.Count > 0
                && queue[0].Puck != null
                && queue[0].Puck.gameObject != null)
            {
                return queue[0].Puck;
            }

            return null;
        }

        private static bool IsGoalieLookHoldActive(int rinkIndex)
        {
            return goalieLookHoldUntil.TryGetValue(rinkIndex, out float holdUntil)
                && Time.time < holdUntil;
        }

        /// <summary>Current in-flight practice puck — no threat filter while look-hold is active.</summary>
        private static Puck ResolveHeldFlyingPuck(int rinkIndex)
        {
            if (!rinkPucks.TryGetValue(rinkIndex, out List<Puck> list) || list == null)
                return null;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                Puck p = list[i];
                if (p != null && p.gameObject != null)
                    return p;
            }

            return null;
        }

        private static Puck ResolveBestFlyingLookPuck(int rinkIndex)
        {
            if (!rinkPucks.TryGetValue(rinkIndex, out List<Puck> flying) || flying == null || flying.Count == 0)
                return null;

            Vector3 aimPos = default;
            bool haveAim = false;
            if (TryGetRinkSlot(rinkIndex, out RinkSlot slot))
                haveAim = TryGetNetWorld(slot, PlayerTeam.Blue, out aimPos);

            Puck best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < flying.Count; i++)
            {
                Puck puck = flying[i];
                if (puck == null || puck.gameObject == null || puck.transform == null)
                    continue;

                // After the puck has crossed the goal plane, stop forcing look at it.
                if (goaliePuckCrossedGoalAt.ContainsKey(puck))
                    continue;

                Vector3 puckPos = puck.transform.position;
                // Past the defended net = behind the goalie â€” hand look off to the queue.
                if (haveAim
                    && IsPuckPastDefendedGoal(puckPos, aimPos, isShootingAtRedGoal: false))
                    continue;

                Vector3 toAim = haveAim ? aimPos - puckPos : -puckPos;
                float dist = toAim.magnitude;
                if (dist < 0.05f)
                    dist = 0.05f;

                Vector3 vel = puck.Rigidbody != null ? puck.Rigidbody.linearVelocity : Vector3.zero;
                float closing = Vector3.Dot(vel, toAim / dist);

                // Saved / rebounding pucks in the crease â€” hand look to the next queued holder.
                if (closing < GoalieLookMinClosingSpeed)
                    continue;

                if (goaliePuckExpectedArrivalAt.TryGetValue(puck, out float goalieArriveAt)
                    && Time.time >= goalieArriveAt + GoaliePostArrivalLookHandoff)
                {
                    float speed = vel.magnitude;
                    if (speed < PracticeConstants.SettledPuckVelocity)
                        continue;
                }

                // Prefer approaching shots; keep a distance bias so the soonest threat wins ties.
                float score = closing * 8f - dist * 0.15f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = puck;
                }
            }

            return best;
        }

        private static bool IsPuckPastDefendedGoal(Vector3 puckPos, Vector3 goalPos, bool isShootingAtRedGoal)
        {
            if (isShootingAtRedGoal)
                return puckPos.z < goalPos.z - 0.1f;
            return puckPos.z > goalPos.z + 0.1f;
        }

        private static int CountGoaliePracticePucks(int rinkIndex)
        {
            int count = 0;
            if (goaliePendingShots.TryGetValue(rinkIndex, out List<PendingGoalieShot> queue) && queue != null)
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    Puck p = queue[i].Puck;
                    if (p != null && p.gameObject != null)
                        count++;
                }
            }

            if (rinkPucks.TryGetValue(rinkIndex, out List<Puck> flying) && flying != null)
            {
                for (int i = 0; i < flying.Count; i++)
                {
                    Puck p = flying[i];
                    if (p != null && p.gameObject != null)
                        count++;
                }
            }

            return count;
        }

        private static void RefreshLookTarget(int rinkIndex)
        {
            GoaliePracticeLookTarget.Publish(
                rinkIndex,
                ResolveLookPuck(rinkIndex),
                ResolveQueuedLookPuck(rinkIndex));
        }

        internal static void StopAll()
        {
            var indices = new HashSet<int>();
            foreach (int k in saveLoops.Keys) indices.Add(k);
            foreach (int k in tipLoops.Keys) indices.Add(k);
            foreach (int k in rinkPucks.Keys) indices.Add(k);
            foreach (int k in indices)
                StopRink(k);
            StretchPassPractice.StopAll();
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
                bool wantSaveLoop = mode == RinkStripMode.GoaliePractice
                    || mode == RinkStripMode.TipPractice;

                if (!wantSaveLoop && saveLoops.ContainsKey(i))
                    StopRink(i);
                else if (tipLoops.ContainsKey(i))
                    StopRink(i); // legacy tip loop â€” replaced by the goalie-clone save loop

                if (wantSaveLoop && !saveLoops.ContainsKey(i))
                    StartSaveLoop(i, mode);
            }

            StretchPassPractice.TickReconcile();
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
            tipNextTipArrival.Remove(rinkIndex);
            goalieLookHoldUntil.Remove(rinkIndex);
            CleanupPendingGoalieShots(rinkIndex);
            CleanupPendingTipShots(rinkIndex);
            CleanupRinkPucks(rinkIndex);
            StretchPassPractice.Stop(rinkIndex);
            GoaliePracticeLookTarget.ClearRink(rinkIndex);
        }

        private static void StartSaveLoop(int rinkIndex, RinkStripMode stripMode)
        {
            MonoBehaviour host = CoroutineHost;
            if (host == null) return;
            if (saveLoops.ContainsKey(rinkIndex)) return;

            Coroutine loop = host.StartCoroutine(SavePracticeLoop(rinkIndex, stripMode));
            saveLoops[rinkIndex] = loop;
            PracticeLog.Info("[PHLPractice] Rink " + (rinkIndex + 1)
                + (stripMode == RinkStripMode.TipPractice
                    ? " tip practice (goalie-clone) started (persistent)."
                    : " save practice started (persistent)."));
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

        private static IEnumerator SavePracticeLoop(int rinkIndex, RinkStripMode stripMode)
        {
            const bool isShootingAtRedGoal = false;

            try
            {
                while (ShouldRun(rinkIndex, stripMode))
                {
                    if (!TryGetRinkSlot(rinkIndex, out RinkSlot slot)
                        || !TryGetNetWorld(slot, PlayerTeam.Blue, out Vector3 targetGoalPos))
                    {
                        yield return new WaitForSeconds(1f);
                        continue;
                    }

                    List<PendingGoalieShot> queue = GetGoaliePendingList(rinkIndex);
                    PruneDeadPendingShots(queue);
                    CheckAndCleanupGoaliePucks(rinkIndex, isShootingAtRedGoal, targetGoalPos);

                    while (queue.Count < GoalieHoldQueueDepth
                           && CountGoaliePracticePucks(rinkIndex) < GoalieMaxTotalPucks
                           && ShouldRun(rinkIndex, stripMode))
                    {
                        if (!TryEnqueueGoalieShot(rinkIndex, slot, targetGoalPos, queue, stripMode))
                            break;
                    }

                    // Cap full of flying pucks + empty queue used to spin forever because
                    // cleanup never ran on this path â€” force room, then retry enqueue.
                    if (queue.Count == 0)
                    {
                        CheckAndCleanupGoaliePucks(rinkIndex, isShootingAtRedGoal, targetGoalPos);
                        if (CountGoaliePracticePucks(rinkIndex) >= GoalieMaxTotalPucks)
                            ForceDestroyOldestFlyingPuck(rinkIndex);

                        if (CountGoaliePracticePucks(rinkIndex) < GoalieMaxTotalPucks)
                            TryEnqueueGoalieShot(rinkIndex, slot, targetGoalPos, queue, stripMode);

                        if (queue.Count == 0)
                        {
                            yield return new WaitForSeconds(0.35f);
                            continue;
                        }
                    }

                    float releaseAt = queue[0].FireAt;
                    // Don't park on a far-future cadence slot (short travel after a long one).
                    if (releaseAt > Time.time + 4.5f)
                    {
                        PendingGoalieShot adjusted = queue[0];
                        adjusted.FireAt = Time.time + 0.2f;
                        adjusted.GoalArrivalAt = adjusted.FireAt + adjusted.TravelTime;
                        queue[0] = adjusted;
                        releaseAt = adjusted.FireAt;
                        goalieNextGoalArrival[rinkIndex] = adjusted.GoalArrivalAt + GoalieSaveInterval;
                    }

                    while (Time.time < releaseAt && ShouldRun(rinkIndex, stripMode))
                    {
                        yield return new WaitForSeconds(0.05f);
                        CheckAndCleanupGoaliePucks(rinkIndex, isShootingAtRedGoal, targetGoalPos);
                        RefreshLookTarget(rinkIndex);
                    }

                    if (!ShouldRun(rinkIndex, stripMode))
                        break;

                    PruneDeadPendingShots(queue);
                    if (queue.Count == 0)
                        continue;

                    PendingGoalieShot shot = queue[0];
                    queue.RemoveAt(0);

                    if (shot.Puck == null || shot.Puck.gameObject == null)
                    {
                        RefreshLookTarget(rinkIndex);
                        continue;
                    }

                    FireGoalieShot(shot);
                    float firedAt = Time.time;
                    TrackGoaliePuck(rinkIndex, shot.Puck, firedAt, shot.TravelTime);
                    goalieLookHoldUntil[rinkIndex] = firedAt + shot.TravelTime + GoalieLookHoldGraceSeconds;
                    RefreshLookTarget(rinkIndex);

                    if (queue.Count < GoalieHoldQueueDepth
                        && CountGoaliePracticePucks(rinkIndex) < GoalieMaxTotalPucks
                        && ShouldRun(rinkIndex, stripMode))
                        TryEnqueueGoalieShot(rinkIndex, slot, targetGoalPos, queue, stripMode);

                    CheckAndCleanupGoaliePucks(rinkIndex, isShootingAtRedGoal, targetGoalPos);
                    RefreshLookTarget(rinkIndex);
                }
            }
            finally
            {
                saveLoops.Remove(rinkIndex);
                goalieNextGoalArrival.Remove(rinkIndex);
                goalieLookHoldUntil.Remove(rinkIndex);
                CleanupPendingGoalieShots(rinkIndex);
                CleanupRinkPucks(rinkIndex);
                GoaliePracticeLookTarget.ClearRink(rinkIndex);
            }
        }

        private static void PruneDeadPendingShots(List<PendingGoalieShot> queue)
        {
            if (queue == null)
                return;

            for (int i = queue.Count - 1; i >= 0; i--)
            {
                Puck p = queue[i].Puck;
                if (p == null || p.gameObject == null)
                    queue.RemoveAt(i);
            }
        }

        private static void ForceDestroyOldestFlyingPuck(int rinkIndex)
        {
            if (!rinkPucks.TryGetValue(rinkIndex, out List<Puck> puckList) || puckList == null)
                return;

            while (puckList.Count > 0)
            {
                Puck oldest = puckList[0];
                if (oldest == null || oldest.gameObject == null)
                {
                    puckList.RemoveAt(0);
                    continue;
                }

                DestroyGoaliePuck(oldest, puckList, 0);
                break;
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
            float minDist,
            out Vector3 spawnPos,
            out Vector3 aimPoint,
            out bool isFastShot)
        {
            isFastShot = UnityEngine.Random.value < GoalieFastShotChance;
            float iceY = targetGoalPos.y;
            bool blueEndGoal = targetGoalPos.z >= slot.Origin.z;
            Vector3 attackDir = blueEndGoal ? Vector3.back : Vector3.forward;
            float goalLineZ = targetGoalPos.z;

            spawnPos = BuildFallbackGoalieSpawn(slot, targetGoalPos, attackDir, iceY, minDist);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                float dist = UnityEngine.Random.Range(minDist, GoalieShotMaxDist);
                float yaw = UnityEngine.Random.Range(-75f, 75f);
                Vector3 offset = Quaternion.Euler(0f, yaw, 0f) * attackDir * dist;
                Vector3 candidate = targetGoalPos + offset;
                candidate.y = iceY;
                candidate = ClampToRinkLocal(candidate, slot);

                if (!IsGoalieSpawnInFrontOfNet(candidate, goalLineZ, blueEndGoal))
                    continue;

                Vector3 netFlat = new Vector3(targetGoalPos.x, 0f, targetGoalPos.z);
                Vector3 spawnFlat = new Vector3(candidate.x, 0f, candidate.z);
                float actualDist = Vector3.Distance(spawnFlat, netFlat);
                if (actualDist < minDist * 0.85f || actualDist > GoalieShotMaxDist * 1.05f)
                    continue;

                spawnPos = candidate;
                break;
            }

            spawnPos = ClampToRinkLocal(spawnPos, slot);
            spawnPos.y = iceY;
            aimPoint = targetGoalPos + RandomNetTargetOffset();
        }

        private static Vector3 BuildFallbackGoalieSpawn(
            RinkSlot slot, Vector3 targetGoalPos, Vector3 attackDir, float iceY, float minDist)
        {
            float dist = UnityEngine.Random.Range(minDist, Mathf.Max(GoalieShotMaxDist * 0.65f, minDist + 4f));
            Vector3 spawn = targetGoalPos + attackDir * dist;
            spawn.y = iceY;
            spawn = ClampToRinkLocal(spawn, slot);
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
            List<PendingGoalieShot> queue,
            RinkStripMode stripMode)
        {
            if (CountGoaliePracticePucks(rinkIndex) >= GoalieMaxTotalPucks)
                return false;

            float minDist = stripMode == RinkStripMode.TipPractice ? TipShotMinDist : GoalieShotMinDist;
            PlanGoalieShot(slot, targetGoalPos, minDist, out Vector3 spawnPos, out Vector3 aimPoint, out bool isFastShot);

            Puck puck = PracticePuckSpawn.SpawnAt(spawnPos, Quaternion.identity, Vector3.zero);
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
            // Long travel into a near arrival slot: fire ASAP and push arrival to match reality
            // so the next cadence slot isn't scheduled before this puck can reach the net.
            if (fireAt < Time.time + 0.15f)
            {
                fireAt = Time.time + 0.15f;
                arrivalAt = fireAt + travelTime;
            }

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
            RefreshLookTarget(rinkIndex);
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
            try
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

                    if (!TryGetNetWorld(slot, PlayerTeam.Blue, out Vector3 targetGoalPos))
                    {
                        yield return new WaitForSeconds(1f);
                        continue;
                    }

                    List<PendingTipShot> queue = GetTipPendingList(rinkIndex);
                    PruneDeadPendingTipShots(queue);
                    CheckAndCleanupTipPucks(rinkIndex);

                    while (queue.Count < TipHoldQueueDepth
                           && CountTipPracticePucks(rinkIndex) < TipMaxTotalPucks
                           && ShouldRun(rinkIndex, RinkStripMode.TipPractice))
                    {
                        tipper = FindSkaterOnRink(rinkIndex, slot);
                        if (tipper?.PlayerBody == null)
                            break;
                        if (!TryEnqueueTipShot(rinkIndex, slot, tipper, targetGoalPos, queue))
                            break;
                    }

                    if (queue.Count == 0)
                    {
                        CheckAndCleanupTipPucks(rinkIndex);
                        if (CountTipPracticePucks(rinkIndex) >= TipMaxTotalPucks)
                            ForceDestroyOldestFlyingTipPuck(rinkIndex);

                        tipper = FindSkaterOnRink(rinkIndex, slot);
                        if (tipper?.PlayerBody != null
                            && CountTipPracticePucks(rinkIndex) < TipMaxTotalPucks)
                        {
                            TryEnqueueTipShot(rinkIndex, slot, tipper, targetGoalPos, queue);
                        }

                        if (queue.Count == 0)
                        {
                            RefreshLookTarget(rinkIndex);
                            yield return new WaitForSeconds(0.35f);
                            continue;
                        }
                    }

                    float releaseAt = queue[0].FireAt;
                    if (releaseAt > Time.time + 4.5f)
                    {
                        PendingTipShot adjusted = queue[0];
                        adjusted.FireAt = Time.time + PracticeConstants.TipPuckRestTime * TipPaceScale;
                        adjusted.TipArrivalAt = adjusted.FireAt + adjusted.TravelTime;
                        queue[0] = adjusted;
                        releaseAt = adjusted.FireAt;
                        tipNextTipArrival[rinkIndex] = adjusted.TipArrivalAt
                            + UnityEngine.Random.Range(
                                PracticeConstants.TipMinTimeBetweenShots,
                                PracticeConstants.TipMaxTimeBetweenShots) * TipPaceScale;
                    }

                    while (Time.time < releaseAt && ShouldRun(rinkIndex, RinkStripMode.TipPractice))
                    {
                        yield return new WaitForSeconds(0.05f);
                        CheckAndCleanupTipPucks(rinkIndex);
                        RefreshLookTarget(rinkIndex);
                    }

                    if (!ShouldRun(rinkIndex, RinkStripMode.TipPractice))
                        break;

                    PruneDeadPendingTipShots(queue);
                    if (queue.Count == 0)
                        continue;

                    PendingTipShot shot = queue[0];
                    queue.RemoveAt(0);

                    if (shot.Puck == null || shot.Puck.gameObject == null)
                    {
                        RefreshLookTarget(rinkIndex);
                        continue;
                    }

                    FireTipShot(ref shot, slot, rinkIndex, targetGoalPos);
                    TrackTipPuck(rinkIndex, shot.Puck, shot.TravelTime, shot.FeedKind);
                    RefreshLookTarget(rinkIndex);

                    // Second puck already (or now) reloading while the first flies.
                    if (queue.Count < TipHoldQueueDepth
                        && CountTipPracticePucks(rinkIndex) < TipMaxTotalPucks
                        && ShouldRun(rinkIndex, RinkStripMode.TipPractice))
                    {
                        tipper = FindSkaterOnRink(rinkIndex, slot);
                        if (tipper?.PlayerBody != null)
                            TryEnqueueTipShot(rinkIndex, slot, tipper, targetGoalPos, queue);
                    }

                    CheckAndCleanupTipPucks(rinkIndex);
                    RefreshLookTarget(rinkIndex);
                }
            }
            finally
            {
                tipLoops.Remove(rinkIndex);
                tipNextTipArrival.Remove(rinkIndex);
                CleanupPendingTipShots(rinkIndex);
                CleanupRinkPucks(rinkIndex);
                GoaliePracticeLookTarget.ClearRink(rinkIndex);
            }
        }

        private static int CountTipPracticePucks(int rinkIndex)
        {
            int count = 0;
            if (tipPendingShots.TryGetValue(rinkIndex, out List<PendingTipShot> queue) && queue != null)
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    Puck p = queue[i].Puck;
                    if (p != null && p.gameObject != null)
                        count++;
                }
            }

            if (rinkPucks.TryGetValue(rinkIndex, out List<Puck> flying) && flying != null)
            {
                for (int i = 0; i < flying.Count; i++)
                {
                    Puck p = flying[i];
                    if (p != null && p.gameObject != null)
                        count++;
                }
            }

            return count;
        }

        private static List<PendingTipShot> GetTipPendingList(int rinkIndex)
        {
            if (!tipPendingShots.TryGetValue(rinkIndex, out List<PendingTipShot> list))
            {
                list = new List<PendingTipShot>(TipHoldQueueDepth);
                tipPendingShots[rinkIndex] = list;
            }
            return list;
        }

        private static void PruneDeadPendingTipShots(List<PendingTipShot> queue)
        {
            if (queue == null)
                return;

            for (int i = queue.Count - 1; i >= 0; i--)
            {
                Puck p = queue[i].Puck;
                if (p == null || p.gameObject == null)
                    queue.RemoveAt(i);
            }
        }

        private static void CleanupPendingTipShots(int rinkIndex)
        {
            if (!tipPendingShots.TryGetValue(rinkIndex, out List<PendingTipShot> list))
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

            tipPendingShots.Remove(rinkIndex);
        }

        private static bool TryEnqueueTipShot(
            int rinkIndex,
            RinkSlot slot,
            Player tipper,
            Vector3 targetGoalPos,
            List<PendingTipShot> queue)
        {
            if (tipper?.PlayerBody == null)
                return false;
            if (CountTipPracticePucks(rinkIndex) >= TipMaxTotalPucks)
                return false;

            Vector3 tipperPos = tipper.PlayerBody.transform.position;
            PlanTipShot(slot, tipper, tipperPos, targetGoalPos, out Vector3 spawnPos, out Vector3 tipTarget, out GoalieShotPhysics.TipFeedKind feedKind);

            Puck puck = PracticePuckSpawn.SpawnAt(spawnPos, Quaternion.identity, Vector3.zero);
            if (puck == null || puck.Rigidbody == null)
                return false;

            PrepareTipPuckAt(puck, spawnPos);

            bool shootFromPositiveZ = spawnPos.z > tipTarget.z;
            float crossingPlaneZ = ResolveTipCrossingPlaneZ(feedKind, tipperPos.z, targetGoalPos.z);
            if (!GoalieShotPhysics.TryBuildTipLaunch(
                    spawnPos,
                    tipTarget,
                    crossingPlaneZ,
                    shootFromPositiveZ,
                    puck,
                    feedKind,
                    out GoalieShotPhysics.GoalieShotLaunch launch))
            {
                try { UnityEngine.Object.Destroy(puck.gameObject); }
                catch { }
                return false;
            }

            float travelTime = Mathf.Max(launch.TravelTime, 0.12f);
            float minHold = PracticeConstants.TipPuckRestTime * TipPaceScale;
            float gap = UnityEngine.Random.Range(
                PracticeConstants.TipMinTimeBetweenShots,
                PracticeConstants.TipMaxTimeBetweenShots) * TipPaceScale;

            if (!tipNextTipArrival.TryGetValue(rinkIndex, out float nextArrival)
                || nextArrival <= Time.time)
            {
                nextArrival = Time.time + TipQueueVisibleLeadTime + travelTime;
            }

            float arrivalAt = nextArrival;
            float fireAt = arrivalAt - travelTime;
            if (fireAt < Time.time + minHold)
            {
                fireAt = Time.time + minHold;
                arrivalAt = fireAt + travelTime;
            }

            tipNextTipArrival[rinkIndex] = arrivalAt + gap;

            queue.Add(new PendingTipShot
            {
                Puck = puck,
                SpawnPos = spawnPos,
                TipTarget = tipTarget,
                LaunchVelocity = launch.Velocity,
                TravelTime = travelTime,
                TipArrivalAt = arrivalAt,
                FireAt = fireAt,
                FeedKind = feedKind,
            });
            RefreshLookTarget(rinkIndex);
            return true;
        }

        private static void FireTipShot(ref PendingTipShot shot, RinkSlot slot, int rinkIndex, Vector3 targetGoalPos)
        {
            if (shot.Puck == null || shot.Puck.Rigidbody == null)
                return;

            Vector3 tipTarget = shot.TipTarget;
            Vector3 launchVel = shot.LaunchVelocity;
            float travelTime = shot.TravelTime;
            GoalieShotPhysics.TipFeedKind feedKind = shot.FeedKind;

            Player tipper = FindSkaterOnRink(rinkIndex, slot);
            if (tipper?.PlayerBody != null)
            {
                Vector3 tipperPos = tipper.PlayerBody.transform.position;
                RetargetTipShot(slot, tipper, tipperPos, targetGoalPos, feedKind, ref tipTarget);
                bool shootFromPositiveZ = shot.SpawnPos.z > tipTarget.z;
                float crossingPlaneZ = ResolveTipCrossingPlaneZ(feedKind, tipperPos.z, targetGoalPos.z);
                if (GoalieShotPhysics.TryBuildTipLaunch(
                        shot.SpawnPos,
                        tipTarget,
                        crossingPlaneZ,
                        shootFromPositiveZ,
                        shot.Puck,
                        feedKind,
                        out GoalieShotPhysics.GoalieShotLaunch launch))
                {
                    launchVel = launch.Velocity;
                    travelTime = Mathf.Max(launch.TravelTime, 0.12f);
                }
            }

            shot.Puck.Rigidbody.isKinematic = false;
            shot.Puck.Rigidbody.linearVelocity = launchVel;
            shot.Puck.Rigidbody.angularVelocity = Vector3.zero;

            // Keep tracked travel accurate after retarget so cleanup/look handoff stay honest.
            shot.TravelTime = travelTime;
            shot.TipTarget = tipTarget;
            shot.LaunchVelocity = launchVel;
        }

        private static float ResolveTipCrossingPlaneZ(
            GoalieShotPhysics.TipFeedKind feedKind,
            float tipperPlaneZ,
            float netPlaneZ)
        {
            if (feedKind == GoalieShotPhysics.TipFeedKind.OnNet
                || feedKind == GoalieShotPhysics.TipFeedKind.HighLooperNet)
            {
                return netPlaneZ;
            }

            return tipperPlaneZ;
        }

        private static float ResolveTipTravelBuffer(GoalieShotPhysics.TipFeedKind feedKind)
        {
            if (feedKind == GoalieShotPhysics.TipFeedKind.HighLooperTipper)
                return TipHighArcTravelBuffer;
            if (feedKind == GoalieShotPhysics.TipFeedKind.HighLooperNet
                || feedKind == GoalieShotPhysics.TipFeedKind.OnNet)
            {
                return TipNetFeedTravelBuffer;
            }

            return 1.1f;
        }

        private static float ResolveTipSafetySeconds(GoalieShotPhysics.TipFeedKind feedKind)
        {
            if (feedKind == GoalieShotPhysics.TipFeedKind.HighLooperTipper
                || feedKind == GoalieShotPhysics.TipFeedKind.HighLooperNet)
            {
                return TipHighArcSafetySeconds;
            }

            return TipMissedShotSafetySeconds;
        }

        private static float ResolveTipDespawnGrace(GoalieShotPhysics.TipFeedKind feedKind)
        {
            if (feedKind == GoalieShotPhysics.TipFeedKind.OnNet
                || feedKind == GoalieShotPhysics.TipFeedKind.HighLooperNet)
            {
                return TipNetFeedDespawnGrace;
            }

            if (feedKind == GoalieShotPhysics.TipFeedKind.HighLooperTipper)
                return TipPostArrivalDespawnGrace + 0.35f;

            return TipPostArrivalDespawnGrace;
        }

        private static void TrackTipPuck(
            int rinkIndex,
            Puck puck,
            float travelTime,
            GoalieShotPhysics.TipFeedKind feedKind)
        {
            TrackPuck(rinkIndex, puck);
            if (puck == null)
                return;

            float bufferedTravel = Mathf.Max(travelTime, 0.15f) * ResolveTipTravelBuffer(feedKind);
            float expectedArrival = Time.time + bufferedTravel;
            tipPuckArriveAt[puck] = expectedArrival;
            tipPuckSafetyExpireAt[puck] = expectedArrival + ResolveTipSafetySeconds(feedKind);
            tipPuckFeedKind[puck] = feedKind;
        }

        private static void ForceDestroyOldestFlyingTipPuck(int rinkIndex)
        {
            if (!rinkPucks.TryGetValue(rinkIndex, out List<Puck> puckList) || puckList == null)
                return;

            float now = Time.time;
            if (TryDestroyOldestArrivedTipPuck(puckList, now))
                return;

            // Last resort â€” only cull when well past expected flight time.
            for (int i = 0; i < puckList.Count; i++)
            {
                Puck p = puckList[i];
                if (p == null || p.gameObject == null)
                    continue;
                if (tipPuckArriveAt.TryGetValue(p, out float arriveAt) && now < arriveAt + 1.5f)
                    continue;

                DestroyTipPuck(p, puckList, i);
                return;
            }
        }

        private static bool TryDestroyOldestArrivedTipPuck(List<Puck> puckList, float now)
        {
            for (int i = 0; i < puckList.Count; i++)
            {
                Puck p = puckList[i];
                if (p == null || p.gameObject == null)
                    continue;

                if (tipPuckSafetyExpireAt.TryGetValue(p, out float safetyAt) && now >= safetyAt)
                {
                    DestroyTipPuck(p, puckList, i);
                    return true;
                }

                GoalieShotPhysics.TipFeedKind feed = GoalieShotPhysics.TipFeedKind.AtTipper;
                tipPuckFeedKind.TryGetValue(p, out feed);
                if (tipPuckArriveAt.TryGetValue(p, out float arriveAt)
                    && now >= arriveAt + ResolveTipDespawnGrace(feed))
                {
                    DestroyTipPuck(p, puckList, i);
                    return true;
                }
            }

            return false;
        }

        private static void CheckAndCleanupTipPucks(int rinkIndex)
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
                    GoalieShotPhysics.TipFeedKind feed = GoalieShotPhysics.TipFeedKind.AtTipper;
                    tipPuckFeedKind.TryGetValue(p, out feed);
                    float despawnGrace = ResolveTipDespawnGrace(feed);

                    bool pastArrival = tipPuckArriveAt.TryGetValue(p, out float arriveAt)
                        && now >= arriveAt + despawnGrace;
                    bool safetyExpired = tipPuckSafetyExpireAt.TryGetValue(p, out float safetyExpireAt)
                        && now >= safetyExpireAt;
                    bool settled = pastArrival
                        && p.Rigidbody != null
                        && p.Rigidbody.linearVelocity.magnitude < PracticeConstants.SettledPuckVelocity;

                    if (safetyExpired || (pastArrival && settled))
                        DestroyTipPuck(p, puckList, i);
                }
                catch { puckList.RemoveAt(i); }
            }

            while (puckList.Count > TipMaxFlyingPucks
                   || CountTipPracticePucks(rinkIndex) > TipMaxTotalPucks)
            {
                if (!TryDestroyOldestArrivedTipPuck(puckList, now))
                    break;
            }
        }

        private static void DestroyTipPuck(Puck puck, List<Puck> puckList, int index)
        {
            if (puck != null)
            {
                tipPuckSafetyExpireAt.Remove(puck);
                tipPuckArriveAt.Remove(puck);
                tipPuckFeedKind.Remove(puck);
            }
            if (puck != null && puck.gameObject != null)
            {
                try { UnityEngine.Object.Destroy(puck.gameObject); }
                catch { }
            }
            if (index >= 0 && index < puckList.Count)
                puckList.RemoveAt(index);
        }

        private static void PlanTipShot(
            RinkSlot slot,
            Player tipper,
            Vector3 tipperPos,
            Vector3 targetGoalPos,
            out Vector3 spawnPos,
            out Vector3 tipTarget,
            out GoalieShotPhysics.TipFeedKind feedKind)
        {
            float iceY = slot.Origin.y + VanillaRinkCloner.IceSurfaceY + 0.05f;
            bool blueEndGoal = targetGoalPos.z >= slot.Origin.z;
            Vector3 awayFromNet = blueEndGoal ? Vector3.back : Vector3.forward;

            float roll = UnityEngine.Random.value;
            if (roll < 0.24f)
                feedKind = GoalieShotPhysics.TipFeedKind.LongStraight;
            else if (roll < 0.40f)
                feedKind = GoalieShotPhysics.TipFeedKind.AtTipper;
            else if (roll < 0.54f)
                feedKind = GoalieShotPhysics.TipFeedKind.WideTipper;
            else if (roll < 0.70f)
                feedKind = GoalieShotPhysics.TipFeedKind.HighLooperTipper;
            else if (roll < 0.85f)
                feedKind = GoalieShotPhysics.TipFeedKind.HighLooperNet;
            else
                feedKind = GoalieShotPhysics.TipFeedKind.OnNet;

            switch (feedKind)
            {
                case GoalieShotPhysics.TipFeedKind.LongStraight:
                {
                    float back = ClampTipSpawnBack(UnityEngine.Random.Range(12f, 26f));
                    spawnPos = tipperPos + awayFromNet * back
                        + Vector3.right * UnityEngine.Random.Range(-9f, 9f);
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: false);
                    break;
                }
                case GoalieShotPhysics.TipFeedKind.WideTipper:
                {
                    float back = ClampTipSpawnBack(UnityEngine.Random.Range(12f, 20f));
                    float side = UnityEngine.Random.Range(2.5f, 6.5f) * (UnityEngine.Random.value > 0.5f ? 1f : -1f);
                    spawnPos = tipperPos + awayFromNet * back
                        + Vector3.right * side * 0.45f;
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: false);
                    tipTarget.x = tipperPos.x + side + UnityEngine.Random.Range(-0.35f, 0.35f);
                    tipTarget.y = Mathf.Max(tipTarget.y, iceY + 0.55f);
                    break;
                }
                case GoalieShotPhysics.TipFeedKind.HighLooperTipper:
                {
                    float back = ClampTipSpawnBack(UnityEngine.Random.Range(14f, 24f));
                    spawnPos = tipperPos + awayFromNet * back
                        + Vector3.right * UnityEngine.Random.Range(-8f, 8f);
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: false);
                    tipTarget.y = Mathf.Max(tipTarget.y, iceY + UnityEngine.Random.Range(0.65f, 1.15f));
                    break;
                }
                case GoalieShotPhysics.TipFeedKind.HighLooperNet:
                {
                    float back = ClampTipSpawnBack(UnityEngine.Random.Range(14f, 26f));
                    spawnPos = tipperPos + awayFromNet * back
                        + Vector3.right * UnityEngine.Random.Range(-2.5f, 2.5f);
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: true);
                    tipTarget.y = Mathf.Max(tipTarget.y, iceY + UnityEngine.Random.Range(0.75f, 1.15f));
                    break;
                }
                case GoalieShotPhysics.TipFeedKind.OnNet:
                {
                    float back = ClampTipSpawnBack(UnityEngine.Random.Range(10f, 20f));
                    spawnPos = tipperPos + awayFromNet * back
                        + Vector3.right * UnityEngine.Random.Range(-2f, 2f);
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: true);
                    tipTarget.y = Mathf.Max(tipTarget.y, iceY + UnityEngine.Random.Range(0.55f, 0.95f));
                    break;
                }
                default: // AtTipper â€” medium feed through the slot
                {
                    float back = ClampTipSpawnBack(UnityEngine.Random.Range(10f, 18f));
                    spawnPos = tipperPos + awayFromNet * back
                        + Vector3.right * UnityEngine.Random.Range(-6f, 6f);
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: false);
                    break;
                }
            }

            spawnPos.y = iceY;
            spawnPos = ClampToRinkLocal(spawnPos, slot);
            spawnPos.y = iceY;

            tipTarget = ClampToRinkLocal(tipTarget, slot);
            tipTarget.y = Mathf.Max(tipTarget.y, iceY + 0.5f);
        }

        private static float ClampTipSpawnBack(float back)
        {
            return Mathf.Clamp(back, 8f, TipMaxSpawnBack);
        }

        private static Vector3 SampleNetTipTarget(Vector3 netCenter, float iceY, bool highArrival)
        {
            float y = highArrival
                ? iceY + UnityEngine.Random.Range(0.75f, 1.35f)
                : iceY + UnityEngine.Random.Range(0.55f, 1.15f);
            return new Vector3(
                netCenter.x + UnityEngine.Random.Range(-0.65f, 0.65f),
                y,
                netCenter.z);
        }

        /// <summary>Pick stick blade, chest, or net with modest variance.</summary>
        private static Vector3 SampleTipperAimPoint(
            Player tipper,
            Vector3 tipperPos,
            Vector3 netCenter,
            float iceY,
            bool allowNet)
        {
            float roll = UnityEngine.Random.value;
            if (allowNet && roll < 0.18f)
                return SampleNetTipTarget(netCenter, iceY, highArrival: roll < 0.09f);

            float aimRoll = UnityEngine.Random.value;
            Vector3 point;
            if (aimRoll < 0.42f)
                point = GetStickBladePosition(tipper, tipperPos, iceY);
            else if (aimRoll < 0.78f)
                point = GetBodyChestPosition(tipperPos, iceY);
            else
            {
                Vector3 stick = GetStickBladePosition(tipper, tipperPos, iceY);
                Vector3 body = GetBodyChestPosition(tipperPos, iceY);
                point = Vector3.Lerp(body, stick, UnityEngine.Random.Range(0.35f, 0.85f));
            }

            point.x += UnityEngine.Random.Range(-0.4f, 0.4f);
            point.y += UnityEngine.Random.Range(-0.1f, 0.2f);
            point.z += UnityEngine.Random.Range(-0.35f, 0.35f);
            point.y = Mathf.Max(point.y, iceY + 0.5f);
            return point;
        }

        private static Vector3 GetStickBladePosition(Player player, Vector3 fallbackBodyPos, float iceY)
        {
            try
            {
                if (player?.Stick != null)
                    return player.Stick.BladeHandlePosition;
                if (player?.PlayerBody?.Stick != null)
                    return player.PlayerBody.Stick.BladeHandlePosition;
            }
            catch { }

            return new Vector3(fallbackBodyPos.x, iceY + 0.08f, fallbackBodyPos.z);
        }

        private static Vector3 GetBodyChestPosition(Vector3 bodyPos, float iceY)
        {
            return new Vector3(bodyPos.x, iceY + 0.95f, bodyPos.z);
        }

        /// <summary>
        /// Retarget at release â€” net feeds aim at the net; slot feeds track stick/body with variance.
        /// </summary>
        private static void RetargetTipShot(
            RinkSlot slot,
            Player tipper,
            Vector3 tipperPos,
            Vector3 targetGoalPos,
            GoalieShotPhysics.TipFeedKind feedKind,
            ref Vector3 tipTarget)
        {
            float iceY = slot.Origin.y + VanillaRinkCloner.IceSurfaceY + 0.05f;

            switch (feedKind)
            {
                case GoalieShotPhysics.TipFeedKind.OnNet:
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: true);
                    tipTarget.y = Mathf.Max(tipTarget.y, iceY + 0.55f);
                    break;

                case GoalieShotPhysics.TipFeedKind.HighLooperNet:
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: true);
                    tipTarget.y = Mathf.Max(tipTarget.y, iceY + 0.85f);
                    break;

                case GoalieShotPhysics.TipFeedKind.WideTipper:
                {
                    float side = tipTarget.x - tipperPos.x;
                    if (Mathf.Abs(side) < 1.5f)
                        side = UnityEngine.Random.Range(2.2f, 4.5f) * (UnityEngine.Random.value > 0.5f ? 1f : -1f);
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: false);
                    tipTarget.x = tipperPos.x + side + UnityEngine.Random.Range(-0.3f, 0.3f);
                    tipTarget.y = Mathf.Max(tipTarget.y, iceY + 0.55f);
                    break;
                }

                case GoalieShotPhysics.TipFeedKind.HighLooperTipper:
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: false);
                    tipTarget.y = Mathf.Max(tipTarget.y, iceY + 0.65f);
                    break;

                case GoalieShotPhysics.TipFeedKind.LongStraight:
                case GoalieShotPhysics.TipFeedKind.AtTipper:
                default:
                    tipTarget = SampleTipperAimPoint(tipper, tipperPos, targetGoalPos, iceY, allowNet: false);
                    break;
            }

            tipTarget = ClampToRinkLocal(tipTarget, slot);
            tipTarget.y = Mathf.Max(tipTarget.y, iceY + 0.5f);
        }

        private static void PrepareTipPuckAt(Puck puck, Vector3 spawnPos)
        {
            if (puck == null || puck.gameObject == null)
                return;

            try
            {
                if (puck.Rigidbody != null)
                {
                    puck.Rigidbody.isKinematic = true;
                    puck.Rigidbody.linearVelocity = Vector3.zero;
                    puck.Rigidbody.angularVelocity = Vector3.zero;
                }

                puck.transform.SetPositionAndRotation(spawnPos, Quaternion.identity);
                PracticePuckSpawn.RegisterSpawnedPuck(puck, spawnPos);
            }
            catch { }
        }

        private static Vector3 ClampToRinkLocal(Vector3 worldPos, RinkSlot slot)
        {
            Vector3 local = worldPos - slot.Origin;
            float halfW = IceHalfWidth - IceClampMargin;
            float halfL = IceHalfLength - IceClampMargin;
            float cornerR = Mathf.Min(IceCornerRadius, halfW, halfL);

            local.x = Mathf.Clamp(local.x, -halfW, halfW);
            local.z = Mathf.Clamp(local.z, -halfL, halfL);

            // Pull points in the rounded-board corners back onto the ice arc.
            float cornerInnerX = halfW - cornerR;
            float cornerInnerZ = halfL - cornerR;
            float absX = Mathf.Abs(local.x);
            float absZ = Mathf.Abs(local.z);
            if (absX > cornerInnerX && absZ > cornerInnerZ)
            {
                float dx = absX - cornerInnerX;
                float dz = absZ - cornerInnerZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > cornerR && dist > 0.0001f)
                {
                    float scale = cornerR / dist;
                    local.x = Mathf.Sign(local.x) * (cornerInnerX + dx * scale);
                    local.z = Mathf.Sign(local.z) * (cornerInnerZ + dz * scale);
                }
            }

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

        internal static bool TryGetSkaterOnRink(int rinkIndex, out Player skater, out RinkSlot slot)
        {
            skater = null;
            if (!TryGetRinkSlot(rinkIndex, out slot))
                return false;

            skater = FindSkaterOnRink(rinkIndex, slot);
            return skater != null;
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

        private static void TrackGoaliePuck(int rinkIndex, Puck puck, float firedAt, float travelTime)
        {
            TrackPuck(rinkIndex, puck);
            if (puck == null)
                return;

            float expectedArrival = firedAt + travelTime;
            goaliePuckExpectedArrivalAt[puck] = expectedArrival;
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
                    goaliePuckExpectedArrivalAt.Remove(p);
                    tipPuckSafetyExpireAt.Remove(p);
                    tipPuckArriveAt.Remove(p);
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

                    bool pastArrival = goaliePuckExpectedArrivalAt.TryGetValue(p, out float arriveAt)
                        && now >= arriveAt + GoaliePostGoalDespawnGrace * 0.35f;
                    bool settledAfterArrival = pastArrival
                        && p.Rigidbody != null
                        && p.Rigidbody.linearVelocity.magnitude < PracticeConstants.SettledPuckVelocity;
                    if (settledAfterArrival && !puckPastGoalLine)
                    {
                        DestroyGoaliePuck(p, puckList, i);
                        continue;
                    }

                    if (goaliePuckSafetyExpireAt.TryGetValue(p, out float safetyExpireAt)
                        && now >= safetyExpireAt)
                    {
                        DestroyGoaliePuck(p, puckList, i);
                    }
                }
                catch { puckList.RemoveAt(i); }
            }

            while (puckList.Count > GoalieMaxFlyingPucks
                   || CountGoaliePracticePucks(rinkIndex) > GoalieMaxTotalPucks)
            {
                if (puckList.Count == 0)
                    break;

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
                goaliePuckExpectedArrivalAt.Remove(puck);
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

        /// <summary>Uniform net target â€” low, mid, and high slots equally likely.</summary>
        private static Vector3 RandomNetTargetOffset()
        {
            return new Vector3(
                UnityEngine.Random.Range(-NetHalfWidth, NetHalfWidth),
                UnityEngine.Random.Range(0.28f, NetHeight - 0.04f),
                0f);
        }
    }
}
