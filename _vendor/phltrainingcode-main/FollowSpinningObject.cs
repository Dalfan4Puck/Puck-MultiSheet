using UnityEngine;

public class FollowSpinningObject : MonoBehaviour
{
    private Transform target;

    public float followSpeed = 5f;

    void Start()
    {
        // 🔥 Automatically find player in scene
        var player = GameObject.FindObjectOfType<Player>();

        if (player != null)
        {
            target = player.transform;
            Debug.Log("Player found!");
        }
        else
        {
            Debug.LogError("Player NOT found!");
        }
    }

    void Update()
    {
        if (target == null) return;

        transform.position = Vector3.MoveTowards(
            transform.position,
            target.position + new Vector3(0f, 1f, 0f),
            followSpeed * Time.deltaTime
        );
    }
}