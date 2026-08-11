using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// CompTweaksUnlimited scales arena geometry from <see cref="Level.Bounds.center"/>.
    /// MultiSheet expands those bounds to the whole 3×2 grid (~64,64), so CTU pulls goals,
    /// spawns, and clone visuals toward the grid center instead of each rink origin.
    /// Patches CTU to use per-rink scale pivots (nearest <see cref="RinkSlot.Origin"/>).
    /// </summary>
    internal static class CtuArenaMultiRinkCompat
    {
        private const string ArenaSyncTypeName = "CompetitivePuckTweaks.src.ArenaUniformScaleRuntimeSync";
        private const string ArenaDiagnosticsTypeName = "CompetitivePuckTweaks.src.ArenaResizeDiagnostics";
        private const string StaticBatchHelperTypeName = "CompetitivePuckTweaks.src.StaticBatchMeshHelper";
        private const string VisualProxyTypeName = "CompetitivePuckTweaks.src.ArenaScaledVisualProxy";
        private const string GoalNetVisualSyncTypeName = "CompetitivePuckTweaks.src.GoalNetVisualSync";
        private const string GoalFrameBundledMeshTypeName = "CompetitivePuckTweaks.src.GoalFrameBundledMesh";

        private static Type _arenaSyncType;
        private static Type _goalNetVisualSyncType;
        private static MethodInfo _getOrCaptureWorld;
        private static MethodInfo _applyScaledLocalScale;
        private static MethodInfo _isIdentityScale;
        private static MethodInfo _getGoalAssemblyRoot;
        private static MethodInfo _resetGoalBaselines;
        private static MethodInfo _markGoalBaselinesReady;
        private static MethodInfo _applyAllGoals;
        private static FieldInfo _centerIceField;
        private static FieldInfo _centerIceResolvedField;
        private static Transform _cachedServerCloneRoot;
        private static Transform _cachedClientCloneRoot;
        private static float _lastNotifyUnscaledTime = -999f;
        private static bool _typesResolved;
        private static bool _loggedInstall;

        [ThreadStatic] private static Transform _goalCenterRoot;

        internal static bool IsActive()
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            return cfg != null && cfg.EnableMultiRink && cfg.Rinks != null && cfg.Rinks.Count > 1;
        }

        internal static Vector3 ResolveScaleCenter(Vector3 worldPosition)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0)
                return worldPosition;

            int idx = RinkLocator.NearestRink(cfg, worldPosition);
            RinkSlot slot = cfg.Rinks[idx];
            return slot != null ? slot.Origin : Vector3.zero;
        }

        /// <summary>
        /// CTU ships goal-frame vertices in rink-1 absolute world space; offset clones by rink origin delta.
        /// </summary>
        internal static Vector3 GetRinkWorldOffset(Vector3 worldPosition)
        {
            MultiRinkConfig cfg = MultiRinkConfig.Current;
            if (cfg?.Rinks == null || cfg.Rinks.Count == 0)
                return Vector3.zero;

            Vector3 primary = cfg.Rinks[0].Origin;
            return ResolveScaleCenter(worldPosition) - primary;
        }

        private static Mesh TryBuildOffsetGoalFrameMesh(Transform goalFrame, bool mirrorForRedGoal, Vector3 rinkOffset)
        {
            Type meshHelperType = AccessTools.TypeByName(GoalFrameBundledMeshTypeName);
            MethodInfo tryGetTemplate = AccessTools.Method(meshHelperType, "TryGetTemplate");
            if (tryGetTemplate == null || goalFrame == null)
                return null;

            object template = tryGetTemplate.Invoke(null, null);
            if (template == null)
                return null;

            Type templateType = template.GetType();
            FieldInfo verticesField = templateType.GetField("Vertices", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo normalsField = templateType.GetField("Normals", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo trianglesField = templateType.GetField("Triangles", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (verticesField == null || trianglesField == null)
                return null;

            Vector3[] templateVertices = verticesField.GetValue(template) as Vector3[];
            Vector3[] templateNormals = normalsField?.GetValue(template) as Vector3[];
            int[] templateTriangles = trianglesField.GetValue(template) as int[];
            if (templateVertices == null || templateVertices.Length == 0 || templateTriangles == null)
                return null;

            int vertexCount = templateVertices.Length;
            var localPositions = new Vector3[vertexCount];
            var localNormals = new Vector3[vertexCount];

            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 world = templateVertices[i];
                Vector3 worldNormal = templateNormals != null && i < templateNormals.Length
                    ? templateNormals[i]
                    : Vector3.up;

                if (mirrorForRedGoal)
                {
                    world.x = -world.x;
                    world.z = -world.z;
                    worldNormal.x = -worldNormal.x;
                    worldNormal.z = -worldNormal.z;
                }

                world += rinkOffset;

                if (ShouldBakeArenaScaleIntoGoalFrameVertices())
                {
                    Vector3 pivot = ResolveScaleCenter(world);
                    Vector3 arenaScale = GetEffectiveArenaScale();
                    world = ScalePointFromCenter(pivot, world, arenaScale);
                }

                localPositions[i] = goalFrame.InverseTransformPoint(world);
                localNormals[i] = goalFrame.InverseTransformDirection(worldNormal.normalized);
            }

            var mesh = new Mesh { name = "CTU Goal Frame (local, multi-rink)" };
            mesh.SetVertices(localPositions);
            mesh.SetNormals(localNormals);
            mesh.SetTriangles(templateTriangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        internal static void NotifyMultiRinkLayoutReady()
        {
            if (!IsActive()) return;

            // FinishLayout + OnLevelAwake both used to call this — debounce one frame hitch.
            float now = Time.unscaledTime;
            if (now - _lastNotifyUnscaledTime < 0.25f) return;
            _lastNotifyUnscaledTime = now;

            CacheCloneRoots();
            ModPatchInstaller.TryInstallDeferredCtuCompat(null);
            if (!EnsureTypes()) return;

            try
            {
                _resetGoalBaselines?.Invoke(null, null);
                _markGoalBaselinesReady?.Invoke(null, null);

                MethodInfo resetFrameCache = AccessTools.Method(
                    AccessTools.TypeByName(GoalFrameBundledMeshTypeName), "ResetCache");
                resetFrameCache?.Invoke(null, null);

                MethodInfo force = AccessTools.Method(_arenaSyncType, "ForceReapplyCurrentScale");
                bool arenaApplied = force != null && force.Invoke(null, null) is bool applied && applied;

                if (arenaApplied)
                {
                    PracticeLog.Info("[PHLPractice] CTU arena/goals re-applied after multi-rink layout.");
                }
            }
            catch (Exception ex)
            {
                PracticeLog.Info("[PHLPractice] CTU arena re-apply skipped: " + ex.Message);
            }
        }

        private static bool EnsureTypes()
        {
            if (_typesResolved) return _arenaSyncType != null;

            _typesResolved = true;
            _arenaSyncType = AccessTools.TypeByName(ArenaSyncTypeName);
            _goalNetVisualSyncType = AccessTools.TypeByName(GoalNetVisualSyncTypeName);
            if (_arenaSyncType == null) return false;

            _getOrCaptureWorld = AccessTools.Method(_arenaSyncType, "GetOrCaptureWorld", new[] { typeof(Transform) });
            _applyScaledLocalScale = AccessTools.Method(_arenaSyncType, "ApplyScaledLocalScale", new[] { typeof(Transform), typeof(Vector3) });
            _isIdentityScale = AccessTools.Method(_arenaSyncType, "IsIdentityScale", new[] { typeof(Vector3) });
            _centerIceField = AccessTools.Field(_arenaSyncType, "_centerIce");
            _centerIceResolvedField = AccessTools.Field(_arenaSyncType, "_centerIceResolved");

            if (_goalNetVisualSyncType != null)
            {
                _getGoalAssemblyRoot = AccessTools.Method(_goalNetVisualSyncType, "GetAssemblyRoot");
                _resetGoalBaselines = AccessTools.Method(_goalNetVisualSyncType, "ResetBaselines");
                _markGoalBaselinesReady = AccessTools.Method(_goalNetVisualSyncType, "MarkLevelBaselinesReady");
                _applyAllGoals = AccessTools.Method(_goalNetVisualSyncType, "ApplyAllGoals");
            }

            if (!_loggedInstall)
            {
                _loggedInstall = true;
                PracticeLog.Info("[PHLPractice] CTU multi-rink arena compat active.");
            }

            return _getOrCaptureWorld != null && _applyScaledLocalScale != null && _isIdentityScale != null;
        }

        private static bool IsIdentityScale(Vector3 scale)
        {
            if (!EnsureTypes()) return scale == Vector3.one;
            return (bool)_isIdentityScale.Invoke(null, new object[] { scale });
        }

        private static Vector3 ReadBaselinePosition(object baseline)
        {
            if (baseline == null) return Vector3.zero;
            FieldInfo pos = baseline.GetType().GetField("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return pos != null ? (Vector3)pos.GetValue(baseline) : Vector3.zero;
        }

        private static Quaternion ReadBaselineRotation(object baseline)
        {
            if (baseline == null) return Quaternion.identity;
            FieldInfo rot = baseline.GetType().GetField("Rotation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return rot != null ? (Quaternion)rot.GetValue(baseline) : Quaternion.identity;
        }

        private static Vector3 ScalePointFromCenter(Vector3 center, Vector3 baselineWorld, Vector3 scale)
        {
            Vector3 offset = baselineWorld - center;
            return center + new Vector3(
                offset.x * scale.x,
                offset.y * scale.y,
                offset.z * scale.z);
        }

        private static void ApplyScaledWorldTransformCompat(Transform t, Vector3 scale)
        {
            if (t == null || !EnsureTypes()) return;

            object baseline = _getOrCaptureWorld.Invoke(null, new object[] { t });
            Vector3 basePos = ReadBaselinePosition(baseline);
            Quaternion baseRot = ReadBaselineRotation(baseline);

            if (IsIdentityScale(scale))
            {
                t.SetPositionAndRotation(basePos, baseRot);
                return;
            }

            Vector3 center = ResolveScaleCenter(basePos);
            t.SetPositionAndRotation(ScalePointFromCenter(center, basePos, scale), baseRot);
        }

        private static Vector3 GetEffectiveArenaScale()
        {
            MethodInfo getScale = AccessTools.Method(_arenaSyncType ?? AccessTools.TypeByName(ArenaSyncTypeName), "GetEffectiveScaleMultipliers");
            if (getScale == null) return Vector3.one;
            return getScale.Invoke(null, null) is Vector3 scale ? scale : Vector3.one;
        }

        /// <summary>
        /// When CTU nets do not stack with arena scale, bake arena size into bundled frame verts for clone rinks.
        /// When stacking is on, <see cref="GoalNetVisualSync"/> scales the assembly root instead.
        /// </summary>
        private static bool ShouldBakeArenaScaleIntoGoalFrameVertices()
        {
            Type pluginCore = AccessTools.TypeByName("CompetitivePuckTweaks.src.PluginCore");
            FieldInfo cfgField = pluginCore != null ? AccessTools.Field(pluginCore, "config") : null;
            object cfg = cfgField?.GetValue(null);
            if (cfg == null) return false;

            PropertyInfo stackProp = cfg.GetType().GetProperty("ScaleNetWithArenaChanges");
            if (stackProp != null && stackProp.GetValue(cfg) is bool stacksWithArena && stacksWithArena)
                return false;

            return !IsIdentityScale(GetEffectiveArenaScale());
        }

        internal static void ClearCloneRootCache()
        {
            _cachedServerCloneRoot = null;
            _cachedClientCloneRoot = null;
            _lastNotifyUnscaledTime = -999f;
        }

        private static void CacheCloneRoots()
        {
            if (_cachedServerCloneRoot == null)
            {
                GameObject serverRoot = GameObject.Find("PHL_VanillaMultiRink_Server");
                _cachedServerCloneRoot = serverRoot != null ? serverRoot.transform : null;
            }

            if (_cachedClientCloneRoot == null)
            {
                GameObject clientRoot = GameObject.Find("PHL_VanillaMultiRink_Client");
                _cachedClientCloneRoot = clientRoot != null ? clientRoot.transform : null;
            }
        }

        private static bool IsCloneGeometryRootName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // Goals are scaled/repositioned by GoalNetVisualSync.ApplyAllGoals — not localScale here.
            return name.StartsWith("Rink_", StringComparison.Ordinal);
        }

        private static void ApplyScaleToCloneRinkRoots(Vector3 scale)
        {
            if (!EnsureTypes()) return;

            CacheCloneRoots();
            if (_cachedServerCloneRoot != null)
                ApplyScaleUnder(_cachedServerCloneRoot, scale);
            if (_cachedClientCloneRoot != null)
                ApplyScaleUnder(_cachedClientCloneRoot, scale);

            CloneVisualProxy.ApplyArenaScale(scale);
        }

        private static void ApplyScaleUnder(Transform root, Vector3 scale)
        {
            if (root == null) return;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null || !IsCloneGeometryRootName(child.name)) continue;

                _applyScaledLocalScale.Invoke(null, new object[] { child, scale });
            }
        }

        [HarmonyPatch]
        private static class ResolveCenterIcePatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(_arenaSyncType ?? AccessTools.TypeByName(ArenaSyncTypeName), "ResolveCenterIce");

            static void Postfix()
            {
                if (!IsActive() || !EnsureTypes()) return;

                Vector3 center = Vector3.zero;
                if (MinimapRinkView.TryGetVanillaCenter(out Vector3 vanilla))
                    center = vanilla;
                else if (MultiRinkConfig.Current.Rinks[0] != null)
                    center = MultiRinkConfig.Current.Rinks[0].Origin;

                _centerIceField?.SetValue(null, center);
                _centerIceResolvedField?.SetValue(null, true);
            }
        }

        [HarmonyPatch]
        private static class GetCenterIcePatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(_arenaSyncType ?? AccessTools.TypeByName(ArenaSyncTypeName), "GetCenterIce");

            static void Postfix(ref Vector3 __result)
            {
                if (!IsActive()) return;
                if (_goalCenterRoot != null)
                    __result = ResolveScaleCenter(_goalCenterRoot.position);
            }
        }

        [HarmonyPatch]
        private static class GoalApplyFromConfigPatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(_goalNetVisualSyncType ?? AccessTools.TypeByName(GoalNetVisualSyncTypeName), "ApplyFromConfig");

            static void Prefix(Goal goal)
            {
                if (!IsActive() || goal == null || _getGoalAssemblyRoot == null) return;
                _goalCenterRoot = _getGoalAssemblyRoot.Invoke(null, new object[] { goal }) as Transform;
            }

            static void Finalizer()
            {
                _goalCenterRoot = null;
            }
        }

        [HarmonyPatch]
        private static class GetOpeningTowardCenterWorldPatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(_goalNetVisualSyncType ?? AccessTools.TypeByName(GoalNetVisualSyncTypeName), "GetOpeningTowardCenterWorld");

            static void Postfix(Transform assemblyRoot, ref Vector3 __result)
            {
                if (!IsActive() || assemblyRoot == null) return;

                Vector3 toRinkCenter = ResolveScaleCenter(assemblyRoot.position) - assemblyRoot.position;
                toRinkCenter.y = 0f;
                if (toRinkCenter.sqrMagnitude < 1e-6f) return;
                __result = toRinkCenter.normalized;
            }
        }

        [HarmonyPatch]
        private static class ScalePointFromCenterPatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(ArenaDiagnosticsTypeName), "ScalePointFromCenter");

            static bool Prefix(Vector3 centerIce, Vector3 baselineWorld, Vector3 scale, ref Vector3 __result)
            {
                if (!IsActive()) return true;
                __result = ScalePointFromCenter(ResolveScaleCenter(baselineWorld), baselineWorld, scale);
                return false;
            }
        }

        [HarmonyPatch]
        private static class ApplyScaledWorldTransformPatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(_arenaSyncType ?? AccessTools.TypeByName(ArenaSyncTypeName), "ApplyScaledWorldTransform");

            static bool Prefix(Transform t, Vector3 scale)
            {
                if (!IsActive()) return true;
                ApplyScaledWorldTransformCompat(t, scale);
                return false;
            }
        }

        [HarmonyPatch]
        private static class ApplyFromConfigPostfixPatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(_arenaSyncType ?? AccessTools.TypeByName(ArenaSyncTypeName), "ApplyFromConfig");

            static void Postfix(bool __result)
            {
                if (!__result || !IsActive() || _applyAllGoals == null || !EnsureTypes()) return;
                _applyAllGoals.Invoke(null, null);
            }
        }

        [HarmonyPatch]
        private static class ScaleGeometryRootsPatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(_arenaSyncType ?? AccessTools.TypeByName(ArenaSyncTypeName), "ScaleGeometryRoots");

            static void Postfix(Vector3 scale)
            {
                if (!IsActive()) return;
                ApplyScaleToCloneRinkRoots(scale);
            }
        }

        [HarmonyPatch]
        private static class BuildScaledLocalMeshPatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(StaticBatchHelperTypeName), "BuildScaledLocalMesh");

            static void Prefix(MeshRenderer renderer, ref Vector3 centerIce)
            {
                if (!IsActive() || renderer == null) return;
                centerIce = ResolveScaleCenter(renderer.bounds.center);
            }
        }

        /// <summary>
        /// CTU also submits in beginCameraRendering — the LateUpdate all-cameras loop is redundant
        /// and doubles GPU proxy work on scaled rinks.
        /// </summary>
        [HarmonyPatch]
        private static class CtuVisualProxyLateUpdateSkipPatch
        {
            static MethodBase TargetMethod()
            {
                Type proxyType = AccessTools.TypeByName(VisualProxyTypeName);
                return proxyType?.GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }

            static bool Prefix()
            {
                return !IsActive();
            }
        }

        /// <summary>
        /// Fix CTU proxy pivot at apply time so native Submit stays fast (no per-frame reflection loop).
        /// </summary>
        [HarmonyPatch]
        private static class VisualProxyApplyPatch
        {
            static MethodBase TargetMethod()
            {
                Type proxyType = AccessTools.TypeByName(VisualProxyTypeName);
                return proxyType?.GetMethod(
                    "ApplyInternal",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            static void Prefix(ref Vector3 centerIce)
            {
                if (!IsActive()) return;

                if (MinimapRinkView.TryGetVanillaCenter(out Vector3 vanilla))
                    centerIce = vanilla;
                else if (MultiRinkConfig.Current?.Rinks?[0] != null)
                    centerIce = MultiRinkConfig.Current.Rinks[0].Origin;
            }
        }

        /// <summary>
        /// CTU goal-frame JSON is authored in rink-1 world space; without an offset every clone draws on rink 1.
        /// </summary>
        [HarmonyPatch]
        private static class GoalFrameBundledMeshPatch
        {
            static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName(GoalFrameBundledMeshTypeName), "CreateLocalMesh");

            static bool Prefix(Transform goalFrame, bool mirrorForRedGoal, ref Mesh __result)
            {
                if (!IsActive() || goalFrame == null) return true;

                Vector3 rinkOffset = GetRinkWorldOffset(goalFrame.position);
                if (rinkOffset.sqrMagnitude < 0.0001f) return true;

                __result = TryBuildOffsetGoalFrameMesh(goalFrame, mirrorForRedGoal, rinkOffset);
                return __result == null;
            }
        }
    }
}
