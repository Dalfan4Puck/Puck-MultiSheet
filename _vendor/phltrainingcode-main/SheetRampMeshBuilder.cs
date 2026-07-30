using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural sheet ramp geometry — continuous top surface and convex wedge colliders
/// flush with rink ice at the outer edge (wheelchair-style puck ramp).
/// </summary>
public static class SheetRampMeshBuilder
{
    /// <summary>
    /// Triangular prism extruded along X. Local +Z is outward; outer foot sits at y=iceY.
    /// </summary>
    public static Mesh BuildRampWedgeMesh(float span, float run, float iceY, float deckTop)
    {
        float halfSpan = span * 0.5f;
        float halfRun = run * 0.5f;

        Vector3[] verts =
        {
            new Vector3(-halfSpan, iceY, halfRun),
            new Vector3(-halfSpan, iceY, -halfRun),
            new Vector3(-halfSpan, deckTop, -halfRun),
            new Vector3(halfSpan, iceY, halfRun),
            new Vector3(halfSpan, iceY, -halfRun),
            new Vector3(halfSpan, deckTop, -halfRun),
        };

        int[] tris =
        {
            0, 2, 5, 0, 5, 3,
            0, 4, 1, 0, 3, 4,
            1, 4, 5, 1, 5, 2,
            0, 1, 2,
            3, 5, 4,
        };

        var mesh = new Mesh { name = "SheetRampWedge" };
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// One continuous riding surface: flat deck plus four edge ramps (no deck/ramp seam).
    /// </summary>
    public static Mesh BuildSheetSurfaceMesh(SlidableObstacleSetup.SheetRampGeometry g)
    {
        float deckTop = g.DeckTop;
        float iceY = g.IceY;
        float innerW = g.HalfW - g.Run;
        float innerD = g.HalfD - g.Run;

        var verts = new List<Vector3>();
        var tris = new List<int>();

        AddQuad(verts, tris,
            new Vector3(-innerW, deckTop, -innerD),
            new Vector3(innerW, deckTop, -innerD),
            new Vector3(innerW, deckTop, innerD),
            new Vector3(-innerW, deckTop, innerD));

        AddQuad(verts, tris,
            new Vector3(-g.HalfW, deckTop, innerD),
            new Vector3(g.HalfW, deckTop, innerD),
            new Vector3(g.HalfW, iceY, g.HalfD),
            new Vector3(-g.HalfW, iceY, g.HalfD));

        AddQuad(verts, tris,
            new Vector3(-g.HalfW, deckTop, -innerD),
            new Vector3(g.HalfW, deckTop, -innerD),
            new Vector3(g.HalfW, iceY, -g.HalfD),
            new Vector3(-g.HalfW, iceY, -g.HalfD));

        AddQuad(verts, tris,
            new Vector3(innerW, deckTop, -g.HalfD),
            new Vector3(innerW, deckTop, g.HalfD),
            new Vector3(g.HalfW, iceY, g.HalfD),
            new Vector3(g.HalfW, iceY, -g.HalfD));

        AddQuad(verts, tris,
            new Vector3(-innerW, deckTop, -g.HalfD),
            new Vector3(-innerW, deckTop, g.HalfD),
            new Vector3(-g.HalfW, iceY, g.HalfD),
            new Vector3(-g.HalfW, iceY, -g.HalfD));

        var mesh = new Mesh { name = "SheetSurface" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Solid sheet body for rendering (top + sides down to ice plane).</summary>
    public static Mesh BuildSheetSolidMesh(SlidableObstacleSetup.SheetRampGeometry g)
    {
        float deckTop = g.DeckTop;
        float iceY = g.IceY;
        float innerW = g.HalfW - g.Run;
        float innerD = g.HalfD - g.Run;

        var verts = new List<Vector3>();
        var tris = new List<int>();

        Vector3 d0 = new Vector3(-innerW, deckTop, -innerD);
        Vector3 d1 = new Vector3(innerW, deckTop, -innerD);
        Vector3 d2 = new Vector3(innerW, deckTop, innerD);
        Vector3 d3 = new Vector3(-innerW, deckTop, innerD);

        Vector3 pz0 = new Vector3(-g.HalfW, deckTop, innerD);
        Vector3 pz1 = new Vector3(g.HalfW, deckTop, innerD);
        Vector3 pz2 = new Vector3(g.HalfW, iceY, g.HalfD);
        Vector3 pz3 = new Vector3(-g.HalfW, iceY, g.HalfD);

        Vector3 nz0 = new Vector3(-g.HalfW, deckTop, -innerD);
        Vector3 nz1 = new Vector3(g.HalfW, deckTop, -innerD);
        Vector3 nz2 = new Vector3(g.HalfW, iceY, -g.HalfD);
        Vector3 nz3 = new Vector3(-g.HalfW, iceY, -g.HalfD);

        Vector3 px0 = new Vector3(innerW, deckTop, -g.HalfD);
        Vector3 px1 = new Vector3(innerW, deckTop, g.HalfD);
        Vector3 px2 = new Vector3(g.HalfW, iceY, g.HalfD);
        Vector3 px3 = new Vector3(g.HalfW, iceY, -g.HalfD);

        Vector3 nx0 = new Vector3(-innerW, deckTop, -g.HalfD);
        Vector3 nx1 = new Vector3(-innerW, deckTop, g.HalfD);
        Vector3 nx2 = new Vector3(-g.HalfW, iceY, g.HalfD);
        Vector3 nx3 = new Vector3(-g.HalfW, iceY, -g.HalfD);

        AddQuad(verts, tris, d0, d1, d2, d3);
        AddQuad(verts, tris, pz0, pz1, pz2, pz3);
        AddQuad(verts, tris, nz0, nz1, nz2, nz3);
        AddQuad(verts, tris, px0, px1, px2, px3);
        AddQuad(verts, tris, nx0, nx1, nx2, nx3);

        AddQuad(verts, tris, pz3, pz2, nz2, nz3);
        AddQuad(verts, tris, px2, px3, nx3, nx2);
        AddQuad(verts, tris, pz1, pz0, nx0, nx1);
        AddQuad(verts, tris, nz1, nz0, nx3, nx1);

        AddQuad(verts, tris,
            new Vector3(-innerW, iceY, -innerD),
            new Vector3(innerW, iceY, -innerD),
            new Vector3(innerW, iceY, innerD),
            new Vector3(-innerW, iceY, innerD));

        var mesh = new Mesh { name = "SheetSolid" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddQuad(
        List<Vector3> verts,
        List<int> tris,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d)
    {
        int i = verts.Count;
        verts.Add(a);
        verts.Add(b);
        verts.Add(c);
        verts.Add(d);
        tris.Add(i);
        tris.Add(i + 1);
        tris.Add(i + 2);
        tris.Add(i);
        tris.Add(i + 2);
        tris.Add(i + 3);
    }
}
