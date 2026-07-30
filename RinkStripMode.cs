namespace PHLPracticeModPack
{
    /// <summary>Per-rink training tools preset (vote-controlled on the Rinks tab).</summary>
    internal enum RinkStripMode : byte
    {
        Empty = 0,
        PhlTools = 1,
        GoaliePractice = 2,
        TipPractice = 3
    }

    internal static class RinkStripModeUtil
    {
        internal const string EmptyRinkDropdownLabel = "Empty Rink";

        internal static readonly RinkStripMode[] DropdownModes =
        {
            RinkStripMode.PhlTools,
            RinkStripMode.GoaliePractice,
            RinkStripMode.TipPractice
        };

        internal static bool IsPracticeMode(RinkStripMode mode)
        {
            return mode != RinkStripMode.Empty;
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
                default: return "Empty";
            }
        }

        internal static string DropdownLabel(RinkStripMode mode)
        {
            return mode == RinkStripMode.Empty ? EmptyRinkDropdownLabel : DisplayName(mode);
        }

        internal static bool TryParseDropdownLabel(string label, out RinkStripMode mode)
        {
            mode = RinkStripMode.Empty;
            if (string.IsNullOrEmpty(label)) return false;
            if (string.Equals(label, EmptyRinkDropdownLabel, System.StringComparison.Ordinal))
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
            else if (string.Equals(modePart, "empty", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.Empty;
            else
                return false;

            return rinkIndex >= 0;
        }
    }
}
