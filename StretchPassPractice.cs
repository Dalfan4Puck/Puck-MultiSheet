// ---- Pass service trace → server puck.log. Uncomment the next line to enable [PassSvc] lines. ----
// #define PASS_SERVICE_DEBUG_LOG

using System;
using System.Collections;
using System.Collections.Generic;
using PuckChasers;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    internal enum StretchPassVariant : byte
    {
        Normal = 0,
        Hard = 1,
        Soft = 2,
        Air = 3,
        Point = 4,
        Rim = 5,
        /// <summary>Low cycle — direct pass from the blue line down to the slot.</summary>
        Indirect = 6,
    }

    /// <summary>
    /// Per-rink stretch / point passing (SSPT /sp logic), scoped to strip modes.
    /// </summary>
    internal static class StretchPassPractice
    {
        private const float PassCleanupSeconds = 8f;
        private const float PassCleanupSecondsLowCycle = PassCleanupSeconds + 1f;
        private const float IceClampMargin = 0.85f;
        private const float IceCornerRadius = 7.5f;
        /// <summary>Seconds to wait after a pass before spawning the holder on the other dot.</summary>
        private const float HolderSpawnDelayStretch = 1.75f;
        private const float HolderSpawnDelayPoint = 1.5f;
        private const float HolderSpawnDelayLowCycle = 1.35f;
        private const float HolderSettleSeconds = 0.65f;
        /// <summary>Three-puck rotation: queued holder + pass in flight + the skater's last-received puck.</summary>
        private const int MaxPassPucksOnRink = 3;
        /// <summary>Extra look-lock beyond the estimated pass flight time before eyes move to the next dot.</summary>
        private const float LookHoldGraceSeconds = 0.35f;
        /// <summary>Clear drill pucks after the rink has had no skater for this long.</summary>
        private const float SkaterMissingClearSeconds = 3f;
        /// <summary>Inboard from dasher face when aiming at the wall.</summary>
        private const float BoardTargetInset = 0.65f;
        /// <summary>Inboard from corner/board geometry for spawn dots.</summary>
        private const float SpawnBoardInset = 1.4f;
        /// <summary>SSPT stretch faceoff dots — Blue net end (+Z); all drills live at the Blue end.</summary>
        private const float StretchDotHalfX = 15f;
        private const float StretchDotLocalZ = 22f;
        /// <summary>Chance a normal stretch pass becomes a billiard bank off the side glass.</summary>
        private const float StretchBankPassChance = 0.25f;

        private readonly struct PassFeedSides
        {
            internal readonly float WallSign;
            internal readonly float EndSign;

            internal PassFeedSides(float wallSign, float endSign)
            {
                WallSign = wallSign;
                EndSign = endSign;
            }
        }

        private static readonly Dictionary<int, Coroutine> loops = new Dictionary<int, Coroutine>();
        private static readonly Dictionary<int, StretchPassVariant> stretchVariant = new Dictionary<int, StretchPassVariant>();
        private static readonly Dictionary<int, int> pointPassIndex = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> pointPassAlternate = new Dictionary<int, bool>();
        /// <summary>Default point-passing mix: mostly wall rims with the occasional direct point feed.</summary>
        private static readonly StretchPassVariant[] PointPassSequence =
        {
            StretchPassVariant.Rim,
            StretchPassVariant.Rim,
            StretchPassVariant.Point,
        };
        private static readonly Dictionary<int, bool> useLeftDot = new Dictionary<int, bool>();
        private static readonly Dictionary<int, int> lowCyclePassIndex = new Dictionary<int, int>();
        /// <summary>Alternates left/right point-style wall spawn — independent of skater side.</summary>
        private static readonly Dictionary<int, bool> lowCycleLeftWall = new Dictionary<int, bool>();
        /// <summary>
        /// Strong-side feeds mix rims and indirect wall passes; weak-side spawns are
        /// always forced to Rim at launch (see IsLowCycleFarSideSpawn). Length 3 vs the
        /// 2-wall alternation so both walls see every variant over a full rotation.
        /// </summary>
        private static readonly StretchPassVariant[] LowCyclePassSequence =
        {
            StretchPassVariant.Rim,
            StretchPassVariant.Indirect,
            StretchPassVariant.Rim,
        };
        /// <summary>Board dot holder waiting for the next release.</summary>
        private static readonly Dictionary<int, Puck> queuedPassPuck = new Dictionary<int, Puck>();
        /// <summary>In-flight pass toward the skater.</summary>
        private static readonly Dictionary<int, Puck> flyingPassPuck = new Dictionary<int, Puck>();
        /// <summary>Third rotation slot: the skater's last-received puck, destroyed when the next pass fires.</summary>
        private static readonly Dictionary<int, Puck> retiredPassPuck = new Dictionary<int, Puck>();
        /// <summary>Keep the look on the in-flight pass until roughly this time before eyeing the next dot.</summary>
        private static readonly Dictionary<int, float> lookHoldUntil = new Dictionary<int, float>();
        /// <summary>When the rink first came up empty of skaters (for stranded-puck cleanup).</summary>
        private static readonly Dictionary<int, float> skaterMissingSince = new Dictionary<int, float>();
        private static readonly Dictionary<int, List<Puck>> flyingPucks = new Dictionary<int, List<Puck>>();
        private static readonly Dictionary<Puck, float> puckExpireAt = new Dictionary<Puck, float>();
        private static readonly Dictionary<int, float> passCycleStartedAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, int> passCycleNumber = new Dictionary<int, int>();
        private static readonly Dictionary<Puck, float> passPuckSpawnedAt = new Dictionary<Puck, float>();

        internal static void Apply(int rinkIndex, RinkStripMode mode)
        {
            PassLog(rinkIndex, "mode_apply", "mode=" + RinkStripModeUtil.DisplayName(mode));
            Stop(rinkIndex);

            if (mode == RinkStripMode.StretchPassing)
            {
                RinkPracticeDrills.ClearLoosePucksOnRink(rinkIndex);
                stretchVariant[rinkIndex] = StretchPassVariant.Normal;
                useLeftDot[rinkIndex] = true;
                StartLoop(rinkIndex, RinkStripMode.StretchPassing);
            }
            else if (mode == RinkStripMode.PointPassing)
            {
                RinkPracticeDrills.ClearLoosePucksOnRink(rinkIndex);
                pointPassAlternate[rinkIndex] = true;
                pointPassIndex[rinkIndex] = 0;
                StartLoop(rinkIndex, RinkStripMode.PointPassing);
            }
            else if (mode == RinkStripMode.LowCyclePassing)
            {
                RinkPracticeDrills.ClearLoosePucksOnRink(rinkIndex);
                pointPassAlternate[rinkIndex] = true;
                lowCyclePassIndex[rinkIndex] = 0;
                lowCycleLeftWall[rinkIndex] = true;
                StartLoop(rinkIndex, RinkStripMode.LowCyclePassing);
            }
            else
            {
                PassLog(rinkIndex, "mode_apply_skip", "not a pass mode");
            }
        }

        internal static void Stop(int rinkIndex)
        {
            PassLog(rinkIndex, "mode_stop", PassStateSummary(rinkIndex));
            MonoBehaviour host = CoroutineHost;
            if (host != null && loops.TryGetValue(rinkIndex, out Coroutine loop) && loop != null)
                host.StopCoroutine(loop);

            loops.Remove(rinkIndex);
            stretchVariant.Remove(rinkIndex);
            pointPassAlternate.Remove(rinkIndex);
            pointPassIndex.Remove(rinkIndex);
            useLeftDot.Remove(rinkIndex);
            lowCyclePassIndex.Remove(rinkIndex);
            lowCycleLeftWall.Remove(rinkIndex);
            queuedPassPuck.Remove(rinkIndex);
            flyingPassPuck.Remove(rinkIndex);
            CleanupFlying(rinkIndex, "mode_stop");
            passCycleStartedAt.Remove(rinkIndex);
            passCycleNumber.Remove(rinkIndex);
            lookHoldUntil.Remove(rinkIndex);
            skaterMissingSince.Remove(rinkIndex);
            GoaliePracticeLookTarget.ClearRink(rinkIndex);
        }

        internal static void StopAll()
        {
            var indices = new List<int>(loops.Keys);
            for (int i = 0; i < indices.Count; i++)
                Stop(indices[i]);
        }

        internal static void TickReconcile()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return;

            int count = MultiRinkConfig.Current.Rinks?.Count ?? 0;
            if (count <= 0)
                count = 6;

            for (int i = 0; i < count; i++)
            {
                RinkStripMode mode = RinkStripVote.GetServerMode(i);
                bool wantStretch = mode == RinkStripMode.StretchPassing;
                bool wantPoint = mode == RinkStripMode.PointPassing;
                bool wantLowCycle = mode == RinkStripMode.LowCyclePassing;

                if (wantStretch || wantPoint || wantLowCycle)
                {
                    EnforceMaxPucksOnRink(i, MaxPassPucksOnRink);
                    RefreshLook(i);
                }

                if (!wantStretch && !wantPoint && !wantLowCycle && loops.ContainsKey(i))
                {
                    PassLog(i, "reconcile_stop", "mode=" + RinkStripModeUtil.DisplayName(mode));
                    Stop(i);
                }
                else if (wantStretch || wantPoint || wantLowCycle)
                {
                    EnsurePassLoopRunning(i, mode);
                }
            }

            CleanupExpiredPucks();
        }

        /// <summary>R-key spawn on a pass-practice sheet — cap at two pucks; drill pucks win over player puck.</summary>
        internal static void OnPlayerPuckSpawned(int rinkIndex, Puck playerPuck)
        {
            if (rinkIndex < 0 || playerPuck == null)
                return;

            RinkStripMode mode = RinkStripVote.GetServerMode(rinkIndex);
            if (mode != RinkStripMode.StretchPassing
                && mode != RinkStripMode.PointPassing
                && mode != RinkStripMode.LowCyclePassing)
            {
                return;
            }

            PassLog(
                rinkIndex,
                "player_puck_spawn",
                PuckTag(playerPuck) + " " + PassStateSummary(rinkIndex));
            EnforceMaxPucksOnRink(rinkIndex, MaxPassPucksOnRink);
        }

        internal static int ResolveRinkIndexForWorldPos(Vector3 worldPos)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0)
                return -1;

            RinkStripMode mode = RinkStripVote.GetServerMode(RinkLocator.NearestRink(cfg, worldPos));
            int idx = RinkLocator.NearestRink(cfg, worldPos);
            if (idx < 0)
                return -1;

            mode = RinkStripVote.GetServerMode(idx);
            if (mode == RinkStripMode.StretchPassing
                || mode == RinkStripMode.PointPassing
                || mode == RinkStripMode.LowCyclePassing)
            {
                return idx;
            }

            return -1;
        }

        internal static bool TryHandleChat(ulong clientId, string styleArg, out string reply)
        {
            reply = null;
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return false;

            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            Player player = pm?.GetPlayerByClientId(clientId);
            if (player == null)
            {
                reply = "Could not resolve player.";
                return true;
            }

            int rinkIndex = ResolvePlayerRinkIndex(player);
            if (rinkIndex < 0)
            {
                reply = "Join a rink first.";
                return true;
            }

            RinkStripMode mode = RinkStripVote.GetServerMode(rinkIndex);
            StretchPassVariant? variant = ParseVariant(styleArg);
            if (variant == null)
            {
                reply = "Use: /sp [normal|hard|soft|air|point|rim|indirect|low]";
                return true;
            }

            if (mode == RinkStripMode.StretchPassing)
            {
                if (variant.Value == StretchPassVariant.Point
                    || variant.Value == StretchPassVariant.Rim
                    || variant.Value == StretchPassVariant.Indirect)
                {
                    reply = "Point/rim/low-cycle passes use Point Passing or Low Cycle Passing on the Rinks tab.";
                    return true;
                }

                stretchVariant[rinkIndex] = variant.Value;
                reply = "Rink " + (rinkIndex + 1) + " stretch pass style: " + VariantLabel(variant.Value) + ".";
                return true;
            }

            if (mode == RinkStripMode.PointPassing)
            {
                if (variant.Value == StretchPassVariant.Point)
                {
                    pointPassAlternate[rinkIndex] = false;
                    stretchVariant[rinkIndex] = StretchPassVariant.Point;
                    reply = "Rink " + (rinkIndex + 1) + " point passes only.";
                    return true;
                }

                if (variant.Value == StretchPassVariant.Rim)
                {
                    pointPassAlternate[rinkIndex] = false;
                    stretchVariant[rinkIndex] = StretchPassVariant.Rim;
                    reply = "Rink " + (rinkIndex + 1) + " rim passes only.";
                    return true;
                }

                if (variant.Value == StretchPassVariant.Normal)
                {
                    pointPassAlternate[rinkIndex] = true;
                    stretchVariant.Remove(rinkIndex);
                    reply = "Rink " + (rinkIndex + 1) + " mixed passes (mostly rim, occasional point).";
                    return true;
                }

                reply = "On Point Passing use /sp point, /sp rim, or /sp normal to alternate.";
                return true;
            }

            if (mode == RinkStripMode.LowCyclePassing)
            {
                if (variant.Value == StretchPassVariant.Indirect)
                {
                    pointPassAlternate[rinkIndex] = false;
                    stretchVariant[rinkIndex] = StretchPassVariant.Indirect;
                    reply = "Rink " + (rinkIndex + 1) + " low-cycle indirect passes only.";
                    return true;
                }

                if (variant.Value == StretchPassVariant.Rim)
                {
                    pointPassAlternate[rinkIndex] = false;
                    stretchVariant[rinkIndex] = StretchPassVariant.Rim;
                    reply = "Rink " + (rinkIndex + 1) + " low-cycle rim passes only.";
                    return true;
                }

                if (variant.Value == StretchPassVariant.Normal)
                {
                    pointPassAlternate[rinkIndex] = true;
                    stretchVariant.Remove(rinkIndex);
                    reply = "Rink " + (rinkIndex + 1) + " alternating indirect + rim low-cycle passes.";
                    return true;
                }

                reply = "On Low Cycle Passing use /sp indirect, /sp rim, or /sp normal to alternate.";
                return true;
            }

            reply = "Vote Stretch, Point, or Low Cycle Passing on the Rinks tab first.";
            return true;
        }

        internal static Puck ResolveLookPuck(int rinkIndex)
        {
            Puck flying = ResolveValidFlying(rinkIndex);

            // Keep eyes on the incoming pass until it has (roughly) arrived, then
            // switch to the next dot holder so skaters look ahead of the next feed.
            if (flying != null && IsLookHoldActive(rinkIndex))
                return flying;

            if (queuedPassPuck.TryGetValue(rinkIndex, out Puck queued)
                && queued != null
                && queued.gameObject != null)
            {
                return queued;
            }

            return flying;
        }

        internal static Puck ResolveQueuedLookPuck(int rinkIndex)
        {
            Puck flying = ResolveValidFlying(rinkIndex);
            queuedPassPuck.TryGetValue(rinkIndex, out Puck queued);
            bool queuedValid = queued != null && queued.gameObject != null;

            if (flying != null && IsLookHoldActive(rinkIndex))
                return queuedValid ? queued : null;

            if (flying != null)
                return flying;

            return queuedValid ? queued : null;
        }

        private static Puck ResolveValidFlying(int rinkIndex)
        {
            if (flyingPassPuck.TryGetValue(rinkIndex, out Puck flying)
                && flying != null
                && flying.gameObject != null)
            {
                return flying;
            }

            if (flyingPucks.TryGetValue(rinkIndex, out List<Puck> list) && list != null)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Puck p = list[i];
                    if (p != null && p.gameObject != null)
                        return p;
                }
            }

            return null;
        }

        private static bool IsLookHoldActive(int rinkIndex)
        {
            return lookHoldUntil.TryGetValue(rinkIndex, out float holdUntil)
                && Time.time < holdUntil;
        }

        /// <summary>Restart pass loop after an unexpected exit — no puck clear / full Stop.</summary>
        private static void EnsurePassLoopRunning(int rinkIndex, RinkStripMode mode)
        {
            if (loops.ContainsKey(rinkIndex))
                return;

            PassLog(rinkIndex, "reconcile_restart", "mode=" + RinkStripModeUtil.DisplayName(mode));
            StartLoop(rinkIndex, mode);
        }

        private static void StartLoop(int rinkIndex, RinkStripMode mode)
        {
            MonoBehaviour host = CoroutineHost;
            if (host == null)
                return;
            if (loops.ContainsKey(rinkIndex))
                return;

            Coroutine loop = host.StartCoroutine(PassLoop(rinkIndex, mode));
            loops[rinkIndex] = loop;
            passCycleNumber[rinkIndex] = 0;
            PassLog(rinkIndex, "loop_start", "mode=" + RinkStripModeUtil.DisplayName(mode));
            PracticeLog.Info("[PHLPractice] Rink " + (rinkIndex + 1) + " " +
                             RinkStripModeUtil.DisplayName(mode) + " started.");
        }

        private static IEnumerator PassLoop(int rinkIndex, RinkStripMode stripMode)
        {
            yield return new WaitForSeconds(0.5f);

            try
            {
                while (RinkStripVote.GetServerMode(rinkIndex) == stripMode)
                {
                    EnforceMaxPucksOnRink(rinkIndex, MaxPassPucksOnRink);
                    CleanupExpiredPucks();
                    PruneOrphanPassHolders(rinkIndex);

                    if (!RinkPracticeDrills.TryGetSkaterOnRink(rinkIndex, out Player skater, out RinkSlot slot))
                    {
                        HandleSkaterMissing(rinkIndex);
                        yield return new WaitForSeconds(1f);
                        continue;
                    }

                    skaterMissingSince.Remove(rinkIndex);

                    if (skater?.PlayerBody == null)
                    {
                        yield return new WaitForSeconds(0.5f);
                        continue;
                    }

                    Vector3 playerLocal = GetPassTargetLocal(skater, slot, stripMode);
                    Vector3 playerVelocity = Vector3.zero;
                    if (skater.PlayerBody.Rigidbody != null)
                        playerVelocity = skater.PlayerBody.Rigidbody.linearVelocity;

                    PassFeedSides sides = ResolveFeedSides(playerLocal);
                    PruneStaleQueuedPuck(rinkIndex);

                    StretchPassVariant variant = ResolveActiveVariant(rinkIndex, stripMode);
                    int cycle = passCycleNumber.TryGetValue(rinkIndex, out int cycleNum) ? cycleNum + 1 : 1;
                    passCycleNumber[rinkIndex] = cycle;
                    passCycleStartedAt[rinkIndex] = Time.time;
                    PassLog(
                        rinkIndex,
                        "cycle_begin",
                        "cycle=" + cycle + " variant=" + VariantLabel(variant) + " " + PassStateSummary(rinkIndex));

                    bool holderSpawnedThisCycle = false;
                    if (!HasValidQueued(rinkIndex))
                    {
                        EnforceMaxPucksOnRink(rinkIndex, MaxPassPucksOnRink);
                        Puck holder = SpawnHolderPuck(
                            rinkIndex,
                            variant,
                            stripMode,
                            slot,
                            sides,
                            playerLocal,
                            "cycle_primary");
                        if (holder == null)
                        {
                            PassLog(rinkIndex, "spawn_holder_failed", "cycle=" + cycle + " role=primary");
                            yield return new WaitForSeconds(1f);
                            continue;
                        }

                        queuedPassPuck[rinkIndex] = holder;
                        holderSpawnedThisCycle = true;
                        RefreshLook(rinkIndex);
                        float settleUntil = Time.time + GetHolderSettleSeconds(stripMode);
                        PassLog(
                            rinkIndex,
                            "holder_settle_wait",
                            "cycle=" + cycle + " " + PuckTag(holder) + " seconds=" + GetHolderSettleSeconds(stripMode).ToString("F2"));
                        while (Time.time < settleUntil
                               && RinkStripVote.GetServerMode(rinkIndex) == stripMode)
                        {
                            RefreshLook(rinkIndex);
                            yield return new WaitForSeconds(0.05f);
                        }

                        PassLog(
                            rinkIndex,
                            "holder_settled",
                            "cycle=" + cycle + " " + PuckTag(holder) + " dt=" + ElapsedSinceCycleStart(rinkIndex).ToString("F2"));
                    }

                    if (!queuedPassPuck.TryGetValue(rinkIndex, out Puck queued)
                        || queued == null
                        || queued.gameObject == null)
                    {
                        queuedPassPuck.Remove(rinkIndex);
                        yield return new WaitForSeconds(0.25f);
                        continue;
                    }

                    playerLocal = GetPassTargetLocal(skater, slot, stripMode);
                    if (skater.PlayerBody.Rigidbody != null)
                        playerVelocity = skater.PlayerBody.Rigidbody.linearVelocity;
                    sides = ResolveFeedSides(playerLocal);

                    // Preloaded holder from the prior cycle is already on its dot — don't re-randomize.
                    if (holderSpawnedThisCycle)
                    {
                        if (stripMode == RinkStripMode.StretchPassing)
                            SnapStretchHolder(rinkIndex, queued, slot);
                        else
                            RepositionHolder(rinkIndex, queued, variant, stripMode, slot, sides, playerLocal);
                    }

                    if (!LaunchPassPuck(
                            rinkIndex,
                            queued,
                            variant,
                            stripMode,
                            slot,
                            sides,
                            playerLocal,
                            playerVelocity,
                            boardFeed: true,
                            cycle: cycle))
                    {
                        PassLog(
                            rinkIndex,
                            "pass_launch_failed",
                            "cycle=" + cycle + " " + PuckTag(queued) + " dt=" + ElapsedSinceCycleStart(rinkIndex).ToString("F2"));
                        RefreshLook(rinkIndex);
                        yield return new WaitForSeconds(0.35f);
                        continue;
                    }

                    queuedPassPuck.Remove(rinkIndex);
                    ToggleVariantAfterPass(rinkIndex, stripMode, variant);
                    if (stripMode == RinkStripMode.StretchPassing)
                        AdvanceStretchDot(rinkIndex);

                    RefreshLook(rinkIndex);
                    float holderSpawnDelay = GetHolderSpawnDelay(stripMode);
                    PassLog(
                        rinkIndex,
                        "pass_launched",
                        "cycle=" + cycle + " variant=" + VariantLabel(variant) + " spawnDelay=" + holderSpawnDelay.ToString("F2")
                        + " " + PassStateSummary(rinkIndex));
                    float spawnDelayUntil = Time.time + holderSpawnDelay;
                    while (Time.time < spawnDelayUntil
                           && RinkStripVote.GetServerMode(rinkIndex) == stripMode)
                    {
                        RefreshLook(rinkIndex);
                        yield return new WaitForSeconds(0.05f);
                    }

                    EnforceMaxPucksOnRink(rinkIndex, MaxPassPucksOnRink);
                    variant = ResolveActiveVariant(rinkIndex, stripMode);
                    sides = ResolveFeedSides(playerLocal);

                    if (!HasValidQueued(rinkIndex))
                    {
                        Puck next = SpawnHolderPuck(
                            rinkIndex,
                            variant,
                            stripMode,
                            slot,
                            sides,
                            playerLocal,
                            "cycle_preload");
                        if (next != null)
                        {
                            queuedPassPuck[rinkIndex] = next;
                            PassLog(
                                rinkIndex,
                                "preload_queued",
                                "cycle=" + cycle + " " + PuckTag(next) + " dt=" + ElapsedSinceCycleStart(rinkIndex).ToString("F2"));
                        }
                        else
                        {
                            PassLog(
                                rinkIndex,
                                "spawn_holder_failed",
                                "cycle=" + cycle + " role=preload dt=" + ElapsedSinceCycleStart(rinkIndex).ToString("F2"));
                        }
                    }

                    RefreshLook(rinkIndex);
                    float gapSeconds = GetPassGapSeconds(rinkIndex, stripMode);
                    PassLog(
                        rinkIndex,
                        "cycle_gap_wait",
                        "cycle=" + cycle + " gap=" + gapSeconds.ToString("F2") + " " + PassStateSummary(rinkIndex));
                    float gapUntil = Time.time + gapSeconds;
                    while (Time.time < gapUntil
                           && RinkStripVote.GetServerMode(rinkIndex) == stripMode)
                    {
                        RefreshLook(rinkIndex);
                        yield return new WaitForSeconds(0.05f);
                    }
                    CleanupExpiredPucks();
                }
            }
            finally
            {
                PassLog(rinkIndex, "loop_end", PassStateSummary(rinkIndex));
                loops.Remove(rinkIndex);
                CleanupFlying(rinkIndex, "loop_end");
                GoaliePracticeLookTarget.ClearRink(rinkIndex);
            }
        }

        private static bool HasValidQueued(int rinkIndex)
        {
            return queuedPassPuck.TryGetValue(rinkIndex, out Puck queued)
                && queued != null
                && queued.gameObject != null;
        }

        /// <summary>Nobody on the sheet: after a short grace, clear stranded drill pucks (dot holders included).</summary>
        private static void HandleSkaterMissing(int rinkIndex)
        {
            if (!skaterMissingSince.TryGetValue(rinkIndex, out float since))
            {
                skaterMissingSince[rinkIndex] = Time.time;
                return;
            }

            if (Time.time - since < SkaterMissingClearSeconds)
                return;

            bool hadPucks = HasValidQueued(rinkIndex)
                || (flyingPassPuck.TryGetValue(rinkIndex, out Puck flying) && flying != null)
                || (retiredPassPuck.TryGetValue(rinkIndex, out Puck retired) && retired != null);
            if (!hadPucks)
                return;

            PassLog(rinkIndex, "skater_missing_clear", PassStateSummary(rinkIndex));
            CleanupFlying(rinkIndex, "skater_missing");
            GoaliePracticeLookTarget.ClearRink(rinkIndex);
        }

        private static float GetHolderSettleSeconds(RinkStripMode stripMode)
        {
            return HolderSettleSeconds;
        }

        private static float GetHolderSpawnDelay(RinkStripMode stripMode)
        {
            if (stripMode == RinkStripMode.LowCyclePassing)
                return HolderSpawnDelayLowCycle;
            if (stripMode == RinkStripMode.PointPassing)
                return HolderSpawnDelayPoint;
            return HolderSpawnDelayStretch;
        }

        /// <summary>Destroy kinematic drill holders that lost queue tracking (stuck dot pucks).</summary>
        private static void PruneOrphanPassHolders(int rinkIndex)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || rinkIndex < 0 || rinkIndex >= cfg.Rinks.Count)
                return;

            RinkSlot slot = cfg.Rinks[rinkIndex];
            if (slot == null)
                return;

            queuedPassPuck.TryGetValue(rinkIndex, out Puck queued);
            flyingPassPuck.TryGetValue(rinkIndex, out Puck flying);

            PuckManager pm = MonoBehaviourSingleton<PuckManager>.Instance;
            if (pm == null)
                return;

            List<Puck> all = null;
            try { all = pm.GetPucks(false); }
            catch { }

            if (all == null)
                return;

            for (int i = 0; i < all.Count; i++)
            {
                Puck puck = all[i];
                if (puck == null || puck.gameObject == null)
                    continue;
                if (puck == queued || puck == flying)
                    continue;
                if (!IsWorldOnRink(puck.transform.position, slot))
                    continue;
                if (puck.Rigidbody == null || !puck.Rigidbody.isKinematic)
                    continue;

                PassLog(rinkIndex, "orphan_holder_pruned", PuckTag(puck));
                DestroyPuck(puck, "orphan_holder");
            }
        }

        private static void PruneStaleQueuedPuck(int rinkIndex)
        {
            if (!queuedPassPuck.TryGetValue(rinkIndex, out Puck queued))
                return;

            if (queued != null && queued.gameObject != null)
                return;

            queuedPassPuck.Remove(rinkIndex);
        }

        private static float GetPassGapSeconds(int rinkIndex, RinkStripMode stripMode)
        {
            // The 3-puck rotation only adds receive slack — it must not turn the
            // sequence into a barrage. Keep the cycle near the point-passing cadence.
            if (stripMode == RinkStripMode.LowCyclePassing)
                return 3.1f;

            if (stripMode == RinkStripMode.PointPassing)
                return 2f;

            if (stretchVariant.TryGetValue(rinkIndex, out StretchPassVariant variant)
                && variant == StretchPassVariant.Hard)
            {
                return 3f;
            }

            return 4f;
        }

        private static void ToggleVariantAfterPass(int rinkIndex, RinkStripMode stripMode, StretchPassVariant launched)
        {
            if (stripMode == RinkStripMode.LowCyclePassing)
            {
                // Advance the scheduled index, not the launched variant — weak-side launches
                // get forced to Rim and must not stall the rotation.
                int idx = lowCyclePassIndex.TryGetValue(rinkIndex, out int current) ? current : 0;
                lowCyclePassIndex[rinkIndex] = (idx + 1) % LowCyclePassSequence.Length;
                AdvanceLowCycleSpawnWall(rinkIndex);
                return;
            }

            if (!pointPassAlternate.TryGetValue(rinkIndex, out bool alternate) || !alternate)
                return;

            if (stripMode == RinkStripMode.PointPassing)
            {
                int idx = pointPassIndex.TryGetValue(rinkIndex, out int current) ? current : 0;
                pointPassIndex[rinkIndex] = (idx + 1) % PointPassSequence.Length;
            }
        }

        private static void EnforceMaxPucksOnRink(int rinkIndex, int maxCount, Puck alwaysKeep = null)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || rinkIndex < 0 || rinkIndex >= cfg.Rinks.Count)
                return;

            RinkSlot slot = cfg.Rinks[rinkIndex];
            if (slot == null)
                return;

            var protect = new HashSet<Puck>();
            if (alwaysKeep != null)
                protect.Add(alwaysKeep);

            if (flyingPassPuck.TryGetValue(rinkIndex, out Puck flying) && flying != null)
                protect.Add(flying);

            if (queuedPassPuck.TryGetValue(rinkIndex, out Puck queued) && queued != null)
                protect.Add(queued);

            if (retiredPassPuck.TryGetValue(rinkIndex, out Puck retired) && retired != null)
                protect.Add(retired);

            try
            {
                PuckManager pm = MonoBehaviourSingleton<PuckManager>.Instance;
                if (pm == null)
                    return;

                List<Puck> all = null;
                try { all = pm.GetPucks(false); }
                catch { }

                if (all == null)
                    return;

                var onRink = new List<Puck>();
                for (int i = 0; i < all.Count; i++)
                {
                    Puck puck = all[i];
                    if (puck == null || puck.gameObject == null)
                        continue;
                    if (!IsWorldOnRink(puck.transform.position, slot))
                        continue;
                    onRink.Add(puck);
                }

                while (onRink.Count > maxCount)
                {
                    Puck victim = PickPuckToCull(onRink, protect);
                    if (victim == null)
                        break;

                    PassLog(
                        rinkIndex,
                        "cap_cull",
                        PuckTag(victim) + " onRink=" + onRink.Count + " max=" + maxCount + " " + PassStateSummary(rinkIndex));
                    onRink.Remove(victim);
                    DestroyPuck(victim, "cap_cull");
                }
            }
            catch (Exception ex)
            {
                PracticeLog.Info("[PHLPractice] Pass puck cap failed: " + ex.Message);
                PassLog(rinkIndex, "cap_error", ex.Message);
            }
        }

        private static Puck PickPuckToCull(List<Puck> onRink, HashSet<Puck> protect)
        {
            for (int i = 0; i < onRink.Count; i++)
            {
                Puck p = onRink[i];
                if (p != null && !protect.Contains(p))
                    return p;
            }

            return null;
        }

        private static bool IsWorldOnRink(Vector3 worldPos, RinkSlot slot)
        {
            Vector3 local = worldPos - slot.Origin;
            return Mathf.Abs(local.x) <= RinkGeometry.HalfWidth + 4f
                && Mathf.Abs(local.z) <= RinkGeometry.HalfLength + 4f;
        }

        private static void AdvanceStretchDot(int rinkIndex)
        {
            bool left = !useLeftDot.TryGetValue(rinkIndex, out bool useLeft) || useLeft;
            useLeftDot[rinkIndex] = !left;
        }

        private static void SnapStretchHolder(int rinkIndex, Puck puck, RinkSlot slot)
        {
            if (puck?.Rigidbody == null)
                return;

            Vector3 startLocal = ClampPassLocal(PickStretchDotLocal(rinkIndex));
            Vector3 startWorld = LocalToWorld(slot, startLocal);
            puck.Rigidbody.isKinematic = true;
            puck.Rigidbody.linearVelocity = Vector3.zero;
            puck.Rigidbody.angularVelocity = Vector3.zero;
            puck.transform.SetPositionAndRotation(startWorld, Quaternion.identity);
        }

        private static void RepositionHolder(
            int rinkIndex,
            Puck puck,
            StretchPassVariant variant,
            RinkStripMode stripMode,
            RinkSlot slot,
            PassFeedSides sides,
            Vector3 playerLocal)
        {
            if (puck?.Rigidbody == null)
                return;

            Vector3 startLocal = ClampPassLocal(PickStartLocal(rinkIndex, variant, stripMode, sides, playerLocal));
            Vector3 startWorld = LocalToWorld(slot, startLocal);
            puck.Rigidbody.isKinematic = true;
            puck.Rigidbody.linearVelocity = Vector3.zero;
            puck.Rigidbody.angularVelocity = Vector3.zero;
            puck.transform.SetPositionAndRotation(startWorld, Quaternion.identity);
        }

        private static Puck SpawnHolderPuck(
            int rinkIndex,
            StretchPassVariant variant,
            RinkStripMode stripMode,
            RinkSlot slot,
            PassFeedSides sides,
            Vector3 playerLocal,
            string spawnRole)
        {
            Vector3 startLocal = ClampPassLocal(PickStartLocal(rinkIndex, variant, stripMode, sides, playerLocal));
            Vector3 startWorld = LocalToWorld(slot, startLocal);
            float spawnAt = Time.time;
            Puck puck = PracticePuckSpawn.SpawnAt(startWorld, Quaternion.identity, Vector3.zero);
            if (puck?.Rigidbody == null)
            {
                PassLog(
                    rinkIndex,
                    "spawn_holder_reject",
                    "role=" + spawnRole + " variant=" + VariantLabel(variant) + " local=" + FormatLocal(startLocal));
                if (puck?.gameObject != null)
                {
                    try { UnityEngine.Object.Destroy(puck.gameObject); }
                    catch { }
                }

                return null;
            }

            puck.Rigidbody.isKinematic = true;
            puck.Rigidbody.linearVelocity = Vector3.zero;
            puck.Rigidbody.angularVelocity = Vector3.zero;
            puck.transform.SetPositionAndRotation(startWorld, Quaternion.identity);
            passPuckSpawnedAt[puck] = spawnAt;

            // Point/low-cycle runs a 3-puck rotation with crossing paths — a feed clipping
            // the puck the skater is stickhandling ruins both, so drill pucks pass through
            // each other. Every drill puck comes through here, so pairing each new puck
            // against all pucks currently on the rink covers every coexisting pair.
            if (stripMode == RinkStripMode.PointPassing || stripMode == RinkStripMode.LowCyclePassing)
                IgnorePuckPuckCollision(slot, puck);
            PassLog(
                rinkIndex,
                "spawn_holder",
                "role=" + spawnRole + " variant=" + VariantLabel(variant) + " " + PuckTag(puck)
                + " local=" + FormatLocal(startLocal) + " dtCycle=" + ElapsedSinceCycleStart(rinkIndex).ToString("F2"));
            return puck;
        }

        private static void IgnorePuckPuckCollision(RinkSlot slot, Puck newPuck)
        {
            if (newPuck == null || newPuck.gameObject == null)
                return;

            PuckManager pm = MonoBehaviourSingleton<PuckManager>.Instance;
            if (pm == null)
                return;

            List<Puck> all = null;
            try { all = pm.GetPucks(false); }
            catch { }
            if (all == null)
                return;

            Collider[] newCols = newPuck.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < all.Count; i++)
            {
                Puck other = all[i];
                if (other == null || other == newPuck || other.gameObject == null)
                    continue;
                if (!IsWorldOnRink(other.transform.position, slot))
                    continue;

                Collider[] otherCols = other.GetComponentsInChildren<Collider>(true);
                for (int a = 0; a < newCols.Length; a++)
                {
                    Collider colA = newCols[a];
                    if (colA == null || colA.isTrigger)
                        continue;
                    for (int b = 0; b < otherCols.Length; b++)
                    {
                        Collider colB = otherCols[b];
                        if (colB == null || colB.isTrigger)
                            continue;
                        try { Physics.IgnoreCollision(colA, colB, true); }
                        catch { }
                    }
                }
            }
        }

        private static bool LaunchPassPuck(
            int rinkIndex,
            Puck puck,
            StretchPassVariant variant,
            RinkStripMode stripMode,
            RinkSlot slot,
            PassFeedSides sides,
            Vector3 playerLocal,
            Vector3 playerVelocity,
            bool boardFeed,
            int cycle)
        {
            if (puck == null || puck.gameObject == null)
            {
                PassLog(rinkIndex, "pass_launch_reject", "cycle=" + cycle + " reason=null_puck");
                return false;
            }

            Rigidbody body = puck.Rigidbody;
            if (body == null)
            {
                PassLog(rinkIndex, "pass_launch_reject", "cycle=" + cycle + " " + PuckTag(puck) + " reason=no_rigidbody");
                return false;
            }

            flyingPassPuck.TryGetValue(rinkIndex, out Puck priorFlying);

            try
            {
                Vector3 startWorld = puck.transform.position;
                Vector3 startLocal = startWorld - slot.Origin;
                startLocal.y = 0f;

                StretchPassVariant launchVariant = variant;
                PassFeedSides planSides = sides;
                if (stripMode == RinkStripMode.LowCyclePassing)
                {
                    float spawnWallSign = Mathf.Abs(startLocal.x) > 0.75f
                        ? Mathf.Sign(startLocal.x)
                        : sides.WallSign;
                    planSides = new PassFeedSides(spawnWallSign, sides.EndSign);

                    if (IsLowCycleFarSideSpawn(startLocal, playerLocal))
                    {
                        launchVariant = StretchPassVariant.Rim;
                        PassLog(
                            rinkIndex,
                            "pass_far_side_rim",
                            "cycle=" + cycle + " " + PuckTag(puck) + " spawn=" + FormatLocal(startLocal)
                            + " player=" + FormatLocal(playerLocal));
                    }
                }

                if (!PlanPass(
                        launchVariant,
                        stripMode,
                        planSides,
                        startLocal,
                        playerLocal,
                        playerVelocity,
                        out Vector3 targetLocal,
                        out float passSpeed,
                        out float maxHeight))
                {
                    PassLog(
                        rinkIndex,
                        "pass_plan_failed",
                        "cycle=" + cycle + " " + PuckTag(puck) + " variant=" + VariantLabel(launchVariant));
                    return false;
                }

                Vector3 targetWorld = LocalToWorld(slot, targetLocal);
                targetWorld.y = startWorld.y;

                Vector3 direction = targetWorld - startWorld;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.0001f)
                {
                    Vector3 toPlayer = LocalToWorld(slot, playerLocal) - startWorld;
                    toPlayer.y = 0f;
                    if (toPlayer.sqrMagnitude < 0.0001f)
                    {
                        PassLog(
                            rinkIndex,
                            "pass_launch_reject",
                            "cycle=" + cycle + " " + PuckTag(puck) + " reason=zero_direction");
                        return false;
                    }

                    direction = toPlayer.normalized;
                }
                else
                {
                    direction = direction.normalized;
                }

                Vector3 passVelocity = direction * passSpeed;
                passVelocity.y = maxHeight;

                body.isKinematic = false;
                body.linearVelocity = passVelocity;
                body.angularVelocity = Vector3.zero;

                float ageSinceSpawn = passPuckSpawnedAt.TryGetValue(puck, out float spawnedAt)
                    ? Time.time - spawnedAt
                    : -1f;
                float horizDist = Vector3.Distance(
                    new Vector3(startLocal.x, 0f, startLocal.z),
                    new Vector3(targetLocal.x, 0f, targetLocal.z));
                PassLog(
                    rinkIndex,
                    "pass_fire",
                    "cycle=" + cycle + " " + PuckTag(puck) + " variant=" + VariantLabel(launchVariant)
                    + " speed=" + passSpeed.ToString("F1") + " lift=" + maxHeight.ToString("F2")
                    + " dist=" + horizDist.ToString("F1") + " age=" + ageSinceSpawn.ToString("F2")
                    + " dtCycle=" + ElapsedSinceCycleStart(rinkIndex).ToString("F2")
                    + " target=" + FormatLocal(targetLocal));

                // Low cycle: stay locked on the current pass until the NEXT fire replaces
                // the flying puck — never glance early at the queued dot holder.
                lookHoldUntil[rinkIndex] = stripMode == RinkStripMode.LowCyclePassing
                    ? float.PositiveInfinity
                    : Time.time
                      + horizDist / Mathf.Max(passSpeed, 1f)
                      + LookHoldGraceSeconds;

                if (boardFeed)
                {
                    flyingPassPuck[rinkIndex] = puck;
                    puckExpireAt[puck] = Time.time + GetPassCleanupSeconds(stripMode);
                    RefreshLook(rinkIndex);
                }
                else
                {
                    TrackFlying(rinkIndex, puck);
                }

                if (priorFlying != null && priorFlying != puck)
                    RetirePriorPassPuck(rinkIndex, priorFlying, stripMode);

                return true;
            }
            catch (Exception ex)
            {
                PassLog(
                    rinkIndex,
                    "pass_launch_exception",
                    "cycle=" + cycle + " " + PuckTag(puck) + " " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static StretchPassVariant ResolveActiveVariant(int rinkIndex, RinkStripMode stripMode)
        {
            if (stripMode == RinkStripMode.PointPassing)
            {
                if (pointPassAlternate.TryGetValue(rinkIndex, out bool alternate) && alternate)
                {
                    int idx = pointPassIndex.TryGetValue(rinkIndex, out int current) ? current : 0;
                    if (idx < 0)
                        idx = 0;
                    return PointPassSequence[idx % PointPassSequence.Length];
                }

                if (stretchVariant.TryGetValue(rinkIndex, out StretchPassVariant forced)
                    && (forced == StretchPassVariant.Point || forced == StretchPassVariant.Rim))
                {
                    return forced;
                }

                return StretchPassVariant.Point;
            }

            if (stripMode == RinkStripMode.LowCyclePassing)
            {
                // /sp rim | /sp indirect pins a single variant (alternate off).
                if (pointPassAlternate.TryGetValue(rinkIndex, out bool lowAlt) && !lowAlt
                    && stretchVariant.TryGetValue(rinkIndex, out StretchPassVariant forcedLow)
                    && (forcedLow == StretchPassVariant.Rim || forcedLow == StretchPassVariant.Indirect))
                {
                    return forcedLow;
                }

                int idx = lowCyclePassIndex.TryGetValue(rinkIndex, out int current) ? current : 0;
                if (idx < 0)
                    idx = 0;
                return LowCyclePassSequence[idx % LowCyclePassSequence.Length];
            }

            if (stretchVariant.TryGetValue(rinkIndex, out StretchPassVariant variant))
                return variant;

            return StretchPassVariant.Normal;
        }

        private static PassFeedSides ResolveFeedSides(Vector3 playerLocal)
        {
            float wallSign = playerLocal.x >= 0f ? 1f : -1f;
            // Every pass drill lives at the Blue net end (+Z) — never flip with the skater.
            return new PassFeedSides(wallSign, 1f);
        }

        private static Vector3 PickStartLocal(
            int rinkIndex,
            StretchPassVariant variant,
            RinkStripMode stripMode,
            PassFeedSides sides,
            Vector3 playerLocal)
        {
            if (variant == StretchPassVariant.Point)
                return PickPointPassWallSpawn(sides);

            if (stripMode == RinkStripMode.LowCyclePassing)
            {
                PassFeedSides spawnSides = ResolveLowCycleSpawnSides(rinkIndex, sides);
                return PickLowCycleWallSpawn(spawnSides);
            }

            if (variant == StretchPassVariant.Rim)
                return PickPointPassRimSpawn(sides);

            if (stripMode == RinkStripMode.StretchPassing)
                return PickStretchDotLocal(rinkIndex);

            float dotX = sides.WallSign * 15f;
            float dotZ = sides.EndSign * (RinkGeometry.BlueLineZ + 7f);
            return new Vector3(dotX, 0f, dotZ);
        }

        /// <summary>Blue-end faceoff dots — alternates x = ±15 at fixed z = +22.</summary>
        private static Vector3 PickStretchDotLocal(int rinkIndex)
        {
            bool left = !useLeftDot.TryGetValue(rinkIndex, out bool useLeft) || useLeft;
            float x = left ? -StretchDotHalfX : StretchDotHalfX;
            return new Vector3(x, 0f, StretchDotLocalZ);
        }

        /// <summary>Point feed dot hugging the skater's wall in the end zone.</summary>
        private static Vector3 PickPointPassWallSpawn(PassFeedSides sides)
        {
            float absZ = UnityEngine.Random.Range(
                RinkGeometry.NetZ + 1.2f,
                RinkGeometry.HalfLength - IceClampMargin - 0.5f);
            return WallLaneLocal(sides, absZ, SpawnBoardInset);
        }

        /// <summary>Rim feed — same end/wall, slightly more inboard before firing at the dasher.</summary>
        private static Vector3 PickPointPassRimSpawn(PassFeedSides sides)
        {
            float absZ = UnityEngine.Random.Range(
                RinkGeometry.NetZ + 0.8f,
                RinkGeometry.HalfLength - IceClampMargin - 0.5f);
            Vector3 onWall = WallLaneLocal(sides, absZ, SpawnBoardInset + 1.8f);
            onWall.x += UnityEngine.Random.Range(-1.5f, 1.5f);
            return onWall;
        }

        /// <summary>Low-cycle feed dot at the point (blue-line depth) on the wall — passes fire DOWN into the zone.</summary>
        private static Vector3 PickLowCycleWallSpawn(PassFeedSides sides)
        {
            // Point positions: just inside the blue line, a stride or two off the wall.
            float absZ = UnityEngine.Random.Range(
                RinkGeometry.BlueLineZ + 4f,
                RinkGeometry.BlueLineZ + 10f);
            return WallLaneLocal(sides, absZ, SpawnBoardInset + 2f);
        }

        private static PassFeedSides ResolveLowCycleSpawnSides(int rinkIndex, PassFeedSides playerSides)
        {
            bool useLeft = !lowCycleLeftWall.TryGetValue(rinkIndex, out bool left) || left;
            float wallSign = useLeft ? -1f : 1f;
            return new PassFeedSides(wallSign, playerSides.EndSign);
        }

        private static void AdvanceLowCycleSpawnWall(int rinkIndex)
        {
            bool left = !lowCycleLeftWall.TryGetValue(rinkIndex, out bool useLeft) || useLeft;
            lowCycleLeftWall[rinkIndex] = !left;
        }

        private static bool IsLowCycleFarSideSpawn(Vector3 spawnLocal, Vector3 playerLocal)
        {
            if (Mathf.Abs(spawnLocal.x) < 1f || Mathf.Abs(playerLocal.x) < 1f)
                return false;

            return Mathf.Sign(spawnLocal.x) != Mathf.Sign(playerLocal.x);
        }

        private static float GetPassCleanupSeconds(RinkStripMode stripMode)
        {
            if (stripMode == RinkStripMode.LowCyclePassing)
                return PassCleanupSecondsLowCycle;
            return PassCleanupSeconds;
        }

        /// <summary>
        /// Three-puck rotation: the puck the skater just received stays in play for one
        /// more full cycle. Only the previously-retired puck is destroyed at each fire.
        /// </summary>
        private static void RetirePriorPassPuck(int rinkIndex, Puck prior, RinkStripMode stripMode)
        {
            if (retiredPassPuck.TryGetValue(rinkIndex, out Puck oldest)
                && oldest != null
                && oldest != prior)
            {
                DestroyPuck(oldest, "retired_rotation");
            }

            retiredPassPuck[rinkIndex] = prior;
            puckExpireAt[prior] = Time.time + GetPassCleanupSeconds(stripMode);
            PassLog(rinkIndex, "puck_retired", PuckTag(prior));
        }

        private static Vector3 GetPassTargetLocal(Player skater, RinkSlot slot, RinkStripMode stripMode)
        {
            if (stripMode == RinkStripMode.LowCyclePassing && skater?.PlayerBody != null)
                return skater.PlayerBody.transform.position - slot.Origin;

            Vector3 playerPos = GetBladePosition(skater);
            if (playerPos == default && skater?.PlayerBody != null)
                playerPos = skater.PlayerBody.transform.position;

            return playerPos - slot.Origin;
        }

        private static Vector3 WallLaneLocal(PassFeedSides sides, float absZ, float inboardFromBoard)
        {
            float boardHalf = RinkGeometry.BoardHalfWidthAtZ(absZ) - inboardFromBoard;
            boardHalf = Mathf.Clamp(boardHalf, 6f, RinkGeometry.HalfWidth - 2f);
            return new Vector3(
                sides.WallSign * boardHalf,
                0f,
                sides.EndSign * absZ);
        }

        private static float ResolveBoardsX(float wallSign, float absZ)
        {
            float half = RinkGeometry.BoardHalfWidthAtZ(absZ) - BoardTargetInset;
            half = Mathf.Clamp(half, 8f, RinkGeometry.HalfWidth - 1f);
            return wallSign * half;
        }

        private static Vector3 ClampPassLocal(Vector3 local)
        {
            float halfW = RinkGeometry.HalfWidth - IceClampMargin;
            float halfL = RinkGeometry.HalfLength - IceClampMargin;
            float cornerR = Mathf.Min(IceCornerRadius, halfW, halfL);

            local.x = Mathf.Clamp(local.x, -halfW, halfW);
            local.z = Mathf.Clamp(local.z, -halfL, halfL);

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

            local.y = 0f;
            return local;
        }
        private static bool PlanPass(
            StretchPassVariant variant,
            RinkStripMode stripMode,
            PassFeedSides sides,
            Vector3 startLocal,
            Vector3 playerLocal,
            Vector3 playerVelocity,
            out Vector3 targetLocal,
            out float passSpeed,
            out float maxHeight)
        {
            targetLocal = playerLocal;
            passSpeed = 30f;
            maxHeight = 2f;

            float estimatedDistance = Vector3.Distance(
                new Vector3(startLocal.x, 0f, startLocal.z),
                new Vector3(playerLocal.x, 0f, playerLocal.z));
            bool stretchNormal = stripMode == RinkStripMode.StretchPassing && variant == StretchPassVariant.Normal;
            float estimatedSpeed = stretchNormal
                ? UnityEngine.Random.Range(24f, 58f)
                : UnityEngine.Random.Range(20f, 40f);
            float passFlightTime = estimatedDistance / Mathf.Max(estimatedSpeed, 1f);

            Vector3 predictedMovement = Vector3.zero;
            if (playerVelocity.magnitude > 0.1f)
            {
                predictedMovement = playerVelocity * passFlightTime;
                float maxPrediction;
                switch (variant)
                {
                    case StretchPassVariant.Hard:
                        maxPrediction = 3f;
                        break;
                    case StretchPassVariant.Air:
                        maxPrediction = 8f;
                        break;
                    case StretchPassVariant.Normal:
                    {
                        float speedFactor = Mathf.Clamp(playerVelocity.magnitude / 15f, 0.3f, 1f);
                        maxPrediction = 12f * speedFactor;
                        break;
                    }
                    default:
                        maxPrediction = 15f;
                        break;
                }

                if (predictedMovement.magnitude > maxPrediction)
                    predictedMovement = predictedMovement.normalized * maxPrediction;
            }

            Vector3 predictedLocal = playerLocal + predictedMovement;

            // Occasionally bank a normal stretch pass off the side glass so it
            // still arrives on the skater's stick.
            if (stretchNormal
                && UnityEngine.Random.value < StretchBankPassChance
                && TryPlanStretchBankPass(startLocal, predictedLocal, out targetLocal, out passSpeed, out maxHeight))
            {
                return true;
            }

            if (variant == StretchPassVariant.Point)
            {
                float absZ = Mathf.Abs(predictedLocal.z);
                targetLocal = new Vector3(
                    ResolveBoardsX(sides.WallSign, absZ),
                    0f,
                    predictedLocal.z + UnityEngine.Random.Range(-4f, 4f));
                targetLocal = ClampPassLocal(targetLocal);
            }
            else if (variant == StretchPassVariant.Indirect)
            {
                if (stripMode == RinkStripMode.LowCyclePassing)
                {
                    // Strong-side indirect: banked down the skater's wall below the hash marks.
                    float absZ = UnityEngine.Random.Range(
                        RinkGeometry.BlueLineZ + 12f,
                        RinkGeometry.NetZ - 2f);
                    targetLocal = new Vector3(
                        ResolveBoardsX(sides.WallSign, absZ),
                        0f,
                        sides.EndSign * absZ);
                    targetLocal = ClampPassLocal(targetLocal);
                }
                else
                {
                    targetLocal = predictedLocal + new Vector3(
                        UnityEngine.Random.Range(-3f, 3f),
                        0f,
                        UnityEngine.Random.Range(-2f, 2f));
                    if (sides.EndSign > 0f)
                        targetLocal.z = Mathf.Clamp(targetLocal.z, 35f, 47f);
                    else
                        targetLocal.z = Mathf.Clamp(targetLocal.z, -47f, -35f);
                    targetLocal = ClampPassLocal(targetLocal);
                }
            }
            else if (variant == StretchPassVariant.Rim && stripMode == RinkStripMode.LowCyclePassing)
            {
                float absZ;
                if (IsLowCycleFarSideSpawn(startLocal, playerLocal))
                {
                    // Weak-side rim: aim at the deep corner so the puck wraps the end
                    // boards behind the net and arrives on the skater's wall.
                    absZ = UnityEngine.Random.Range(
                        RinkGeometry.NetZ - 0.5f,
                        RinkGeometry.HalfLength - IceClampMargin - 1f);
                }
                else
                {
                    // Strong-side rim: die on the skater's wall down low.
                    absZ = UnityEngine.Random.Range(
                        RinkGeometry.BlueLineZ + 14f,
                        RinkGeometry.NetZ - 2f);
                }

                targetLocal = new Vector3(
                    ResolveBoardsX(sides.WallSign, absZ),
                    0f,
                    sides.EndSign * absZ);
                targetLocal = ClampPassLocal(targetLocal);
            }
            else if (variant == StretchPassVariant.Rim && stripMode == RinkStripMode.PointPassing)
            {
                // True rim: glance the boards a few metres up-ice of the deep spawn so the
                // puck contacts the wall early (near the corner) and rides it up to the
                // point. Aiming at the player's z put the first wall contact AT the point —
                // a ~7 degree diagonal that never touched the boards and read as direct.
                float spawnAbsZ = Mathf.Abs(startLocal.z);
                float contactAbsZ = Mathf.Clamp(
                    spawnAbsZ - UnityEngine.Random.Range(4f, 7f),
                    RinkGeometry.BlueLineZ + 8f,
                    RinkGeometry.HalfLength - IceClampMargin - 0.5f);
                targetLocal = new Vector3(
                    ResolveBoardsX(sides.WallSign, contactAbsZ),
                    0f,
                    sides.EndSign * contactAbsZ);
                targetLocal = ClampPassLocal(targetLocal);
            }
            else if (variant == StretchPassVariant.Rim)
            {
                float absZ = UnityEngine.Random.Range(
                    RinkGeometry.NetZ + 0.5f,
                    RinkGeometry.HalfLength - IceClampMargin - 0.5f);
                targetLocal = new Vector3(
                    ResolveBoardsX(sides.WallSign, absZ),
                    0f,
                    sides.EndSign * absZ);
                targetLocal = ClampPassLocal(targetLocal);
            }
            else if (variant == StretchPassVariant.Hard && stripMode == RinkStripMode.LowCyclePassing)
            {
                targetLocal = predictedLocal + new Vector3(
                    UnityEngine.Random.Range(-1f, 1f),
                    0f,
                    UnityEngine.Random.Range(-1f, 1f));
                targetLocal = ClampPassLocal(targetLocal);
            }
            else if (variant == StretchPassVariant.Hard)
            {
                targetLocal = predictedLocal + new Vector3(
                    UnityEngine.Random.Range(-0.5f, 0.5f),
                    0f,
                    UnityEngine.Random.Range(-0.5f, 0.5f));
                targetLocal = ClampPassLocal(targetLocal);
            }
            else
            {
                targetLocal = predictedLocal + new Vector3(
                    UnityEngine.Random.Range(-3f, 3f),
                    0f,
                    UnityEngine.Random.Range(-2f, 2f));
                targetLocal = ClampPassLocal(targetLocal);
            }

            switch (variant)
            {
                case StretchPassVariant.Hard:
                    if (stripMode == RinkStripMode.LowCyclePassing)
                    {
                        passSpeed = UnityEngine.Random.Range(54f, 72f);
                        maxHeight = UnityEngine.Random.Range(0.15f, 0.85f);
                    }
                    else
                    {
                        passSpeed = UnityEngine.Random.Range(30f, 45f);
                        maxHeight = UnityEngine.Random.Range(1f, 3f);
                    }
                    break;
                case StretchPassVariant.Soft:
                    passSpeed = UnityEngine.Random.Range(10f, 20f);
                    maxHeight = UnityEngine.Random.Range(2f, 5f);
                    break;
                case StretchPassVariant.Air:
                    if (stripMode == RinkStripMode.LowCyclePassing)
                    {
                        passSpeed = UnityEngine.Random.Range(46f, 62f);
                        maxHeight = UnityEngine.Random.Range(4.5f, 8f);
                    }
                    else
                    {
                        passSpeed = UnityEngine.Random.Range(40f, 55f);
                        maxHeight = UnityEngine.Random.Range(5f, 8f);
                    }
                    break;
                case StretchPassVariant.Point:
                    passSpeed = UnityEngine.Random.Range(36f, 50f);
                    maxHeight = UnityEngine.Random.Range(1f, 3.5f);
                    break;
                case StretchPassVariant.Rim:
                    if (stripMode == RinkStripMode.PointPassing)
                    {
                        // Hot but receivable: the early board glance costs ~20% of the pace
                        // (cos of the into-wall angle x board tangential keep), so launch a
                        // touch faster than the old shallow diagonal.
                        passSpeed = UnityEngine.Random.Range(42f, 50f);
                        maxHeight = UnityEngine.Random.Range(0f, 0.25f);
                    }
                    else if (stripMode == RinkStripMode.LowCyclePassing)
                    {
                        // Weak-side wraps travel corner + end boards and must not die en
                        // route; strong-side rims die on the skater's wall down low.
                        passSpeed = IsLowCycleFarSideSpawn(startLocal, playerLocal)
                            ? UnityEngine.Random.Range(50f, 58f)
                            : UnityEngine.Random.Range(40f, 48f);
                        maxHeight = UnityEngine.Random.Range(0.1f, 0.5f);
                    }
                    else
                    {
                        passSpeed = UnityEngine.Random.Range(35f, 50f);
                        maxHeight = UnityEngine.Random.Range(1f, 4f);
                    }
                    break;
                case StretchPassVariant.Indirect:
                    if (stripMode == RinkStripMode.LowCyclePassing)
                    {
                        passSpeed = UnityEngine.Random.Range(40f, 50f);
                        maxHeight = UnityEngine.Random.Range(0.1f, 0.5f);
                    }
                    else
                    {
                        passSpeed = UnityEngine.Random.Range(35f, 50f);
                        maxHeight = UnityEngine.Random.Range(1f, 4f);
                    }
                    break;
                default:
                    // Keep the speed the prediction was computed with — re-randomizing it
                    // here changed the flight time and made lead passes miss.
                    passSpeed = estimatedSpeed;
                    if (stretchNormal)
                    {
                        if (estimatedDistance > 48f)
                            passSpeed = Mathf.Min(passSpeed * 1.12f, 62f);
                        else if (estimatedDistance > 36f)
                            passSpeed = Mathf.Min(passSpeed * 1.06f, 58f);
                    }

                    // Vertical launch budget: the arc must land by the time the puck
                    // reaches the target, or it arrives waist-high at the skater.
                    float flightTime = estimatedDistance / Mathf.Max(passSpeed, 1f);
                    float liftBudget = 0.5f * 9.81f * flightTime * 0.9f;

                    if (stretchNormal)
                    {
                        float styleRoll = UnityEngine.Random.value;
                        if (styleRoll < 0.3f && estimatedDistance > 26f)
                            maxHeight = UnityEngine.Random.Range(2.5f, 4.5f); // saucer
                        else if (styleRoll < 0.55f)
                            maxHeight = UnityEngine.Random.Range(0.75f, 2f);
                        else
                            maxHeight = UnityEngine.Random.Range(0.15f, 1f); // flat tape-to-tape
                    }
                    else if (estimatedDistance < 30f)
                    {
                        maxHeight = UnityEngine.Random.Range(0.5f, 2f);
                    }
                    else if (estimatedDistance < 50f)
                    {
                        maxHeight = UnityEngine.Random.Range(1f, 3f);
                    }
                    else
                    {
                        maxHeight = UnityEngine.Random.Range(2f, 4f);
                    }

                    maxHeight = Mathf.Min(maxHeight, liftBudget);
                    break;
            }

            // Every variant except the intentional Air chip must land by arrival.
            if (variant != StretchPassVariant.Air)
            {
                float targetDist = Vector3.Distance(
                    new Vector3(startLocal.x, 0f, startLocal.z),
                    new Vector3(targetLocal.x, 0f, targetLocal.z));
                float landLimit = 0.5f * 9.81f * (targetDist / Mathf.Max(passSpeed, 1f)) * 0.9f;
                maxHeight = Mathf.Min(maxHeight, landLimit);
            }

            return true;
        }

        /// <summary>
        /// Boards keep tangential speed but kill most of the perpendicular component
        /// (server runs soft boards, restitution ~0.19, plus a ~15% linear cut on
        /// impact). A perfect-mirror bank therefore never comes back out to mid-ice —
        /// the puck just runs on down the wall.
        /// </summary>
        private const float BoardRestitution = 0.19f;
        private const float BoardImpactSpeedKeep = 0.85f;

        /// <summary>
        /// Restitution-aware board bank: solve the bounce point so that the *damped*
        /// reflection (outgoing normal = restitution x incoming normal, tangential
        /// preserved) points at the skater, then only accept geometries whose rebound
        /// leg still arrives with playable speed.
        /// </summary>
        private static bool TryPlanStretchBankPass(
            Vector3 startLocal,
            Vector3 predictedLocal,
            out Vector3 targetLocal,
            out float passSpeed,
            out float maxHeight)
        {
            targetLocal = default;
            passSpeed = 0f;
            maxHeight = 0f;

            // Straight glass only — a bank into the rounded corners scatters unpredictably.
            float straightWallLimitZ = RinkGeometry.HalfLength - IceCornerRadius - 1.5f;

            // Prefer the wall on the spawn dot's side; fall back to the far wall.
            float firstSign = startLocal.x >= 0f ? 1f : -1f;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                float wallSign = attempt == 0 ? firstSign : -firstSign;
                float wallX = wallSign * (RinkGeometry.HalfWidth - BoardTargetInset);

                float normalIn = wallX - startLocal.x;
                if (Mathf.Abs(normalIn) < 1f)
                    continue;

                // Outgoing ray parameter from the damped-reflection constraint:
                // (P.x - wallX) = -e * normalIn * k, (P.z - bounceZ) = tangential * k.
                float k = (predictedLocal.x - wallX) / (-BoardRestitution * normalIn);
                if (k < 0.05f)
                    continue;

                float bounceZ = (predictedLocal.z + k * startLocal.z) / (1f + k);
                if (Mathf.Abs(bounceZ) > straightWallLimitZ)
                    continue;

                Vector3 bounce = new Vector3(wallX, 0f, bounceZ);
                float legIn = Vector3.Distance(new Vector3(startLocal.x, 0f, startLocal.z), bounce);
                float legOut = Vector3.Distance(bounce, new Vector3(predictedLocal.x, 0f, predictedLocal.z));
                if (legIn < 6f || legOut < 3f || legOut > 20f)
                    continue;

                // Post-bounce speed fraction along the outgoing ray.
                float tangential = bounceZ - startLocal.z;
                float inMag = Mathf.Sqrt(normalIn * normalIn + tangential * tangential);
                if (inMag < 0.5f)
                    continue;
                float outMag = Mathf.Sqrt(
                    BoardRestitution * BoardRestitution * normalIn * normalIn
                    + tangential * tangential);
                float reboundKeep = (outMag / inMag) * BoardImpactSpeedKeep;
                if (reboundKeep < 0.18f)
                    continue;

                // Launch fast enough that the rebound leg still arrives ~10 m/s,
                // and don't let the total trip drag on.
                float speed = Mathf.Clamp(10f / reboundKeep + legIn * 0.35f, 30f, 50f);
                if (speed * reboundKeep < 6.5f)
                    continue;
                float totalTime = legIn / speed + legOut / Mathf.Max(speed * reboundKeep, 1f);
                if (totalTime > 2.4f)
                    continue;

                targetLocal = bounce;
                passSpeed = speed;
                // Dead flat — any hop off the glass scatters the damped rebound.
                maxHeight = UnityEngine.Random.Range(0.05f, 0.25f);
                return true;
            }

            return false;
        }

        private static Vector3 LocalToWorld(RinkSlot slot, Vector3 local)
        {
            float iceY = slot.Origin.y + VanillaRinkCloner.IceSurfaceY + 0.05f;
            return new Vector3(slot.Origin.x + local.x, iceY, slot.Origin.z + local.z);
        }

        private static Vector3 GetBladePosition(Player player)
        {
            try
            {
                Stick stick = player?.PlayerBody?.Stick;
                if (stick == null)
                    return default;

                // BladeHandlePosition is the HEEL (shaft joint), basically on top of the
                // skates — passes aimed there arrived in the feet. The blade collider spans
                // heel→toe, so mirroring the heel across its center gives the toe.
                Vector3 heel = stick.BladeHandlePosition;
                Collider blade = stick.StickMesh != null ? stick.StickMesh.BladeCollider : null;
                if (blade != null)
                {
                    Vector3 toe = blade.bounds.center * 2f - heel;
                    toe.y = heel.y;
                    return toe;
                }
                return heel;
            }
            catch { }

            return default;
        }

        private static void CleanupExpiredPucks()
        {
            float now = Time.time;
            var expired = new List<Puck>();

            foreach (KeyValuePair<Puck, float> pair in puckExpireAt)
            {
                if (pair.Key == null || pair.Key.gameObject == null || now >= pair.Value)
                    expired.Add(pair.Key);
            }

            for (int i = 0; i < expired.Count; i++)
                DestroyPuck(expired[i], "expire_timer");

            var staleFlying = new List<int>();
            foreach (KeyValuePair<int, Puck> pair in flyingPassPuck)
            {
                Puck p = pair.Value;
                if (p == null || p.gameObject == null)
                {
                    staleFlying.Add(pair.Key);
                    continue;
                }

                if (p.Rigidbody != null && p.Rigidbody.linearVelocity.magnitude < 0.35f
                    && puckExpireAt.TryGetValue(p, out float expireAt)
                    && now >= expireAt)
                {
                    PassLog(
                        pair.Key,
                        "flying_settled_cleanup",
                        PuckTag(p) + " vel=" + p.Rigidbody.linearVelocity.magnitude.ToString("F2")
                        + " expireAt=" + expireAt.ToString("F2"));
                    DestroyPuck(p, "flying_settled");
                    staleFlying.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleFlying.Count; i++)
                flyingPassPuck.Remove(staleFlying[i]);
        }

        private static void CleanupFlying(int rinkIndex, string reason)
        {
            if (flyingPucks.TryGetValue(rinkIndex, out List<Puck> list) && list != null)
            {
                for (int i = 0; i < list.Count; i++)
                    DestroyPuck(list[i], reason);
            }

            flyingPucks.Remove(rinkIndex);

            if (queuedPassPuck.TryGetValue(rinkIndex, out Puck queued))
            {
                DestroyPuck(queued, reason);
                queuedPassPuck.Remove(rinkIndex);
            }

            if (flyingPassPuck.TryGetValue(rinkIndex, out Puck flying))
            {
                DestroyPuck(flying, reason);
                flyingPassPuck.Remove(rinkIndex);
            }

            if (retiredPassPuck.TryGetValue(rinkIndex, out Puck retired))
            {
                DestroyPuck(retired, reason);
                retiredPassPuck.Remove(rinkIndex);
            }
        }

        private static void TrackFlying(int rinkIndex, Puck puck)
        {
            if (flyingPassPuck.TryGetValue(rinkIndex, out Puck prior) && prior != null && prior != puck)
                DestroyPuck(prior, "track_flying_replace");

            if (flyingPucks.TryGetValue(rinkIndex, out List<Puck> list) && list != null)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Puck old = list[i];
                    if (old != null && old != puck)
                        DestroyPuck(old, "track_flying_clear");
                }

                list.Clear();
            }
            else
            {
                list = new List<Puck>();
                flyingPucks[rinkIndex] = list;
            }

            list.Add(puck);
            puckExpireAt[puck] = Time.time + PassCleanupSeconds;
        }
        private static void DestroyPuck(Puck puck, string reason)
        {
            if (puck == null)
                return;

            float age = passPuckSpawnedAt.TryGetValue(puck, out float spawnedAt) ? Time.time - spawnedAt : -1f;
            PassLog(-1, "puck_destroy", PuckTag(puck) + " reason=" + reason + " age=" + age.ToString("F2"));

            puckExpireAt.Remove(puck);
            passPuckSpawnedAt.Remove(puck);

            foreach (KeyValuePair<int, List<Puck>> pair in flyingPucks)
            {
                pair.Value?.Remove(puck);
            }

            UntrackPassPuckRefs(puck);

            CustomLevelPlugin.ProtectedPucks.Remove(puck);

            PuckManager pm = MonoBehaviourSingleton<PuckManager>.Instance;
            if (pm != null)
            {
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

                try { pm.Server_DespawnPuck(puck); }
                catch { }
            }

            if (puck.gameObject != null)
            {
                try { UnityEngine.Object.Destroy(puck.gameObject); }
                catch { }
            }
        }

        private static void UntrackPassPuckRefs(Puck puck)
        {
            var flyingRinks = new List<int>();
            foreach (KeyValuePair<int, Puck> pair in flyingPassPuck)
            {
                if (pair.Value == puck)
                    flyingRinks.Add(pair.Key);
            }

            for (int i = 0; i < flyingRinks.Count; i++)
                flyingPassPuck.Remove(flyingRinks[i]);

            var queuedRinks = new List<int>();
            foreach (KeyValuePair<int, Puck> pair in queuedPassPuck)
            {
                if (pair.Value == puck)
                    queuedRinks.Add(pair.Key);
            }

            for (int i = 0; i < queuedRinks.Count; i++)
                queuedPassPuck.Remove(queuedRinks[i]);

            var retiredRinks = new List<int>();
            foreach (KeyValuePair<int, Puck> pair in retiredPassPuck)
            {
                if (pair.Value == puck)
                    retiredRinks.Add(pair.Key);
            }

            for (int i = 0; i < retiredRinks.Count; i++)
                retiredPassPuck.Remove(retiredRinks[i]);
        }

        private static void RefreshLook(int rinkIndex)
        {
            GoaliePracticeLookTarget.Publish(
                rinkIndex,
                ResolveLookPuck(rinkIndex),
                ResolveQueuedLookPuck(rinkIndex));
        }

        private static int ResolvePlayerRinkIndex(Player player)
        {
            try
            {
                int assigned = MultiRinkService.GetActiveRinkIndex(player.OwnerClientId);
                if (assigned >= 0)
                    return assigned;
            }
            catch { }

            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || player?.PlayerBody == null)
                return -1;

            return RinkLocator.NearestRink(cfg, player.PlayerBody.transform.position);
        }

        private static StretchPassVariant? ParseVariant(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg))
                return StretchPassVariant.Normal;

            switch (arg.Trim().ToLowerInvariant())
            {
                case "normal":
                case "":
                    return StretchPassVariant.Normal;
                case "hard":
                    return StretchPassVariant.Hard;
                case "soft":
                    return StretchPassVariant.Soft;
                case "air":
                case "aerial":
                    return StretchPassVariant.Air;
                case "point":
                    return StretchPassVariant.Point;
                case "rim":
                    return StretchPassVariant.Rim;
                case "indirect":
                case "low":
                case "cycle":
                    return StretchPassVariant.Indirect;
                default:
                    return null;
            }
        }

        private static string VariantLabel(StretchPassVariant variant)
        {
            switch (variant)
            {
                case StretchPassVariant.Hard: return "hard";
                case StretchPassVariant.Soft: return "soft";
                case StretchPassVariant.Air: return "air";
                case StretchPassVariant.Point: return "point";
                case StretchPassVariant.Rim: return "rim";
                case StretchPassVariant.Indirect: return "indirect";
                default: return "normal";
            }
        }

        private static MonoBehaviour CoroutineHost
        {
            get
            {
                try { return NetworkBehaviourSingleton<GameManager>.Instance; }
                catch { return null; }
            }
        }

