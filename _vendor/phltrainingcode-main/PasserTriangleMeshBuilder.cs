using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flat equilateral triangle pass-back bumper visual.
/// Base edge faces -local Z (toward the shooter); apex points +Z.
/// </summary>
public static class PasserTriangleMeshBuilder
{
    public struct WallSpec
    {
        public Vector3 Center;
        public Quaternion Rotation;
        public Vector3 Size;
        public Vector3 Outward;
    }

    public struct Geometry
    {
        public float EdgeLength;
        public float Height;
        public float WallThickness;
        public float Circumradius;
        public Vector3 Apex;
        public Vector3 BaseLeft;
        public Vector3 BaseRight;
        public WallSpec Wall0;
        public WallSpec Wall1;
        public WallSpec Wall2;
    }

    public static Geometry Compute(Vector3 scale)
    {
        var g = new Geometry
        {
            EdgeLength = Mathf.Max(scale.x, 0.8f),
            Height = Mathf.Max(scale.y, 0.25f),
            WallThickness = Mathf.Clamp(scale.z, 0.06f, 0.35f),
        };

        float s = g.EdgeLength;
        float r = s / Mathf.Sqrt(3f);
        g.Circumradius = r;
        g.Apex = new Vector3(0f, 0f, r);
        g.BaseLeft = new Vector3(-s * 0.5f, 0f, -r * 0.5f);
        g.BaseRight = new Vector3(s * 0.5f, 0f, -r * 0.5f);

        g.Wall0 = BuildWall(g.BaseLeft, g.BaseRight, g.Height, g.WallThickness);
        g.Wall1 = BuildWall(g.BaseRight, g.Apex, g.Height, g.WallThickness);
        g.Wall2 = BuildWall(g.Apex, g.BaseLeft, g.Height, g.WallThickness);
        return g;
    }

    public static WallSpec GetWall(in Geometry g, int edge) =>
        edge switch
        {
            1 => g.Wall1,
            2 => g.Wall2,
            _ => g.Wall0,
        };

    private static WallSpec BuildWall(Vector3 a, Vector3 b, float height, float thickness)
    {
        Vector3 edgeDir = b - a;
        float length = edgeDir.magnitude;
        Vector3 mid = (a + b) * 0.5f;
        Vector3 outward = OutwardFromEdge(a, b, mid);

        return new WallSpec
        {
            Center = mid + outward * (thickness * 0.5f),
            Rotation = Quaternion.LookRotation(outward, Vector3.up),
            Size = new Vector3(length, height, thickness),
            Outward = outward,
        };
    }

    private static Vector3 OutwardFromEdge(Vector3 a, Vector3 b, Vector3 mid)
    {
        Vector3 edgeDir = (b - a).normalized;
        Vector3 candidate = Vector3.Cross(Vector3.up, edgeDir);
        if (Vector3.Dot(candidate, -mid) < 0f)
            candidate = -candidate;
        return candidate.normalized;
    }

    public static Mesh BuildFrameMesh(in Geometry g) => BuildSolidMesh(g);

    /// <summary>Single flat equilateral triangle lying on the ice plane.</summary>
    public static Mesh BuildSolidMesh(in Geometry g)
    {
        const float lift = 0.008f;
        Vector3 apex = Lift(g.Apex, lift);
        Vector3 baseLeft = Lift(g.BaseLeft, lift);
        Vector3 baseRight = Lift(g.BaseRight, lift);

        var verts = new List<Vector3>(6);
        var tris = new List<int>(6);

        AddTriangle(verts, tris, baseLeft, apex, baseRight);
        AddTriangle(verts, tris, baseRight, apex, baseLeft);

        var mesh = new Mesh { name = "PasserTriangleFlat" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddTriangle(
        List<Vector3> verts,
        List<int> tris,
        Vector3 a,
        Vector3 b,
        Vector3 c)
    {
        int i = verts.Count;
        verts.Add(a);
        verts.Add(b);
        verts.Add(c);
        tris.Add(i);
        tris.Add(i + 1);
        tris.Add(i + 2);
    }

    private static Vector3 Lift(Vector3 v, float y) => new Vector3(v.x, y, v.z);
}
