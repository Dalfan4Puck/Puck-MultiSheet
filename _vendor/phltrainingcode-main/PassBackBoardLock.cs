using UnityEngine;

/// <summary>
/// Marks a pass-back board as pose-locked. Cleans up puck-ignore registration and the
/// optional client <c>PassBackAnchor_*</c> ownership root on destroy.
/// </summary>
public sealed class PassBackBoardLock : MonoBehaviour
{
    public GameObject ownedAnchor;

    private void OnDestroy()
    {
        SlidablePuckFilter.Unregister(gameObject);

        if (ownedAnchor == null)
            return;

        GameObject anchor = ownedAnchor;
        ownedAnchor = null;
        if (anchor != null)
            Destroy(anchor);
    }
}