#if PASS_SERVICE_DEBUG_LOG
        private const string PassLogTag = "[PassSvc]";

        private static void PassLog(int rinkIndex, string eventName, string detail)
        {
            string rink = rinkIndex >= 0 ? "rink=" + (rinkIndex + 1) + " " : string.Empty;
            Debug.Log(PassLogTag + " t=" + Time.time.ToString("F2") + " " + rink + eventName + " " + detail);
        }

        private static string PassStateSummary(int rinkIndex)
        {
            queuedPassPuck.TryGetValue(rinkIndex, out Puck queued);
            flyingPassPuck.TryGetValue(rinkIndex, out Puck flying);
            retiredPassPuck.TryGetValue(rinkIndex, out Puck retired);
            int cycle = passCycleNumber.TryGetValue(rinkIndex, out int n) ? n : 0;
            return "cycle=" + cycle
                + " loop=" + (loops.ContainsKey(rinkIndex) ? "Y" : "N")
                + " queued=" + PuckTag(queued)
                + " flying=" + PuckTag(flying)
                + " retired=" + PuckTag(retired);
        }

        private static float ElapsedSinceCycleStart(int rinkIndex)
        {
            return passCycleStartedAt.TryGetValue(rinkIndex, out float startedAt)
                ? Time.time - startedAt
                : -1f;
        }

        private static string PuckTag(Puck puck)
        {
            if (puck == null)
                return "puck=null";

            try { return "netId=" + puck.NetworkObjectId; }
            catch { return "puck=" + puck.GetInstanceID(); }
        }

        private static string FormatLocal(Vector3 local)
        {
            return "(" + local.x.ToString("F1") + "," + local.z.ToString("F1") + ")";
        }
#else
        private static void PassLog(int rinkIndex, string eventName, string detail) { }

        private static string PassStateSummary(int rinkIndex) => string.Empty;

        private static float ElapsedSinceCycleStart(int rinkIndex) => 0f;

        private static string PuckTag(Puck puck) => string.Empty;

        private static string FormatLocal(Vector3 local) => string.Empty;
#endif
    }
}
