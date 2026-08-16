namespace SonarPresetSwitcher.Models;

/// <summary>
/// A Windows audio render endpoint, plus the two sentinel entries the pickers need.
///
/// <see cref="Id"/> is the raw MMDevice id (always of the form <c>{0.0.0.00000000}.{guid}</c>), so
/// neither sentinel value can collide with a real device.
/// </summary>
public sealed record AudioDevice(string Id, string Name)
{
    /// <summary>Per-mapping "fall back to the global default in Settings".</summary>
    public const string InheritId = "~global";

    /// <summary>"Leave Windows alone" — clears any per-app override.</summary>
    public const string SystemDefaultId = "";

    public static readonly AudioDevice Inherit = new(InheritId, "Use default (Settings)");

    public static readonly AudioDevice SystemDefault = new(SystemDefaultId, "None — system default");

    public bool IsSentinel => Id is InheritId or SystemDefaultId;

    public override string ToString() => Name;
}
