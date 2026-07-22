namespace HamNetProgrammer.Desktop.Utils;

/// <summary>The 104 standard DCS (Digital-Coded Squelch) codes, the same list every radio
/// programming tool offers. Desktop-only (not Core) since, unlike CtcssTones, no encoder
/// currently consumes DCS - see ChannelEditDialog's remarks on why this is UI-only validation for
/// now, not a hardware-write concern.</summary>
public static class DcsCodes
{
    public static readonly string[] StandardCodes =
    [
        "023", "025", "026", "031", "032", "036", "043", "047", "051", "053",
        "054", "065", "071", "072", "073", "074", "114", "115", "116", "122",
        "125", "131", "132", "134", "143", "145", "152", "155", "156", "162",
        "165", "172", "174", "205", "212", "223", "225", "226", "243", "244",
        "245", "246", "251", "252", "255", "261", "263", "265", "266", "271",
        "274", "306", "311", "315", "325", "331", "332", "343", "346", "351",
        "356", "364", "365", "371", "411", "412", "413", "423", "431", "432",
        "445", "446", "452", "454", "455", "462", "464", "465", "466", "503",
        "506", "516", "523", "526", "532", "546", "565", "606", "612", "624",
        "627", "631", "632", "654", "662", "664", "703", "712", "723", "731",
        "732", "734", "743", "754",
    ];
}
