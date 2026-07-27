using System;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace PHLPracticeModPack
{
    /// <summary>
    /// Single role model for MultiSheet. Anchored on Puck's ApplicationManager.IsDedicatedGameServer
    /// with GraphicsDeviceType.Null fallback (see Stats Tooltip ServerFunc).
    /// </summary>
    internal static class ModRuntimeContext
    {
        private static bool initialized;
        private static bool isDedicatedGameServer;

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            isDedicatedGameServer = ResolveIsDedicatedGameServer();
        }

        internal static void Reset()
        {
            initialized = false;
            isDedicatedGameServer = false;
        }

        internal static bool IsDedicatedGameServer
        {
            get
            {
                if (!initialized) Initialize();
                return isDedicatedGameServer;
            }
        }

        internal static bool IsServer
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsServer;
            }
        }

        internal static bool IsClient
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsClient;
            }
        }

        internal static bool IsHost => IsServer && IsClient;

        internal static bool HasGpu =>
            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>Non-dedicated machines that may run client UI and presentation.</summary>
        internal static bool ShouldInstallClientPatches() => !IsDedicatedGameServer;

        /// <summary>Client visuals, TRL bridge, minimap, DrawMesh proxy.</summary>
        internal static bool ShouldInstallClientPresentation() => !IsDedicatedGameServer;

        internal static string RoleLabel
        {
            get
            {
                if (IsDedicatedGameServer) return "dedicated";
                if (IsHost) return "host";
                if (IsClient) return "client";
                if (IsServer) return "server";
                return "offline";
            }
        }

        private static bool ResolveIsDedicatedGameServer()
        {
            try
            {
                PropertyInfo dedicatedProp = typeof(ApplicationManager).GetProperty(
                    "IsDedicatedGameServer",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (dedicatedProp != null && dedicatedProp.PropertyType == typeof(bool))
                    return (bool)dedicatedProp.GetValue(null);
            }
            catch { }

            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        }
    }
}
