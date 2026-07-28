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
            FlamieLog.Info("Player found!");
        }
        else
        {
            FlamieLog.Error("Player NOT found!");
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