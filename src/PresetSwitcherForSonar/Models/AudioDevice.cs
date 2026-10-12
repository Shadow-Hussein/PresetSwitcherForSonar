namespace PresetSwitcherForSonar.Models;

/// <summary>
/// A Windows audio render endpoint, plus the two sentinel entries the pickers need.
///
/// <see cref="Id"/> is the raw MMDevice id (always of the form <c>{0.0.0.00000000}.{guid}</c>), so
/// neither sentinel value can collide with a real device.
///
/// <see cref="Name"/> is the friendly name exactly as Windows reports it, and is the only form that is
/// ever persisted. Everything the user should read comes from <see cref="DisplayName"/>.
/// </summary>
public sealed record AudioDevice(string Id, string Name)
{
    /// <summary>Per-mapping "fall back to the global default in Settings".</summary>
    public const string InheritId = "~global";

    /// <summary>"Leave Windows alone" — clears any per-app override.</summary>
    public const string SystemDefaultId = "";

    /// <summary>
    /// "This app's input is not ours to manage" — the recording-device picker's default, and the only
    /// value that means *do nothing at all*.
    ///
    /// Distinct from <see cref="SystemDefaultId"/> on purpose: that one actively clears the override,
    /// which would wipe a per-app input device the user had set by hand in the Volume mixer. Every
    /// mapping written before the recording-device picker existed lands here, so the feature cannot
    /// change anything until it is asked to.
    /// </summary>
    public const string UnmanagedId = "~unmanaged";

    /// <summary>How a device the enumeration no longer lists is marked in the pickers.</summary>
    public const string MissingSuffix = " (not connected)";

    public static readonly AudioDevice Inherit = new(InheritId, "Use default (Settings)");

    public static readonly AudioDevice SystemDefault = new(SystemDefaultId, "None — system default");

    public static readonly AudioDevice Unmanaged = new(UnmanagedId, "Don't change");

    /// <summary>
    /// True for an entry a saved setting still points at but the enumeration no longer lists — an
    /// unplugged headset, or an endpoint whose id changed underneath us.
    /// </summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// What the pickers show. The marker is applied here and never written into <see cref="Name"/>:
    /// baking it in let a selection round-trip through settings.json and come back decorated twice.
    /// </summary>
    public string DisplayName => IsMissing ? Name + MissingSuffix : Name;

    public bool IsSentinel => Id is InheritId or SystemDefaultId or UnmanagedId;

    public override string ToString() => DisplayName;

    /// <summary>
    /// True for one of Sonar's own virtual endpoints — "SteelSeries Sonar - Gaming" and friends.
    ///
    /// Sonar answers this authoritatively via <c>isVad</c> on <c>GET /audioDevices</c>, but that needs
    /// Sonar running. This name match is the offline fallback, and matches the friendly name GG gives
    /// every one of them.
    /// </summary>
    public static bool LooksLikeSonarVirtual(string? name) =>
        (name ?? "").Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A saved name with the marker stripped. An older build baked "(not connected)" into
    /// <see cref="Name"/> and then re-appended it on every refresh, so a settings file can carry several.
    /// Nothing writes it any more; this is what makes an already-corrupted name usable again — both for
    /// matching a live endpoint and for reading back in the log.
    /// </summary>
    public static string CleanName(string? name)
    {
        var cleaned = (name ?? "").TrimEnd();

        while (cleaned.EndsWith(MissingSuffix, StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^MissingSuffix.Length].TrimEnd();

        return cleaned;
    }
}
