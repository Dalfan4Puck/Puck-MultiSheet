using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using MyMod;
using Unity.Netcode;

public class PuckSpawner : MonoBehaviour
{
    private bool initialized = false;
    private Rigidbody playerBody;
    private Rigidbody puckBody;
    private GameObject reactiveTarget;
    public float targetDistance = 8f;
    public float targetMoveRange = 4f;
    public int incomingNumberOfPucks = 15;      
    public float incomingSpawnDistance = 10f;  
    public float incomingSpawnInterval = 0.5f; 
    public float incomingPuckSpeed = 8f;       
    public float incomingSpawnHeight = 0.4f;
    public int countReactiveTargets = 0;      
    public int maxReactiveTargets = 10;      

    void Update()
    {
        if (!TrainingObjectManager.IsModEnabled)
            return;// Middle mouse click

        if (Mouse.current.middleButton.wasPressedThisFrame)
        {
            SpawnPuckInFront();
        }
        /*if (Keyboard.current.vKey.wasPressedThisFrame)
        {
            PassPuckToPlayer();
        }
        // B key starts incoming puck drill
        if (Keyboard.current.bKey.wasPressedThisFrame)
        {
            StartCoroutine(SpawnIncomingPucks());
            Debug.Log("Incoming puck drill started!");
        }
    //    if (Keyboard.current.cKey.wasPressedThisFrame)
    //    {
    //        StartReactivePassingDrill();
    //    }
        if (Keyboard.current.xKey.wasPressedThisFrame)
        {
            LobPassToPlayer();
        }*/
    }

    void InitializeIfNeeded()
    {
        if (playerBody == null)
        {
            playerBody = GameObject.FindFirstObjectByType<Player>()?.PlayerBody?.GetComponent<Rigidbody>();
        }

        if (puckBody == null)
        {
            puckBody = GameObject.FindFirstObjectByType<Puck>()?.GetComponent<Rigidbody>();
        }

        initialized = (playerBody != null && puckBody != null);
    }

    void SpawnPuckInFront()
{
    InitializeIfNeeded();
    if (!initialized) return;

    Vector3 forward = playerBody.transform.forward;
    forward.y = 0f;
    forward.Normalize();

    // 🔥 FORCE SPAWN IN AIR (ignore ice completely)
    Vector3 spawnPos = playerBody.position + forward * 2f + Vector3.up * 0.4f;

    puckBody.position = spawnPos;

    puckBody.linearVelocity = playerBody.linearVelocity;
    puckBody.angularVelocity = Vector3.zero;

    Debug.Log("TEST: Puck spawned in air");
}
        void PassPuckToPlayer()
    {
          InitializeIfNeeded();
          if (!initialized) return;

          Vector3 forward = playerBody.transform.forward;
          forward.y = 0f;
          forward.Normalize();

           // Spawn puck 6 meters in front of player
         Vector3 spawnPos = playerBody.position + forward * 9f;

         // Snap to ice
          if (Physics.Raycast(spawnPos + Vector3.up, Vector3.down, out RaycastHit hit, 10f))
          {
              spawnPos = hit.point;
          }

         spawnPos.y += 0.2f;

            puckBody.position = spawnPos;

            // Send puck toward player
            Vector3 directionToPlayer = (playerBody.position - spawnPos).normalized;

            float passSpeed = 12f; // adjust if needed
            puckBody.linearVelocity = directionToPlayer * passSpeed;

            puckBody.angularVelocity = Vector3.zero;

            Debug.Log("Pass coming toward player");
        }
        IEnumerator SpawnIncomingPucks()
{
    InitializeIfNeeded();
    if (!initialized) yield break;

    for (int i = 0; i < incomingNumberOfPucks; i++)
    {
        // Create a new puck instance
        GameObject newPuck = GameObject.Instantiate(puckBody.gameObject);
        Rigidbody newPuckRb = newPuck.GetComponent<Rigidbody>();

        // Random lateral offset
        Vector3 offset = new Vector3(Random.Range(-4f, 4f), 0f, 0f);
        Vector3 spawnPos = playerBody.position + playerBody.transform.forward * incomingSpawnDistance + offset;
        spawnPos.y = incomingSpawnHeight;

        newPuck.transform.position = spawnPos;

        // Send puck toward player
        Vector3 directionToPlayer = (playerBody.position - spawnPos).normalized;
        newPuckRb.linearVelocity = directionToPlayer * incomingPuckSpeed;
        newPuckRb.angularVelocity = Vector3.zero;

        // Destroy the puck automatically after 5 seconds
        Destroy(newPuck, 4f);

        Debug.Log($"Incoming puck #{i + 1} spawned toward player!");

        yield return new WaitForSeconds(incomingSpawnInterval);
    }
}
/*void StartReactivePassingDrill()
{
    InitializeIfNeeded();
    if (!initialized) return;

    if (reactiveTarget == null && countReactiveTargets < maxReactiveTargets)
    {
        countReactiveTargets++;
        
        reactiveTarget = GameObject.CreatePrimitive(PrimitiveType.Cylinder);

        // Set initial position on the ice
        reactiveTarget.transform.position = new Vector3(0, 0.05f, 0);

        // Make it tall enough so puck always hits it
        reactiveTarget.transform.localScale = new Vector3(1f, 0.3f, 1f);

        Renderer r = reactiveTarget.GetComponent<Renderer>();
        r.material.color = Color.red;

        reactiveTarget.GetComponent<Collider>().isTrigger = true;

        reactiveTarget.AddComponent<ReactiveTarget>().spawner = this;
    }

    MoveTarget();
}*/

public void MoveTarget()
{
    Vector3 forward = playerBody.transform.forward;
    forward.y = 0;
    forward.Normalize();

    Vector3 basePos = playerBody.position + forward * targetDistance;

    float randomX = Random.Range(-targetMoveRange, targetMoveRange);
    float randomZ = Random.Range(-targetMoveRange, targetMoveRange);

    Vector3 newPos = basePos + new Vector3(randomX, 0, randomZ);
    newPos.y = 0.05f;

    reactiveTarget.transform.position = newPos;
}
void LobPassToPlayer()
{
    InitializeIfNeeded();
    if (!initialized) return;

    Vector3 forward = playerBody.transform.forward;
    forward.y = 0f;
    forward.Normalize();

    // Random left/right spawn
    Vector3 sideOffset = playerBody.transform.right * Random.Range(-8f, 8f);

    // Spawn 20m behind player
    Vector3 spawnPos = playerBody.position - forward * 20f + sideOffset;
    spawnPos.y = 1.2f;

    puckBody.position = spawnPos;

    // Predict where player is going
    Vector3 predictedPosition = playerBody.position + playerBody.linearVelocity * 2f;

    Vector3 direction = (predictedPosition - spawnPos).normalized;

    float forwardSpeed = 25f;
    float upwardSpeed = 13f;

    puckBody.linearVelocity = direction * forwardSpeed + Vector3.up * upwardSpeed;
    puckBody.angularVelocity = Vector3.zero;

    Debug.Log("Lob pass spawned!");
}

}
