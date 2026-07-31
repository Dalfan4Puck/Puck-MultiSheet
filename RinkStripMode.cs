using System.Collections.Generic;

namespace PHLPracticeModPack
{
    /// <summary>Per-rink training tools preset (vote-controlled on the Rinks tab).</summary>
    internal enum RinkStripMode : byte
    {
        Empty = 0,
        PhlTools = 1,
        GoaliePractice = 2,
        TipPractice = 3,
        PuckChasers = 4,
        StretchPassing = 5,
        PointPassing = 6,
        LowCyclePassing = 7,
    }

    internal static class RinkStripModeUtil
    {
        internal const string EmptyRinkDropdownLabel = "None";
        /// <summary>Legacy label — still accepted when parsing saved UI state.</summary>
        internal const string EmptyRinkDropdownLabelLegacy = "Empty Rink";

        internal static readonly RinkStripMode[] DropdownModes =
        {
            RinkStripMode.PhlTools,
            RinkStripMode.GoaliePractice,
            RinkStripMode.TipPractice,
            RinkStripMode.PuckChasers,
            RinkStripMode.StretchPassing,
            RinkStripMode.PointPassing,
            RinkStripMode.LowCyclePassing,
        };

        internal static bool IsPracticeMode(RinkStripMode mode)
        {
            return mode != RinkStripMode.Empty;
        }

        /// <summary>Max humans that may join a rink while this strip mode is active (0 = use server default).</summary>
        internal static int GetJoinCapacity(RinkStripMode mode, int defaultCapacity)
        {
            switch (mode)
            {
                case RinkStripMode.GoaliePractice: return 1;
                case RinkStripMode.TipPractice: return 2;
                case RinkStripMode.StretchPassing: return 1;
                case RinkStripMode.PointPassing: return 1;
                case RinkStripMode.LowCyclePassing: return 1;
                default:
                    return defaultCapacity <= 0 ? 0 : defaultCapacity;
            }
        }

        /// <summary>Short UI label for practice-mode join caps (null when default capacity applies).</summary>
        internal static string GetJoinCapacityHint(RinkStripMode mode)
        {
            switch (mode)
            {
                case RinkStripMode.GoaliePractice: return "1 player max";
                case RinkStripMode.TipPractice: return "2 players max";
                default: return null;
            }
        }

        internal static int DropdownIndex(RinkStripMode mode)
        {
            for (int i = 0; i < DropdownModes.Length; i++)
            {
                if (DropdownModes[i] == mode)
                    return i;
            }
            return 0;
        }

        internal static RinkStripMode FromDropdownIndex(int index)
        {
            if (index < 0 || index >= DropdownModes.Length)
                return RinkStripMode.PhlTools;
            return DropdownModes[index];
        }

        internal static string DisplayName(RinkStripMode mode)
        {
            switch (mode)
            {
                case RinkStripMode.PhlTools: return "PHL Tools";
                case RinkStripMode.GoaliePractice: return "Goalie Practice";
                case RinkStripMode.TipPractice: return "Tip Practice";
                case RinkStripMode.PuckChasers: return "Puck Chasers";
                case RinkStripMode.StretchPassing: return "Stretch Passing";
                case RinkStripMode.PointPassing: return "Point Passing";
                case RinkStripMode.LowCyclePassing: return "Low Cycle Passing";
                default: return "Empty";
            }
        }

        internal static string DropdownLabel(RinkStripMode mode)
        {
            return mode == RinkStripMode.Empty ? EmptyRinkDropdownLabel : DisplayName(mode);
        }

        /// <summary>Hover label for the active-mode strip bar (e.g. "Remove Goalie Practice").</summary>
        internal static string RemoveBarLabel(RinkStripMode mode)
        {
            switch (mode)
            {
                case RinkStripMode.PhlTools: return "Remove PHL Tools";
                case RinkStripMode.GoaliePractice: return "Remove Goalie Practice";
                case RinkStripMode.TipPractice: return "Remove Tip Practice";
                case RinkStripMode.PuckChasers: return "Remove Puck Chasers";
                case RinkStripMode.StretchPassing: return "Remove Stretch Passing";
                case RinkStripMode.PointPassing: return "Remove Point Passing";
                case RinkStripMode.LowCyclePassing: return "Remove Low Cycle Passing";
                default: return "Remove";
            }
        }

