using Unity.Netcode;
using UnityEngine;

public class CircularMovingTarget : MonoBehaviour
{
    private Vector3 startPosition;
    [Header("Movement Settings")]
    public float moveRange = 1.45f;      // How far left/right from start
    public float moveSpeed = 2f;      // Sideways speed
    private int direction = 1;
    public float minHeight = 0.5f;  // lowest possible height
    public float maxHeight = 1.5f;    // highest possible height

    private TrainingObjectManager manager;

    /// <summary>False on remote clients — pose comes from <see cref="TrainingMotionSync"/>.</summary>
    public bool simulateLocally = true;

    [Header("Visual Settings")]
    public float diameter = 0.5f;       // Size of circle
    public float thickness = 0.03f;   // Thin cylinder

    public void Init(Vector3 position, TrainingObjectManager mgr)
    {
        startPosition = position;
        transform.position = position;
        manager = mgr;

        CreateRing(0.2f, Color.red, -0.02f);    // center
        CreateRing(0.4f, Color.white, -0.01f);  // middle
        CreateRing(0.8f, Color.blue, 0f);   // outer

        // Collider for puck detection
        var col = gameObject.AddComponent<SphereCollider>();
        col.isTrigger = true;
        col.radius = 0.7f;
    }

    private void Update()
    {
        if (simulateLocally)
        {
            // Sideways movement only
            Vector3 pos = transform.position;
            pos.x += direction * moveSpeed * Time.deltaTime;
            if (Mathf.Abs(pos.x - startPosition.x) > moveRange)
                direction *= -1;
            transform.position = pos;
        }

        // Puck hits are server-authoritative.
        if (!IsServerSide())
            return;

        foreach (var puck in GameObject.FindObjectsByType<Puck>(FindObjectsSortMode.None))
        {
            if (Vector3.Distance(puck.transform.position, transform.position) < 0.7f)
                OnHit();
        }
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

    private void OnHit()
{
    if (manager == null) return;

    Vector3 newPos = transform.position;

    // random sideways movement
    newPos.x = startPosition.x + Random.Range(-moveRange, moveRange);

    // random height
    newPos.y = Random.Range(minHeight, maxHeight);

    transform.position = newPos;

    Debug.Log($"Target moved to new position: {newPos}");
}
    void CreateRing(float size, Color color, float offset)
{
    GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);

    ring.transform.parent = transform;
    ring.transform.localPosition = new Vector3(0f, 0f, offset);
//    ring.transform.localPosition = Vector3.zero;
    ring.transform.localScale = new Vector3(size, thickness, size);
    ring.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

    var renderer = ring.GetComponent<Renderer>();
    renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
    renderer.material.color = color;

    Destroy(ring.GetComponent<Collider>()); // we only want the main collider
}
}