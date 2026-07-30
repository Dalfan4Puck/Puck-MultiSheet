using UnityEngine;

/// <summary>
/// Forwards HitFace trigger contact to <see cref="PuckPasser"/> on the bumper root.
/// </summary>
public sealed class PassBumperHitRelay : MonoBehaviour
{
    public PuckPasser passer;

    private void OnTriggerEnter(Collider other)
    {
        if (passer != null)
            passer.TryPassPuck(other, transform);
    }
}
