using UnityEngine;

/// <summary>Drives UITK radio HUD attach/refresh and tears down on mod disable.</summary>
public sealed class RadioHudDriver : MonoBehaviour
{
    private void Update()
    {
        RadioHudUI.Tick();
    }

    private void OnDestroy()
    {
        RadioHudUI.TearDown();
    }
}
