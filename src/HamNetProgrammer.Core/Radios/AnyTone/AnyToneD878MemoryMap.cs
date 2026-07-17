using System.Collections.Generic;

namespace HamNetProgrammer.Core.Radios.AnyTone;

public sealed record MemoryRegion(string Name, uint Address, int Length);

/// <summary>
/// Known memory regions of the AT-D878UV, as reverse-engineered by
/// https://github.com/reald/anytone-flash-tools (at-d878uv_memory.md).
///
/// Deliberately excludes the Digital Contact List (0x04000000-0x07680000+) and the
/// "Talk group offsets" write-support section (0x04340000): both are offset-indexed,
/// variable-extent structures whose real size depends on a "next free entry" pointer
/// rather than a fixed length, and the contact list's reserved address space alone
/// spans ~800MB if read naively. These need their own sizing logic - see the roadmap.
/// </summary>
public static class AnyToneD878MemoryMap
{
    public static IReadOnlyList<MemoryRegion> GetBaselineRegions()
    {
        var regions = new List<MemoryRegion>();

        // Channels: 32 banks of 8192 bytes stepping by 0x40000, last bank only 2176 bytes.
        // 4000 channels + 2 VFO channels, 64 bytes per channel.
        for (var i = 0; i < 32; i++)
        {
            var address = 0x00800000u + (uint)(i * 0x40000);
            var length = i == 31 ? 2176 : 8192;
            regions.Add(new MemoryRegion($"Channels[{i}]", address, length));
        }

        regions.Add(new MemoryRegion("Zones", 0x01000000, 250 * 512));
        regions.Add(new MemoryRegion("RoamingChannels", 0x01040000, 250 * 32));
        regions.Add(new MemoryRegion("RoamingChannelsUsed", 0x01042000, 32));
        regions.Add(new MemoryRegion("RoamingZonesUsed", 0x01042080, 16));
        regions.Add(new MemoryRegion("RoamingZones", 0x01043000, 64 * 128));

        // Scanlists: 250 entries of 144 bytes, addressed via
        // 0x01080000 + floor((n)/16)*0x40000 + (n%16)*0x200 for n = 0..249.
        for (var n = 0; n < 250; n++)
        {
            var address = 0x01080000u + (uint)(n / 16 * 0x40000) + (uint)(n % 16 * 0x200);
            regions.Add(new MemoryRegion($"ScanList[{n + 1}]", address, 144));
        }

        regions.Add(new MemoryRegion("SmsStorageSlots", 0x01640000, 100 * 16));
        regions.Add(new MemoryRegion("SmsStorageUsedFlags", 0x01640800, 144));

        // Prefabricated SMS text: 100 entries of 208 bytes, addressed via
        // 0x02140000 + floor(n/8)*0x40000 + (n%8)*0x100 for n = 0..99.
        for (var n = 0; n < 100; n++)
        {
            var address = 0x02140000u + (uint)(n / 8 * 0x40000) + (uint)(n % 8 * 0x100);
            regions.Add(new MemoryRegion($"PrefabSms[{n + 1}]", address, 208));
        }

        regions.Add(new MemoryRegion("FmChannelsAndVfo", 0x02480000, 0x240));
        regions.Add(new MemoryRegion("FiveToneTable", 0x024c0000, 100 * 32));
        regions.Add(new MemoryRegion("FiveToneUsed", 0x024c0c80, 16));
        regions.Add(new MemoryRegion("FiveToneInfoIds", 0x024c0d00, 16 * 32));
        regions.Add(new MemoryRegion("FiveToneAndDtmfGeneralSettings", 0x024c1000, 224));
        regions.Add(new MemoryRegion("TwoToneEncode", 0x024c1100, 24 * 16));
        regions.Add(new MemoryRegion("TwoToneEncodeUsed", 0x024c1280, 16));
        regions.Add(new MemoryRegion("TwoToneEncodeGeneralSettings", 0x024c1290, 16));
        regions.Add(new MemoryRegion("ZonesUsed", 0x024c1300, 32));
        regions.Add(new MemoryRegion("RadioIdListUsed", 0x024c1320, 32));
        regions.Add(new MemoryRegion("ScanListsUsed", 0x024c1340, 32));
        regions.Add(new MemoryRegion("AlarmSettings", 0x024c1400, 128));
        regions.Add(new MemoryRegion("EncryptionIds", 0x024c1500, 576));
        regions.Add(new MemoryRegion("EncryptionKeys", 0x024c1800, 32 * 40));
        regions.Add(new MemoryRegion("AutoRepeaterOffsets", 0x024c2000, 1008));
        regions.Add(new MemoryRegion("TwoToneDecode", 0x024c2400, 512));
        regions.Add(new MemoryRegion("TwoToneDecodeUsed", 0x024c2600, 16));
        regions.Add(new MemoryRegion("AesEncryptionKeys", 0x024c4000, 255 * 64));
        regions.Add(new MemoryRegion("PowerOnAndOptionalSettings", 0x02500000, 256));
        regions.Add(new MemoryRegion("ZoneAChannel", 0x02500100, 250 * 2));
        regions.Add(new MemoryRegion("ZoneBChannel", 0x02500300, 250 * 2));
        regions.Add(new MemoryRegion("DtmfEncodeList", 0x02500500, 256));
        regions.Add(new MemoryRegion("PowerOnSettings", 0x02500600, 48));
        regions.Add(new MemoryRegion("AprsGeneralSettings", 0x02501000, 256));
        regions.Add(new MemoryRegion("AprsSendingText", 0x02501200, 64));
        regions.Add(new MemoryRegion("GpsTemplateText", 0x02501280, 64));
        regions.Add(new MemoryRegion("MoreOptionalSettings", 0x02501400, 256));
        regions.Add(new MemoryRegion("AnalogAprsList", 0x02501800, 256));
        regions.Add(new MemoryRegion("ZoneNames", 0x02540000, 250 * 32));
        regions.Add(new MemoryRegion("RadioIdList", 0x02580000, 250 * 32));
        regions.Add(new MemoryRegion("HotKeyBlock", 0x025c0000, 2864));
        regions.Add(new MemoryRegion("TalkGroupsControlData", 0x02600000, 10000 * 4));
        regions.Add(new MemoryRegion("TalkGroupListUsed", 0x02640000, 1264));
        // Doc marks the per-entry size "100 bytes per dataset? TBC" - treated as approximate.
        regions.Add(new MemoryRegion("TalkGroupList", 0x02680000, 10000 * 100));
        regions.Add(new MemoryRegion("AnalogAddressBookIndexAndUsed", 0x02900000, 384));
        regions.Add(new MemoryRegion("AnalogAddressBook", 0x02940000, 3072));
        regions.Add(new MemoryRegion("ReceiveGroupCallList", 0x02980000, 250 * 512));
        regions.Add(new MemoryRegion("BootLogo", 0x02ac0000, 40960));
        regions.Add(new MemoryRegion("LocalInformation", 0x02fa0000, 256));
        regions.Add(new MemoryRegion("StandbyBackgroundPicture1", 0x02b00000, 40960));
        regions.Add(new MemoryRegion("StandbyBackgroundPicture2", 0x02b80000, 40960));

        return regions;
    }
}
