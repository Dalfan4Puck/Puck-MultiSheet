namespace PHLPracticeModPack
{
    /// <summary>Live strip vote tally for MOTD button badges (mirrors PHL Public game-mode UI).</summary>
    internal struct RinkStripVoteProgress
    {
        internal bool Active;
        internal int RinkIndex;
        internal RinkStripMode Mode;
        internal int InFavour;
        internal int Required;

        internal static RinkStripVoteProgress None
        {
            get { return new RinkStripVoteProgress { Active = false }; }
        }

        internal string BadgeText
        {
            get
            {
                if (!Active || Required < 1) return "";
                return InFavour + "/" + Required;
            }
        }
    }
}
