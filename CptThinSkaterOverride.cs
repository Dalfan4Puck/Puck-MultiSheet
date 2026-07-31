using System;
using System.IO;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

namespace PHLPracticeModPack
{
    /// <summary>
    /// CPT defaults ThinSkaterBodies=true and does not sync that flag from the server.
    /// MultiSheet (and PHL) require CompTweaks on clients, so every joiner would get
    /// thumb-skaters unless we force the in-memory flag off.
    ///
    /// Important: we must NOT call CPT SaveToFile on a dedicated server. That rewrites
    /// the entire CompetitivePuckTweaks.json from whatever is in memory and can clobber
    /// PHL turn / puck values with slower CPT defaults. Persist is client-only and only
    /// flips the ThinSkaterBodies JSON key.
    /// </summary>
    internal static class CptThinSkaterOverride
    {
        private const string PluginCoreTypeName = "CompetitivePuckTweaks.src.PluginCore";
        private static readonly string ConfigPath = Path.Combine(".", "config", "CompetitivePuckTweaks.json");

        internal static void Apply()
        {
            try
            {
                // Dedicated server: do not touch CPT at all. Turn/puck physics are
                // server-authored from that machine's CompetitivePuckTweaks.json.
                NetworkManager nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer && !nm.IsClient)
                {
                    PracticeLog.Info("[PHLPractice] Dedicated server — skipping ThinSkaterBodies override (CPT file left untouched).");
                    return;
                }

                Type pluginCore = FindType(PluginCoreTypeName);
                if (pluginCore == null)
                {
                    PracticeLog.Info("[PHLPractice] CPT not loaded; ThinSkaterBodies override skipped.");
                    return;
                }

                FieldInfo configField = pluginCore.GetField("config", BindingFlags.Public | BindingFlags.Static);
                object config = configField?.GetValue(null);
                if (config == null)
                {
                    Debug.LogWarning("[PHLPractice] CPT config missing; ThinSkaterBodies override skipped.");
                    return;
                }

                PropertyInfo thinProp = config.GetType().GetProperty("ThinSkaterBodies", BindingFlags.Public | BindingFlags.Instance);
                if (thinProp == null || thinProp.PropertyType != typeof(bool))
                {
                    Debug.LogWarning("[PHLPractice] CPT ThinSkaterBodies property not found.");
                    return;
                }

                bool was = (bool)thinProp.GetValue(config);
                if (was)
                    thinProp.SetValue(config, false);

                // Client / host only: flip the one JSON key. Never dump the whole config.
                PersistThinSkaterFlagOnly();

                PracticeLog.Info(was
                    ? "[PHLPractice] Forced CPT ThinSkaterBodies=false (was true); updated client JSON key only."
                    : "[PHLPractice] CPT ThinSkaterBodies already false.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] ThinSkaterBodies override failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Surgical persist — only rewrites <c>"ThinSkaterBodies": true</c> → false.
        /// Avoids CPT SaveToFile, which serializes every field and can erase PHL turn rates.
        /// </summary>
        private static void PersistThinSkaterFlagOnly()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                string json = File.ReadAllText(ConfigPath);
                string updated = System.Text.RegularExpressions.Regex.Replace(
                    json,
                    "\"ThinSkaterBodies\"\\s*:\\s*true",
                    "\"ThinSkaterBodies\": false",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (json != updated)
                    File.WriteAllText(ConfigPath, updated);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PHLPractice] Could not persist CPT ThinSkaterBodies=false: " + ex.Message);
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
