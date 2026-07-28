/// <summary>
/// Protocol / package version for dedicated + client installs.
/// Bump when Custom Messaging payloads or layout schema change.
/// </summary>
public static class FlamiePracVersion
{
    public const string Package = "1.0.0";
    public const string Protocol = "1";
    public const string Target = "blank-dedicated"; // no MultiSheet

    public static string Banner =>
        "FlamiePrac " + Package + " protocol=" + Protocol + " target=" + Target +
        " slidable=" + FlamiePracFeatures.EnableSlidableProps +
        " slidablePhysics=" + FlamiePracFeatures.SlidablePhysicsEnabled +
        " hiveMotion=" + FlamiePracFeatures.EnableHiveMotion +
        " radio=" + FlamiePracFeatures.EnableRadio;
}