        internal static bool TryParseDropdownLabel(string label, out RinkStripMode mode)
        {
            mode = RinkStripMode.Empty;
            if (string.IsNullOrEmpty(label)) return false;
            if (string.Equals(label, EmptyRinkDropdownLabel, System.StringComparison.Ordinal)
                || string.Equals(label, EmptyRinkDropdownLabelLegacy, System.StringComparison.Ordinal))
                return true;
            for (int i = 0; i < DropdownModes.Length; i++)
            {
                if (string.Equals(label, DisplayName(DropdownModes[i]), System.StringComparison.Ordinal))
                {
                    mode = DropdownModes[i];
                    return true;
                }
            }
            return false;
        }

        internal static string FeatureBadge(RinkStripMode mode)
        {
            switch (mode)
            {
                case RinkStripMode.PhlTools: return "TOOLS";
                case RinkStripMode.GoaliePractice: return "GOALIE";
                case RinkStripMode.TipPractice: return "TIP";
                case RinkStripMode.PuckChasers: return "CHASE";
                case RinkStripMode.StretchPassing: return "STRETCH";
                case RinkStripMode.PointPassing: return "POINT";
                case RinkStripMode.LowCyclePassing: return "LOW";
                default: return "";
            }
        }

        internal static RinkStripMode Parse(byte value)
        {
            switch (value)
            {
                case (byte)RinkStripMode.PhlTools: return RinkStripMode.PhlTools;
                case (byte)RinkStripMode.GoaliePractice: return RinkStripMode.GoaliePractice;
                case (byte)RinkStripMode.TipPractice: return RinkStripMode.TipPractice;
                case (byte)RinkStripMode.PuckChasers: return RinkStripMode.PuckChasers;
                case (byte)RinkStripMode.StretchPassing: return RinkStripMode.StretchPassing;
                case (byte)RinkStripMode.PointPassing: return RinkStripMode.PointPassing;
                case (byte)RinkStripMode.LowCyclePassing: return RinkStripMode.LowCyclePassing;
                default: return RinkStripMode.Empty;
            }
        }

        internal static string ToVoteKey(int rinkIndex, RinkStripMode mode)
        {
            string suffix;
            switch (mode)
            {
                case RinkStripMode.PhlTools: suffix = "tools"; break;
                case RinkStripMode.GoaliePractice: suffix = "goalie"; break;
                case RinkStripMode.TipPractice: suffix = "tip"; break;
                case RinkStripMode.PuckChasers: suffix = "chasers"; break;
                case RinkStripMode.StretchPassing: suffix = "stretch"; break;
                case RinkStripMode.PointPassing: suffix = "pointpass"; break;
                case RinkStripMode.LowCyclePassing: suffix = "lowcycle"; break;
                default: suffix = "empty"; break;
            }
            return rinkIndex + ":" + suffix;
        }

        internal static bool TryParseVoteKey(string key, out int rinkIndex, out RinkStripMode mode)
        {
            rinkIndex = 0;
            mode = RinkStripMode.Empty;
            if (string.IsNullOrEmpty(key)) return false;

            int colon = key.IndexOf(':');
            if (colon <= 0 || colon >= key.Length - 1) return false;
            if (!int.TryParse(key.Substring(0, colon), out rinkIndex)) return false;

            string modePart = key.Substring(colon + 1);
            if (string.Equals(modePart, "tools", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.PhlTools;
            else if (string.Equals(modePart, "goalie", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.GoaliePractice;
            else if (string.Equals(modePart, "tip", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.TipPractice;
            else if (string.Equals(modePart, "chasers", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.PuckChasers;
            else if (string.Equals(modePart, "stretch", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.StretchPassing;
            else if (string.Equals(modePart, "pointpass", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.PointPassing;
            else if (string.Equals(modePart, "lowcycle", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.LowCyclePassing;
            else if (string.Equals(modePart, "empty", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.Empty;
            else
                return false;

            return rinkIndex >= 0;
        }

        /// <summary>
        /// Puck Chasers is global — only one rink may run it. Returns the other rink index if blocked.
        /// </summary>
        internal static bool TryGetChasersOccupiedRink(
            IList<RinkStripMode> modes,
            int exceptRinkIndex,
            out int occupiedRinkIndex)
        {
            occupiedRinkIndex = -1;
            if (modes == null)
                return false;

            for (int i = 0; i < modes.Count; i++)
            {
                if (i == exceptRinkIndex)
                    continue;
                if (modes[i] == RinkStripMode.PuckChasers)
                {
                    occupiedRinkIndex = i;
                    return true;
                }
            }

            return false;
        }

        internal static string ChasersBlockedMessage(int occupiedRinkIndex)
        {
            return "Puck Chasers is already active on Rink " + (occupiedRinkIndex + 1) +
                   ". Remove it there first.";
        }
    }
}
