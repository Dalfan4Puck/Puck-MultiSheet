using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Static-batched arena geometry shares combined meshes. Each MeshRenderer draws
    /// only [subMeshStartIndex, subMeshStartIndex + materialCount) with vertices
    /// already baked in world space (see puck-replay-web buildRink.ts).
    /// </summary>
    internal static class StaticBatchMeshHelper
    {
        private static PropertyInfo _subMeshStartIndexProp;

        internal static bool TryGetBatchSlice(MeshRenderer mr, out int firstSubMesh, out int subMeshCount)
        {
            firstSubMesh = 0;
            subMeshCount = 0;
            if (mr == null || !mr.isPartOfStaticBatch) return false;

            firstSubMesh = GetSubMeshStartIndex(mr);
            Material[] mats = mr.sharedMaterials;
            subMeshCount = mats != null && mats.Length > 0 ? mats.Length : 1;
            return true;
        }

        internal static Mesh BuildClientSlicedMesh(
            MeshRenderer sourceMr, Mesh sourceMesh, Transform cloneTransform, Vector3 worldOffset)
        {
            if (sourceMesh == null || cloneTransform == null) return null;

            if (sourceMr != null && sourceMr.isPartOfStaticBatch &&
                TryGetBatchSlice(sourceMr, out int first, out int count))
            {
                Mesh slice = ExtractSubmeshRange(sourceMesh, first, count);
                if (slice == null) return null;

                // Static-batch combined verts are already in world space.
                Vector3[] verts = slice.vertices;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 world = verts[i] + worldOffset;
                    verts[i] = cloneTransform.InverseTransformPoint(world);
                }

                slice.vertices = verts;
                slice.RecalculateBounds();
                slice.RecalculateNormals();
                slice.name = sourceMesh.name + "_slice_" + first;
                return slice;
            }

            if (sourceMesh.name.IndexOf("Combined Mesh", StringComparison.OrdinalIgnoreCase) >= 0 &&
                worldOffset.sqrMagnitude > 0.0001f && sourceMr != null)
            {
                Transform srcTransform = sourceMr.transform;
                Mesh dup = UnityEngine.Object.Instantiate(sourceMesh);
                Vector3[] original = sourceMesh.vertices;
                Vector3[] verts = dup.vertices;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 world = srcTransform.TransformPoint(original[i]) + worldOffset;
                    verts[i] = cloneTransform.InverseTransformPoint(world);
                }

                dup.vertices = verts;
                dup.RecalculateBounds();
                dup.RecalculateNormals();
                dup.name = sourceMesh.name + "_offset";
                return dup;
            }

            return null;
        }

        internal static Mesh BuildWorldSpaceBatchSlice(
            MeshRenderer sourceMr, Mesh sourceMesh, Vector3 worldOffset)
        {
            if (sourceMesh == null || sourceMr == null) return null;
            if (!TryGetBatchSlice(sourceMr, out int first, out int count)) return null;

            Mesh slice = ExtractSubmeshRange(sourceMesh, first, count);
            if (slice == null) return null;

            if (worldOffset.sqrMagnitude > 0.0001f)
                OffsetVerticesWorldSpace(slice, worldOffset);

            slice.name = sourceMesh.name + "_world_" + first;
            return slice;
        }

        internal static Mesh BuildClientVisualMesh(
            MeshRenderer sourceMr, Mesh sourceMesh, Transform cloneTransform, Vector3 worldOffset)
        {
            if (sourceMesh == null || cloneTransform == null) return null;

            if (sourceMr != null && sourceMr.isPartOfStaticBatch &&
                TryGetBatchSlice(sourceMr, out int first, out int count))
            {
                Mesh slice = ExtractSubmeshRange(sourceMesh, first, count);
                if (slice == null) return null;

                Vector3[] verts = slice.vertices;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 world = verts[i] + worldOffset;
                    verts[i] = cloneTransform.InverseTransformPoint(world);
                }

                slice.vertices = verts;
                slice.RecalculateBounds();
                slice.RecalculateNormals();
                slice.name = sourceMesh.name + "_slice_" + first;
                return slice;
            }

            if (sourceMesh.name.IndexOf("Combined Mesh", StringComparison.OrdinalIgnoreCase) >= 0 &&
                worldOffset.sqrMagnitude > 0.0001f && sourceMr != null)
            {
                Transform srcTransform = sourceMr.transform;
                Mesh dup = UnityEngine.Object.Instantiate(sourceMesh);
                Vector3[] original = sourceMesh.vertices;
                Vector3[] verts = dup.vertices;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 world = srcTransform.TransformPoint(original[i]) + worldOffset;
                    verts[i] = cloneTransform.InverseTransformPoint(world);
                }

                dup.vertices = verts;
                dup.RecalculateBounds();
                dup.RecalculateNormals();
                dup.name = sourceMesh.name + "_offset";
                return dup;
            }

            return null;
        }

        internal static void DuplicateMaterials(Renderer renderer)
        {
            if (renderer == null) return;

            Material[] mats = renderer.sharedMaterials;
            if (mats == null || mats.Length == 0) return;

            var dup = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++)
                dup[i] = mats[i] != null ? new Material(mats[i]) : null;
            renderer.sharedMaterials = dup;
        }

        internal static Mesh BuildCloneMesh(
            MeshRenderer srcMr, Mesh srcMesh, Transform srcTransform, Vector3 worldOffset)
        {
            if (srcMesh == null) return null;

            if (srcMr != null && srcMr.isPartOfStaticBatch &&
                TryGetBatchSlice(srcMr, out int first, out int count))
            {
                Mesh slice = ExtractSubmeshRange(srcMesh, first, count);
                if (slice == null) return null;
                if (worldOffset.sqrMagnitude > 0.0001f)
                    OffsetVerticesWorldSpace(slice, worldOffset);
                slice.name = srcMesh.name + "_batch_" + first;
                return slice;
            }

            return OffsetMeshWithTransform(srcMesh, srcTransform, worldOffset);
        }

        internal static Mesh ExtractSubmeshRange(Mesh src, int firstSubMesh, int subMeshCount)
        {
            if (src == null || subMeshCount <= 0) return null;

            int total = src.subMeshCount;
            if (total <= 0) return UnityEngine.Object.Instantiate(src);

            var triangles = new List<int>();
            for (int s = 0; s < subMeshCount; s++)
            {
                int idx = firstSubMesh + s;
                if (idx < 0 || idx >= total) continue;
                triangles.AddRange(src.GetTriangles(idx));
            }

            if (triangles.Count == 0)
                return UnityEngine.Object.Instantiate(src);

            var dst = new Mesh { name = src.name + "_sub_" + firstSubMesh };
            dst.indexFormat = src.indexFormat;
            CopyMeshChannels(src, dst);
            dst.SetTriangles(triangles, 0);
            dst.RecalculateBounds();
            return dst;
        }

        private static Mesh OffsetMeshWithTransform(Mesh srcMesh, Transform srcTransform, Vector3 worldOffset)
        {
            Mesh dup = UnityEngine.Object.Instantiate(srcMesh);
            if (worldOffset.sqrMagnitude < 0.0001f) return dup;

            Vector3[] original = srcMesh.vertices;
            Vector3[] verts = dup.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 world = srcTransform.TransformPoint(original[i]) + worldOffset;
                verts[i] = world;
            }

            dup.vertices = verts;
            dup.RecalculateBounds();
            dup.RecalculateNormals();
            dup.name = srcMesh.name + "_offset";
            return dup;
        }

        private static void OffsetVerticesWorldSpace(Mesh mesh, Vector3 worldOffset)
        {
            Vector3[] verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
                verts[i] += worldOffset;
            mesh.vertices = verts;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
        }

        private static void CopyMeshChannels(Mesh src, Mesh dst)
        {
            dst.vertices = src.vertices;
            if (src.normals != null && src.normals.Length == src.vertexCount) dst.normals = src.normals;
            if (src.tangents != null && src.tangents.Length == src.vertexCount) dst.tangents = src.tangents;
            if (src.uv != null && src.uv.Length == src.vertexCount) dst.uv = src.uv;
            if (src.uv2 != null && src.uv2.Length == src.vertexCount) dst.uv2 = src.uv2;
            if (src.colors != null && src.colors.Length == src.vertexCount) dst.colors = src.colors;
        }

        private static int GetSubMeshStartIndex(MeshRenderer mr)
        {
            try
            {
                if (_subMeshStartIndexProp == null)
                {
                    _subMeshStartIndexProp = typeof(MeshRenderer).GetProperty(
                        "subMeshStartIndex",
                        BindingFlags.Instance | BindingFlags.Public);
                }

                if (_subMeshStartIndexProp != null)
                    return (int)_subMeshStartIndexProp.GetValue(mr);
            }
            catch { }

            return 0;
        }
    }
}
