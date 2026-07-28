using UnityEngine;

public class ReactiveTarget : MonoBehaviour
{
    public PuckSpawner spawner;
    private Transform puck;
    private float hitCooldown = 0.3f;
    private float lastHitTime = -1f;


    void Start()
    {
        // Find the puck in the scene
        Puck p = GameObject.FindFirstObjectByType<Puck>();
        if (p != null)
            puck = p.transform;
    }

    void Update()
    {
        if (puck == null) return;

        float distance = Vector3.Distance(transform.position, puck.position);

        // Detect puck near target with cooldown
        if (distance < 0.8f && Time.time > lastHitTime + hitCooldown)
        {
            lastHitTime = Time.time;

            FlamieLog.Info("Puck hit target!");
            spawner.MoveTarget();
        }
    }
    
}