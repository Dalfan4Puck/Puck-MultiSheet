using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// PHL brand art bundled inside MultiSheet.dll (header wordmark + community icons),
    /// so the MOTD chrome never depends on a phlstats deploy or network fetch.
    /// PNG decode keeps the alpha channel: PHL marks store white RGB under A=0, and
    /// UnityWebRequestTexture-style flattening would show them as white boxes.
    /// ImageConversion is resolved via reflection so the netstandard2.1 build does not
    /// reference UnityEngine.ImageConversionModule (CS1705 / netstandard mismatch).
    /// </summary>
    internal static class PracticeMotdAssets
    {
        internal const string PhlWordmark = "PHLLogoBlack";
        internal const string PhlstatsIcon = "phlstatlogo4x";
        internal const string DiscordIcon = "discord4x";
        internal const string YoutubeIcon = "youtube4x";
        internal const string TwitchIcon = "twitch4x";

        private static readonly Dictionary<string, Texture2D> cache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private static MethodInfo loadImageMethod;
        private static bool loadImageResolved;

        internal static bool TryGetTexture(string name, out Texture2D texture)
        {
            texture = null;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (cache.TryGetValue(name, out texture))
                return texture != null;

            byte[] data = ReadResource("PHLPracticeModPack.Icons." + name + ".png");
            texture = data != null ? DecodeWithAlpha(data) : null;
            cache[name] = texture;
            return texture != null;
        }

        internal static void Teardown()
        {
            foreach (KeyValuePair<string, Texture2D> entry in cache)
            {
                if (entry.Value != null)
                {
                    try { UnityEngine.Object.Destroy(entry.Value); } catch { }
                }
            }
            cache.Clear();
        }

        private static byte[] ReadResource(string logicalName)
        {
            try
            {
                Assembly asm = typeof(PracticeMotdAssets).Assembly;
                using (Stream stream = asm.GetManifestResourceStream(logicalName))
                {
                    if (stream == null) return null;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Embedded icon read failed: " + ex.Message);
                return null;
            }
        }

        private static Texture2D DecodeWithAlpha(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!TryLoadImage(texture, data))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return texture;
        }

        private static bool TryLoadImage(Texture2D texture, byte[] data)
        {
            MethodInfo method = ResolveLoadImage();
            if (method == null) return false;
            try
            {
                object result = method.GetParameters().Length == 3
                    ? method.Invoke(null, new object[] { texture, data, false })
                    : method.Invoke(null, new object[] { texture, data });
                return result is bool ok && ok;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] PNG decode failed: " + ex.Message);
                return false;
            }
        }

        private static MethodInfo ResolveLoadImage()
        {
            if (loadImageResolved) return loadImageMethod;
            loadImageResolved = true;

            try
            {
                Type conversion = Type.GetType(
                    "UnityEngine.ImageConversion, UnityEngine.ImageConversionModule",
                    throwOnError: false);
                if (conversion == null)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        conversion = assembly.GetType("UnityEngine.ImageConversion");
                        if (conversion != null) break;
                    }
                }

                if (conversion == null) return null;

                loadImageMethod = conversion.GetMethod(
                    "LoadImage",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) },
                    null);

                if (loadImageMethod == null)
                {
                    loadImageMethod = conversion.GetMethod(
                        "LoadImage",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(Texture2D), typeof(byte[]) },
                        null);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] ImageConversion unavailable: " + ex.Message);
            }

            return loadImageMethod;
        }
    }
}
