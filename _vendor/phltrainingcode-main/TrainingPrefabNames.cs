using System;
using UnityEngine;

/// <summary>
/// Canonical names for trainingprefab objects (applied at spawn via TrainingPrefabRenamer).
/// Legacy Blender names are listed in training_prefab_names.json.
/// </summary>
public static class TrainingPrefabNames
{
    public const string HiveRoot = "trainingprefab";

    // Major assemblies
    public const string CenterPushBeam = "Train_CenterPushBeam";
    public const string ShooterSideRail = "Train_ShooterSideRail";
    public const string FarEndRail = "Train_FarEndRail";
    public const string RadioSpeaker = "Train_RadioSpeaker";
    public const string ShooterTutor = "Train_ShooterTutor";
    // GoalieDecor unused — DummyRed is the live crease goalie. Names kept only for disable/skip.
    public const string GoalieDecor = "Train_GoalieDecor";
    public const string GoalieDecorStick = "Train_GoalieDecorStick";
    public const string PracticePlayer = "Train_PracticePlayer";
    public const string RotatingStickRight = "Train_RotatingStickRight";
    public const string RotatingStickLeft = "Train_RotatingStickLeft";

    public static bool IsRotatingStickName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return name == RotatingStickRight ||
               name == RotatingStickLeft ||
               name == "RotatingStick" ||
               name == "RotatingStick2" ||
               name == "Spinner";
    }

    public const string ConePrefix = "Train_Cone_";
    public const string DecorPuckPrefix = "Train_DecorPuck_";

    public const string StickerPhlLogo = "Train_Sticker_PHLLogo";
    public const string StickerPhlText = "Train_Sticker_PHLText";
    public const string StickerDivisionOpen = "Train_Sticker_DivisionOpen";
    public const string StickerDivisionAllStar = "Train_Sticker_DivisionAllStar";
    public const string StickerDivisionContender = "Train_Sticker_DivisionContender";
    public const string StickerDivisionProspect = "Train_Sticker_DivisionProspect";
    public const string StickerDivisionPro = "Train_Sticker_DivisionPro";
    public const string StickerDivisionFrontierColor = "Train_Sticker_DivisionFrontierColor";
    public const string StickerDivisionFrontierBlack = "Train_Sticker_DivisionFrontierBlack";

    public const string SpeakerSlidableSuffix = "_Slidable_";

    public static readonly string[] SlidableBeamRoots = { CenterPushBeam };

    public static bool IsSpeakerName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return name == RadioSpeaker ||
               name == "Speaker" ||
               name.StartsWith(RadioSpeaker + SpeakerSlidableSuffix, StringComparison.Ordinal) ||
               name.StartsWith("Speaker" + SpeakerSlidableSuffix, StringComparison.Ordinal);
    }

    public static bool IsSpeakerSlidableRoot(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return name.StartsWith(RadioSpeaker + SpeakerSlidableSuffix, StringComparison.Ordinal) ||
               name.StartsWith("Speaker" + SpeakerSlidableSuffix, StringComparison.Ordinal);
    }

    public static bool IsSlidableBeamRoot(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (string root in SlidableBeamRoots)
        {
            if (name == root)
                return true;
        }

        // Legacy bundle names (before renamer runs)
        return name == "Untitl234ed";
    }

    public static bool IsStaticRailRoot(string name)
    {
        return name == ShooterSideRail || name == FarEndRail ||
               name == "Untitled 1" || name == "Untitled 1 (1)";
    }

    /// <summary>Body-check dummies (practice skater only — GoalieDecor unused).</summary>
    public static bool IsBodyCheckDummy(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        // GoalieDecor / GoalieModel retired — do not treat as solid dummies.
        // if (name == GoalieDecor || name == GoalieDecorStick || name == "GoalieModel" ...) return true;

        return name == PracticePlayer ||
               name == "PlayerWithStick";
    }

    public static bool IsUnusedGoalieDecor(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (name == GoalieDecor ||
            name == GoalieDecorStick ||
            name == GoalieDecor + "Stick" ||
            name == "GoalieModel" ||
            name == "GoalieModelStick")
            return true;

        // Blender / export variants — never treat MaxPractice DummyRed/DummyBlue as decor.
        string lower = name.ToLowerInvariant();
        if (lower.Contains("dummyred") || lower.Contains("dummyblue") || lower.Contains("dummy_"))
            return false;

        return lower.Contains("goaliemodel") ||
               lower.Contains("goalie_decor") ||
               lower.Contains("goaliedecor") ||
               lower == "goalie model" ||
               lower == "goalie model stick";
    }

    /// <summary>
    /// Obsolete misnomer — ShooterTutor / Goaltarp MUST keep puck hitboxes.
    /// Kept only so old call sites compile; always returns false.
    /// </summary>
    public static bool IsNonPhysicsDecor(string name) => false;

    /// <summary>Shooter tutor tarp (five-hole must stay open — mesh hitboxes, not boxes).</summary>
    public static bool IsShooterTutorName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (name == ShooterTutor ||
            name == "GoaltarpV1" ||
            name == "Goaltarp" ||
            name == "goaltarp")
            return true;

        string lower = name.ToLowerInvariant();
        return lower.Contains("shootertutor") ||
               lower.Contains("goal_tarp") ||
               lower.Contains("goaltarp");
    }

    public static bool IsUnderShooterTutor(Transform transform)
    {
        Transform current = transform;
        while (current != null)
        {
            if (IsShooterTutorName(current.name))
                return true;
            current = current.parent;
        }

        return false;
    }
}
