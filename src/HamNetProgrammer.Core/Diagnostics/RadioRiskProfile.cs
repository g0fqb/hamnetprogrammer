namespace HamNetProgrammer.Core.Diagnostics;

/// <summary>
/// How much confidence exists in a given radio model's memory map and erase-block layout,
/// i.e. how likely a write is to go wrong in a way this tool hasn't already found and fixed.
/// </summary>
public enum RadioRiskTier
{
    /// <summary>Real hardware write-tested; known failure modes have been found and fixed in code.</summary>
    Validated,

    /// <summary>Same protocol/code family as a validated model, but this model's own memory map
    /// (in particular flash erase-block boundaries) has not been independently confirmed.</summary>
    Moderate,

    /// <summary>No community reverse-engineering exists for this model at all.</summary>
    High,

    /// <summary>Device identifier not recognized - could be a different platform entirely.</summary>
    Unknown,
}

public sealed record RadioRiskProfile(string ModelLabel, RadioRiskTier Tier, string Explanation);

/// <summary>
/// Maps a radio's reported device identifier to a risk tier. Only the AT-D878UV is actually
/// implemented today; the other AnyTone models are catalogued here ahead of that work so the
/// same tiered-disclaimer machinery covers them the moment support is added, instead of every
/// new model needing its own bolted-on warning.
/// </summary>
public static class RadioRiskCatalog
{
    public static RadioRiskProfile Lookup(string deviceId)
    {
        var id = (deviceId ?? "").Trim().ToUpperInvariant();

        if (id.Contains("878"))
            return new RadioRiskProfile("AnyTone AT-D878UV", RadioRiskTier.Validated,
                "This tool's memory map and flash erase-block layout for the D878UV are confirmed " +
                "against real hardware. Writing to flash memory always carries some risk, but a full " +
                "backup is taken automatically before every write, and Restore Previous Codeplug can " +
                "undo it afterwards if anything looks wrong.");

        if (id.Contains("868"))
            return new RadioRiskProfile("AnyTone AT-D868UV / D868UVE", RadioRiskTier.Moderate,
                "Same protocol family as the D878UV (its direct predecessor) with likely-similar record " +
                "layout, but this model's flash erase-block boundaries have not been independently " +
                "confirmed on real hardware. Back up the radio's current codeplug before writing.");

        if (id.Contains("578"))
            return new RadioRiskProfile("AnyTone AT-D578UV", RadioRiskTier.Moderate,
                "Shares CPS/codeplug lineage with the D878UV, but erase-block boundaries are unconfirmed " +
                "for this model. Back up the radio's current codeplug before writing.");

        if (id.Contains("890"))
            return new RadioRiskProfile("AnyTone AT-D890UV", RadioRiskTier.High,
                "No community reverse-engineering exists for this model at all - no confirmed memory map " +
                "or erase-block layout. Recovery from a bad write is not guaranteed.");

        return new RadioRiskProfile(string.IsNullOrWhiteSpace(deviceId) ? "Unknown device" : deviceId, RadioRiskTier.Unknown,
            "This device identifier is not recognized. No memory map, erase-block layout, or protocol " +
            "details have been established for this radio. Recovery from a bad write is not guaranteed.");
    }
}
