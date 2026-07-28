namespace PHLPracticeModPack
{
    /// <summary>Per-rink training tools preset (vote-controlled on the Rinks tab).</summary>
    internal enum RinkStripMode : byte
    {
        Empty = 0,
        PhlTools = 1
    }

    internal static class RinkStripModeUtil
    {
        internal static string DisplayName(RinkStripMode mode)
        {
            switch (mode)
            {
                case RinkStripMode.PhlTools: return "PHL Tools";
                default: return "Empty";
            }
        }

        internal static RinkStripMode Parse(byte value)
        {
            return value == (byte)RinkStripMode.PhlTools ? RinkStripMode.PhlTools : RinkStripMode.Empty;
        }

        internal static string ToVoteKey(int rinkIndex, RinkStripMode mode)
        {
            return rinkIndex + ":" + (mode == RinkStripMode.PhlTools ? "tools" : "empty");
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
            else if (string.Equals(modePart, "empty", System.StringComparison.OrdinalIgnoreCase))
                mode = RinkStripMode.Empty;
            else
                return false;

            return rinkIndex >= 0;
        }
    }
}
