namespace HamNetProgrammer.Core.Radios.AnyTone.CallsignDb;

/// <summary>
/// Builds the write plan for the AT-D878UV II Plus's bulk Callsign Database - a genuinely separate
/// AnyTone feature from Contacts/TalkGroupList (see AnyToneD878CodeplugEncoder), confirmed via two
/// independent sources (reald/anytone-flash-tools' protocol notes, and qdmr's hardware-tested
/// D878UV2CallsignDB, which agree exactly on addresses and a 500,000-entry cap - noticeably larger
/// than the D868UV/plain D878UV's 200,000). It exists purely to resolve an unknown caller's DMR ID
/// to a name/callsign - not editable per-entry in CPS, not referenced by channels, just a bulk
/// lookup table. Read-verified empty (count=0) on real hardware before this was ever built, so
/// there is no existing data at risk here, unlike most other regions in this project.
///
/// Three parts, each gapped into fixed-size banks 0x40000 apart (matching every other partitioned
/// region in this radio's memory map): an Index (8 bytes/entry, sorted ascending by the same
/// BCD-shifted key used by TalkGroupOffsets, mapping DmrId to a byte offset into the entries),
/// Limits (a 16-byte header: entry count + end-of-database address), and the Entries themselves
/// (variable length, see CallsignDbEntryCodec - packed contiguously with no per-bank padding, so an
/// entry can straddle two banks).
/// </summary>
public static class CallsignDbEncoder
{
    private const uint IndexBaseAddress = 0x04000000;
    private const uint IndexBankSpacing = 0x00040000;
    private const int IndexBankSize = 0x0001F400; // 128,000 bytes = 16,000 entries
    private const int IndexEntrySize = 8;

    private const uint LimitsAddress = 0x04840000;
    private const int LimitsSize = 0x10;

    private const uint EntriesBaseAddress = 0x05500000;
    private const uint EntriesBankSpacing = 0x00040000;
    private const int EntriesBankSize = 0x000186A0; // 100,000 bytes

    public const int MaxEntries = 500_000;

    /// <param name="usersSortedById">Must already be sorted ascending by DmrId and deduped - the
    /// index's binary-searchability on the radio depends on ascending order, per both reference
    /// sources.</param>
    public static List<EncodedRegion> Build(IReadOnlyList<CallsignDbUser> usersSortedById, List<string>? warnings = null)
    {
        var users = usersSortedById;
        if (users.Count > MaxEntries)
        {
            warnings?.Add($"{users.Count - MaxEntries:N0} entries beyond this radio's {MaxEntries:N0}-entry callsign database limit were not written.");
            users = users.Take(MaxEntries).ToList();
        }

        var n = users.Count;
        var regions = new List<EncodedRegion>();

        var entrySizes = new int[n];
        long dbSize = 0;
        for (var i = 0; i < n; i++)
        {
            entrySizes[i] = CallsignDbEntryCodec.Size(users[i]);
            dbSize += entrySizes[i];
        }
        long indexSize = (long)n * IndexEntrySize;

        var limitsBytes = new byte[LimitsSize];
        WriteUInt32LE(limitsBytes, 0, (uint)n);
        WriteUInt32LE(limitsBytes, 4, (uint)(EntriesBaseAddress + (ulong)dbSize));
        regions.Add(new EncodedRegion("CallsignDbLimits", LimitsAddress, limitsBytes));

        regions.AddRange(BuildIndexBanks(users, indexSize));
        regions.AddRange(BuildEntryBanks(users, entrySizes));

        return regions;
    }

    private static IEnumerable<EncodedRegion> BuildIndexBanks(IReadOnlyList<CallsignDbUser> users, long indexSize)
    {
        var bankCount = indexSize == 0 ? 0 : (int)((indexSize + IndexBankSize - 1) / IndexBankSize);
        var banks = new byte[bankCount][];
        for (var b = 0; b < bankCount; b++)
        {
            var remaining = indexSize - (long)b * IndexBankSize;
            var thisBankSize = (int)Math.Min(remaining, IndexBankSize);
            var buf = new byte[Align16(thisBankSize)];
            Array.Fill(buf, (byte)0xFF); // 0xFFFFFFFF/0xFFFFFFFF = "no entry" per both reference sources
            banks[b] = buf;
        }

        long entryOffset = 0;
        for (var i = 0; i < users.Count; i++)
        {
            var globalOffset = (long)i * IndexEntrySize;
            var bank = (int)(globalOffset / IndexBankSize);
            var offsetInBank = (int)(globalOffset % IndexBankSize);
            // Bit0=1 means group call in this radio's convention; the callsign DB is always
            // Private, so it's always 0 here - see CallsignDbEntryCodec's remarks.
            var key = BcdDigitsAsHexValue(users[i].DmrId) << 1;
            WriteUInt32LE(banks[bank], offsetInBank, key);
            WriteUInt32LE(banks[bank], offsetInBank + 4, (uint)entryOffset);
            entryOffset += CallsignDbEntryCodec.Size(users[i]);
        }

        for (var b = 0; b < bankCount; b++)
            yield return new EncodedRegion($"CallsignDbIndex[{b}]", IndexBaseAddress + (uint)(b * IndexBankSpacing), banks[b]);
    }

    private static IEnumerable<EncodedRegion> BuildEntryBanks(IReadOnlyList<CallsignDbUser> users, int[] entrySizes)
    {
        if (users.Count == 0) yield break;

        var banks = new List<byte[]>();
        void EnsureBank(int index)
        {
            while (banks.Count <= index) banks.Add(new byte[EntriesBankSize]);
        }

        var bankIdx = 0;
        var offsetInBank = 0;
        EnsureBank(0);
        for (var i = 0; i < users.Count; i++)
        {
            var entryBytes = CallsignDbEntryCodec.Encode(users[i]);
            if (offsetInBank + entryBytes.Length > EntriesBankSize)
            {
                // Straddles a bank boundary - split across the two, matching qdmr's own approach
                // (the actual, hardware-tested reference implementation for this exact format).
                var firstPart = EntriesBankSize - offsetInBank;
                if (firstPart > 0)
                    Array.Copy(entryBytes, 0, banks[bankIdx], offsetInBank, firstPart);
                bankIdx++;
                EnsureBank(bankIdx);
                var secondPart = entryBytes.Length - firstPart;
                Array.Copy(entryBytes, firstPart, banks[bankIdx], 0, secondPart);
                offsetInBank = secondPart;
            }
            else
            {
                Array.Copy(entryBytes, 0, banks[bankIdx], offsetInBank, entryBytes.Length);
                offsetInBank += entryBytes.Length;
            }
        }

        // Every bank except the last is fully used by construction (an entry only advances to the
        // next bank when it doesn't fit in what's left) - only the last needs trimming to its real
        // used length; writing the other banks' full 100,000 bytes is correct as-is.
        for (var b = 0; b < banks.Count; b++)
        {
            var usedLength = b == banks.Count - 1 ? offsetInBank : EntriesBankSize;
            var trimmed = new byte[Align16(usedLength)];
            Array.Copy(banks[b], trimmed, Math.Min(trimmed.Length, banks[b].Length));
            yield return new EncodedRegion($"CallsignDbEntries[{b}]", EntriesBaseAddress + (uint)(b * EntriesBankSpacing), trimmed);
        }
    }

    private static uint BcdDigitsAsHexValue(uint decimalValue)
    {
        var bcdBytes = BcdCodec.Encode(decimalValue, 4);
        uint result = 0;
        foreach (var b in bcdBytes) result = (result << 8) | b;
        return result;
    }

    private static int Align16(int length) => (length + 15) / 16 * 16;

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
