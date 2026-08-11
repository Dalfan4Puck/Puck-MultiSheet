using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Client-side floating Z z z z letters above voluntary napping skaters.
    /// </summary>
    internal static class NapSleepVfx
    {
        private const float RefreshSeconds = 0.2f;

        private static readonly Dictionary<ulong, NapSleepVfxAnchor> anchorsByClient = new Dictionary<ulong, NapSleepVfxAnchor>();
        private static float nextRefreshTime;

        internal static void Tick()
        {
            if (!ModRuntimeContext.ShouldInstallClientPresentation())
                return;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient)
            {
                Teardown();
                return;
            }

            if (!NapSleepSync.AnyNapping)
            {
                if (anchorsByClient.Count > 0)
                    Teardown();
                return;
            }

            if (Time.unscaledTime < nextRefreshTime)
                return;
            nextRefreshTime = Time.unscaledTime + RefreshSeconds;

            PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null)
                return;

            HashSet<ulong> active = new HashSet<ulong>();
            foreach (Player player in pm.GetPlayers())
            {
                if (player == null)
                    continue;

                ulong clientId = player.OwnerClientId;
                if (!NapSleepSync.IsNapping(clientId))
                    continue;

                PlayerBody body = player.PlayerBody;
                if (body == null)
                    continue;

                active.Add(clientId);
                if (!anchorsByClient.TryGetValue(clientId, out NapSleepVfxAnchor anchor) || anchor == null)
                {
                    anchor = NapSleepVfxAnchor.Attach(body);
                    anchorsByClient[clientId] = anchor;
                }
                else if (anchor.Body != body)
                {
                    anchor.Rebind(body);
                }

                anchor.SetVisible(true);
            }

            List<ulong> remove = null;
            foreach (KeyValuePair<ulong, NapSleepVfxAnchor> pair in anchorsByClient)
            {
                if (active.Contains(pair.Key))
                    continue;

                remove ??= new List<ulong>();
                remove.Add(pair.Key);
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
            {
                ulong clientId = remove[i];
                if (anchorsByClient.TryGetValue(clientId, out NapSleepVfxAnchor anchor))
                    anchor.DestroySelf();
                anchorsByClient.Remove(clientId);
            }
        }

        internal static void Teardown()
        {
            foreach (KeyValuePair<ulong, NapSleepVfxAnchor> pair in anchorsByClient)
                pair.Value?.DestroySelf();
            anchorsByClient.Clear();
            nextRefreshTime = 0f;
        }
    }

    internal sealed class NapSleepVfxAnchor : MonoBehaviour
    {
        private const float CycleSeconds = 2.4f;
        /// <summary>Extra lift when bounds top is used (clear helmet shell).</summary>
        private const float HeadTopClearance = 0.04f;
        /// <summary>Fallback when head mesh is unavailable.</summary>
        private const float HeadWorldUpOffset = 0.28f;
        private const float BodyFallbackUpOffset = 1.05f;

        private static Font letterFont;

        private static readonly (string text, float scale, float phase)[] LetterSpec =
        {
            ("Z", 1.00f, 0.00f),
            ("z", 0.78f, 0.35f),
            ("z", 0.62f, 0.70f),
            ("z", 0.48f, 1.05f),
        };

        private PlayerBody body;
        private Transform headTransform;
        private readonly List<LetterSlot> letters = new List<LetterSlot>(4);

        internal PlayerBody Body => body;

        internal static NapSleepVfxAnchor Attach(PlayerBody playerBody)
        {
            if (playerBody == null)
                return null;

            GameObject root = new GameObject("NapSleepVfx");
            root.transform.SetParent(playerBody.transform, false);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            NapSleepVfxAnchor anchor = root.AddComponent<NapSleepVfxAnchor>();
            anchor.body = playerBody;
            anchor.headTransform = ResolveHeadTransform(playerBody);
            anchor.BuildLetters();
            root.AddComponent<NapSleepBillboard>();
            return anchor;
        }

        internal void Rebind(PlayerBody playerBody)
        {
            body = playerBody;
            headTransform = ResolveHeadTransform(playerBody);
            if (body != null)
                transform.SetParent(body.transform, false);
        }

        internal void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible)
                gameObject.SetActive(visible);
        }

        internal void DestroySelf()
        {
            if (gameObject != null)
                Destroy(gameObject);
        }

        private void BuildLetters()
        {
            EnsureFont();
            letters.Clear();

            for (int i = 0; i < LetterSpec.Length; i++)
            {
                (string text, float scale, float phase) = LetterSpec[i];
                float x = i * 0.11f;
                float y = -i * 0.04f;

                GameObject letterObj = new GameObject("NapLetter_" + text + i);
                letterObj.transform.SetParent(transform, false);
                letterObj.transform.localPosition = new Vector3(x, y, 0f);
                letterObj.transform.localRotation = Quaternion.identity;

                GameObject outlineObj = new GameObject("Outline");
                outlineObj.transform.SetParent(letterObj.transform, false);
                outlineObj.transform.localPosition = new Vector3(0.008f, -0.008f, 0.01f);
                TextMesh outline = CreateTextMesh(outlineObj, text, scale * 1.08f, new Color(0.08f, 0.1f, 0.16f, 1f));

                GameObject fillObj = new GameObject("Fill");
                fillObj.transform.SetParent(letterObj.transform, false);
                TextMesh fill = CreateTextMesh(fillObj, text, scale, Color.white);

                letters.Add(new LetterSlot
                {
                    Root = letterObj.transform,
                    Fill = fill,
                    Outline = outline,
                    BaseLocal = new Vector3(x, y, 0f),
                    Phase = phase,
                    WobbleSeed = i * 1.37f,
                });
            }
        }

        private static TextMesh CreateTextMesh(GameObject host, string text, float scale, Color color)
        {
            TextMesh mesh = host.AddComponent<TextMesh>();
            mesh.font = EnsureFont();
            mesh.text = text;
            mesh.fontSize = 64;
            mesh.characterSize = 0.045f * scale;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.fontStyle = FontStyle.Bold;
            mesh.color = color;
            mesh.richText = false;
            return mesh;
        }

        private static Font EnsureFont()
        {
            if (letterFont == null)
            {
                letterFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (letterFont == null)
                    letterFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            return letterFont;
        }

        private void Update()
        {
            if (body == null)
            {
                DestroySelf();
                return;
            }

            if (headTransform == null)
                headTransform = ResolveHeadTransform(body);

            transform.position = ResolveEmitWorldPosition(body, headTransform);

            float now = Time.time;
            for (int i = 0; i < letters.Count; i++)
            {
                LetterSlot slot = letters[i];
                float t = (now + slot.Phase) % CycleSeconds;
                float u = t / CycleSeconds;
                float alpha = Mathf.Clamp01(1f - u);
                float driftX = 0.12f * u;
                float driftY = 0.20f * u;
                float wobble = Mathf.Sin((now + slot.WobbleSeed) * 4.2f) * 6f;

                slot.Root.localPosition = slot.BaseLocal + new Vector3(driftX, driftY, 0f);
                slot.Root.localRotation = Quaternion.Euler(0f, 0f, wobble * (1f - u * 0.5f));

                Color fill = slot.Fill.color;
                fill.a = alpha;
                slot.Fill.color = fill;

                Color outline = slot.Outline.color;
                outline.a = alpha * 0.95f;
                slot.Outline.color = outline;
            }
        }

        private struct LetterSlot
        {
            internal Transform Root;
            internal TextMesh Fill;
            internal TextMesh Outline;
            internal Vector3 BaseLocal;
            internal float Phase;
            internal float WobbleSeed;
        }

        /// <summary>World point at the top of the head/helmet — nap pose is flat so pivot + small offset reads on the chest.</summary>
        private static Vector3 ResolveEmitWorldPosition(PlayerBody playerBody, Transform headTransform)
        {
            if (headTransform != null)
            {
                if (TryGetHeadTopWorldPosition(headTransform, out Vector3 top))
                    return top;

                return headTransform.position + Vector3.up * HeadWorldUpOffset;
            }

            if (playerBody != null)
                return playerBody.transform.position + Vector3.up * BodyFallbackUpOffset;

            return Vector3.zero;
        }

        private static bool TryGetHeadTopWorldPosition(Transform headRoot, out Vector3 top)
        {
            top = default;
            if (headRoot == null)
                return false;

            Renderer[] renderers = headRoot.GetComponentsInChildren<Renderer>(true);
            bool haveBounds = false;
            Bounds bounds = default;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                string name = renderer.name.ToLowerInvariant();
                if (name.Contains("neck"))
                    continue;

                if (!haveBounds)
                {
                    bounds = renderer.bounds;
                    haveBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!haveBounds)
                return false;

            top = new Vector3(bounds.center.x, bounds.max.y + HeadTopClearance, bounds.center.z);
            return true;
        }

        /// <summary>PlayerMesh.PlayerHead — same path as ToastersReskinLoader hat/helmet attach.</summary>
        private static Transform ResolveHeadTransform(PlayerBody playerBody)
        {
            if (playerBody == null)
                return null;

            try
            {
                PlayerHead playerHead = playerBody.PlayerMesh?.PlayerHead;
                if (playerHead != null)
                {
                    Transform helmet = FindHelmetTransform(playerHead);
                    if (helmet != null)
                        return helmet;

                    return playerHead.transform;
                }
            }
            catch { }

            return null;
        }

        private static Transform FindHelmetTransform(PlayerHead playerHead)
        {
            if (playerHead == null)
                return null;

            Renderer[] renderers = playerHead.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                string name = renderer.name.ToLowerInvariant();
                if (name.Contains("helmet") && !name.Contains("cage") && !name.Contains("neck"))
                    return renderer.transform;
            }

            return playerHead.transform;
        }
    }

    internal sealed class NapSleepBillboard : MonoBehaviour
    {
        private Camera localCamera;
        private float nextCameraLookup;

        private void LateUpdate()
        {
            if (Time.time >= nextCameraLookup)
            {
                localCamera = ResolveLocalCamera();
                nextCameraLookup = Time.time + 1f;
            }

            if (localCamera == null)
                return;

            transform.rotation = localCamera.transform.rotation;
        }

        private static Camera ResolveLocalCamera()
        {
            try
            {
                NetworkManager nm = NetworkManager.Singleton;
                PlayerManager pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                Player player = pm != null && nm != null
                    ? pm.GetPlayerByClientId(nm.LocalClientId)
                    : null;
                if (player != null)
                {
                    PlayerCamera playerCamera = player.PlayerCamera;
                    if (playerCamera != null)
                    {
                        Camera cam = playerCamera.GetComponent<Camera>();
                        if (cam == null)
                            cam = playerCamera.GetComponentInChildren<Camera>();
                        if (cam != null)
                            return cam;
                    }
                }
            }
            catch { }

            return Camera.main;
        }
    }
}
