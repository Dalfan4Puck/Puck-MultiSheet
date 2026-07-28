/*using UnityEngine;

public class StaticCircularTarget : MonoBehaviour
{
    private TrainingObjectManager manager;

    public void Init(Vector3 position, TrainingObjectManager mgr)
    {
        manager = mgr;
        transform.position = position;

        // Create visual rings
        CreateRing(0.2f, Color.red, -0.02f);   // center
        CreateRing(0.4f, Color.white, -0.01f); // middle
        CreateRing(0.8f, Color.blue, 0f);      // outer

        // Collider for puck hits
        var col = gameObject.AddComponent<SphereCollider>();
        col.isTrigger = true;
        col.radius = 0.7f;
    }

   void CreateRing(float radius, Color color, float offset)
{
    GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
    ring.transform.parent = transform;

    // Move slightly toward the camera (or wall)
    ring.transform.localPosition = new Vector3(0f, 0f, offset);

    // Rotate flat to face forward
    ring.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

    // Proper circular scale: X = radius, Y = thickness, Z = radius
    ring.transform.localScale = new Vector3(radius, 0.01f, radius);

    var renderer = ring.GetComponent<Renderer>();
    renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
    renderer.material.color = color;

    Destroy(ring.GetComponent<Collider>());
}

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Puck"))
        {
            FlamieLog.Info("[StaticCircularTarget] Hit by puck!");
            if (manager != null)
            {
                // Optional: move or give feedback
                transform.position += transform.forward * -0.2f;
            }
        }
    }
}*/