# AT-D878UV memory map

What this project actually knows about the AnyTone AT-D878UV's flash layout, and - more
importantly - the hazards that aren't obvious from the layout alone. AnyTone doesn't publish this
information; what's here was built from two independently-documented open-source projects
([hmatuschek/qdmr](https://github.com/hmatuschek/qdmr) and
[reald/anytone-flash-tools](https://github.com/reald/anytone-flash-tools), both linked from the
main README) and confirmed directly against real hardware wherever the two disagreed or left
something ambiguous. Every address below is either drawn from those sources or independently
confirmed on a real D878UV II Plus - see the git history for the byte-level validation work,
especially around the TX Color Code and erase-block-disturb incidents referenced throughout this
document.

This is a living document for contributors, not end-user documentation. If you're extending
support to another AnyTone model, start with `AnyToneD878MemoryMap.cs` and
`AnyToneD878CodeplugEncoder.cs` - the source is more current than this file can ever be kept.

## The one thing to understand before touching any of this

**Flash erases in 256KB-aligned blocks.** Writing even one byte inside a `0x40000`-aligned span
erases the *entire* block, not just the bytes you wrote. If anything you don't know about lives in
that same block, it gets wiped as a side effect - not a bug in the write, just how NOR flash works.
This project got burned by it twice before building real defenses (see "Shared erase blocks"
below). Before adding a new region: check whether its address falls inside a `0x40000`-aligned span
already occupied by something else. If it does, you cannot write it standalone - you need to read
the whole block live, splice in your change, and write the merged result back in one session
(`AnyToneD878CodeplugWriter`'s `WriteAndVerifySharedBlock` and the three `Build*Region` helpers are
the established pattern).

## Top-level region map

Addresses and lengths as defined in `AnyToneD878MemoryMap.GetBaselineRegions()` and
`AnyToneD878CodeplugEncoder`. "Encoder" is where the write-side addressing lives - the memory map
mirrors it region-for-region so a dump/backup/restore always matches what a write would touch.

| Region | Address | Layout |
|---|---|---|
| Channel banks | `0x00800000`, stride `0x40000` | 32 banks × 128 channels (bank 31 only 34) + TX Color Code table per bank, see below |
| Zones (membership) | `0x01000000` | 250 × 512 bytes |
| Zone names | `0x02540000` | 250 × 32 bytes (separate from membership - a zone's name is NOT in its 512-byte record) |
| Zones used bitmap | `0x024c1300` | 32 bytes, standard convention (1 = used) |
| Roaming channels | `0x01040000` | 250 × 32 bytes |
| Roaming channels used | `0x01042000` | 32 bytes |
| Roaming zones used | `0x01042080` | 16 bytes (only 64 zones actually usable) |
| Roaming zones | `0x01043000` | 64 × 128 bytes |
| Scan lists | `0x01080000` | 250 slots, address = `base + (n/16)*0x40000 + (n%16)*0x200`, 144 bytes each |
| Scan lists used | `0x024c1340` | 32 bytes |
| SMS storage / prefab text | `0x01640000` / `0x02140000` | not written by this project; present for completeness |
| Zone A/B default channel, power-on settings, APRS, DTMF list | `0x02500000`, length `0x1900` | **shared erase block**, see below |
| Radio IDs used bitmap | `0x024c1320` | 32 bytes |
| Radio IDs | `0x02580000` | 250 × 32 bytes |
| Talkgroups control data | `0x02600000` | 10000 × 4 bytes, redundant with the used bitmap, not needed for decode |
| Talkgroups used bitmap | `0x02640000` | 1264 bytes, **inverted convention** (0 = used), see below |
| Talkgroup list | `0x02680000`, stride `0x40000` | banked, 10 banks × 1000 contacts × 100 bytes, see below |
| Contact ID routing table | `0x04800000` | **D878UV II Plus-specific address** - see "Per-model quirks" below |
| Group lists | `0x02980000` | 250 × 512 bytes, no used bitmap at all, see below |
| Boot logo | `0x02ac0000` | 40960 bytes, 160×128 uncompressed RGB565 |
| Standby background images | `0x02b00000` / `0x02b80000` | 40960 bytes each |

Everything between `0x02480000` and `0x02600000` that isn't named above (FiveTone/TwoTone tables,
alarm settings, encryption keys, auto-repeater offsets, etc.) is covered by generated
`RawFill_0x...` regions in the memory map, purely for forensic dump/diff completeness - this
project doesn't understand or write any of it.

## Shared erase blocks (the hazard, concretely)

Three 256KB blocks are known to contain more than this project writes, confirmed the hard way on
real hardware:

- **`0x01040000`** (roaming): channels + both used bitmaps + zones all live here. Used extent is
  only `0x5000` of the full `0x40000` block; the rest is genuinely unused, matching what official
  AnyTone CPS itself leaves untouched.
- **`0x024C0000`** (general used-bitmaps block): Zones/Radio IDs/Scan Lists used bitmaps share this
  block with FiveTone, TwoTone, DTMF, alarm, and encryption tables this project never writes. Used
  extent `0x8100`.
- **`0x02500000`** (zone channel defaults): power-on password flag, welcome message, APRS settings,
  DTMF encode list, and the two default-channel-per-zone selectors all share this block. Used
  extent `0x1900`.

**Write order matters between two of these.** `0x024C0000 + 0x8100`... doesn't reach `0x02500000`,
but the underlying *erase block* boundaries do: `0x024C0000`'s containing block and `0x02500000`
are physically contiguous on the same flash die. Writing the general-used-bitmaps block was proven
(2026-07-19) to corrupt an already-good, untouched zone-channel-defaults block purely as an
erase-disturb side effect. **`ZoneChannelDefaults` must always be written last** among the three -
session isolation and verify-retry on it alone can't help if something else erases its neighbor
afterward.

Each of these three gets its own isolated programming session (own `Start`/`End`, own
re-enumerate wait) and a live read-modify-write rather than a standalone write - see
`AnyToneD878CodeplugWriter.WriteAndVerifySharedBlock` and the three `Build*Region` methods.

## Channel banks and TX Color Code

`ChannelBankBaseAddress = 0x00800000`, `ChannelBankStride = 0x40000`, 32 banks, 128 channels/bank
(`0x2000` bytes), except bank 31 which only covers the remaining 34 slots (4000 real channels + 2
VFO pseudo-channels, channel numbers 4001/4002 - not real CPS-editable channels, see "VFO
pseudo-channels" below).

**The TX Color Code table sits immediately after the primary channel table in the same bank**, at
offset `0x2000` within the bank (i.e. `bankBase + 0x2000 + slot*64 + 3`, one byte per channel, same
64-byte stride as the primary record) - and, critically, **inside the same 256KB erase block** as
the primary channels. AnyTone firmware V3.06+ (2025-01-23) split Color Code into separate RX and TX
fields in the CPS UI; this project (and qdmr, as of the version checked) only ever modeled one
Color Code field, which the firmware actually uses as RX/display only. The radio *transmits* on TX
Color Code, which - if never explicitly written - reads back as an erased nibble (`0xF` = 15), a
plausible-looking but wrong value. This was the root cause of a multi-day "radio transmits fine but
no MMDVM hotspot ever hears it" investigation. Because both tables share one erase block, they must
always be written together as one combined region (see `EncodeChannels`'s remarks) - which is
exactly why `AnyToneD878MemoryMap` defines `ChannelBank[N]` as one region covering both halves, not
two.

## Banked / indexed structures

Three structures use bank-style addressing rather than one flat array - a naive flat read or write
silently misaligns anything past the first bank:

- **Talkgroup list**: `ContactsPerBank = 1000`, banks spaced `0x40000` apart starting at
  `0x02680000`. `AnyToneD878MemoryMap` defines 10 banks (`TalkGroupList[0..9]`) covering the full
  10,000-contact range the used bitmap and control-data table assume. A single flat
  1,000,000-byte read from `0x02680000` (this project's own bug until 2026-07-22) only ever
  captures real data for the first ~1000 contacts before running into unrelated flash content.
- **Scan lists**: address = `0x01080000 + (n/16)*0x40000 + (n%16)*0x200` for `n` in 0..249 - 16
  slots per 256KB-aligned column.
- **Prefab SMS text** (not written by this project, present in the memory map for completeness):
  same column-of-16 pattern, `0x02140000` base, `0x100` stride within a column.

**The talkgroup-used bitmap uses an inverted convention** - pre-filled `0xFF`, a *clear* bit means
used (`ClearBit`), the opposite of every other `*Used` bitmap in this format (which use `1` = used,
`SetBit`). Confirmed against real data; not a guess.

**Group Lists have no used bitmap at all.** `0x02980000`, 250 × 512 bytes, one region per possible
slot, but nothing marks which slots are populated - only rows that exist in the source database get
written ("empty groups will not be written," matching the doc's own convention elsewhere), and
occupancy on decode has to be inferred (a real encoded record always has at least one `0x00` byte
from name null-padding, so an entirely-`0xFF` record reliably means never-written).

## Per-record byte layouts

Canonical definitions live in `Core/Radios/AnyTone/Codecs/*.cs` - each file's own doc comment
explains how its layout was confirmed. Summary:

| Record | Length | Key fields |
|---|---|---|
| Channel | 64 bytes | RX/TX freq (4-byte BCD each, 10Hz units, offset+sign encoded not literal TX freq), mode/power/bandwidth flags @8, CTCSS config @9-11, **fixed digital template `11 00 11 00 cf 09` @12-17 for digital channels** (not analog/custom-CTCSS fields as first assumed), ContactIndex (uint32 LE) @20, RadioIdIndex @24, ScanListIndex @27 (`0xFF`=none), GroupListIndex @28 (`0xFF`=none), ColorCode @32, TimeSlot bit0 of byte 33, Name (16 ASCII) @35, **through-mode/DMO-simplex bit1 of byte 52**, **byte 57 must be `0x00`, not `0xFF`** (see below) |
| Zone membership | 512 bytes | up to 256 × uint16 LE flat channel indices, `0xFFFF` = unused; name is a *separate* region (`ZoneNames`), not part of this record |
| Scan List | 144 bytes | priority-channel-select derived from which priorities are set (not independent), priority ch1/ch2 (uint16 LE), 4 timing bytes (`seconds*10`, range 0.1-5.0s), revert mode enum, Name (16 ASCII) @10, up to 50 member channel indices (uint16 LE) @26 |
| Talkgroup | 100 bytes | CallType @0, Name (35 ASCII) @1, DMR ID (3-byte BCD, **6-digit hardware ceiling**) @36 |
| Radio ID | 32 bytes | DMR ID (4-byte BCD) @0, Callsign (26 ASCII) @5 |
| Group List | 512 bytes | up to 64 member indices into the talkgroup list (uint32 LE) @0, `0xFFFFFFFF` = unused, Name (16 ASCII) @256 |
| Roaming Zone | 128 bytes | up to 64 member indices (byte) into the roaming-channel table @0, `0xFF` = unused, Name (16 ASCII) @64 |
| Roaming Channel | 32 bytes | RX/TX freq (4-byte BCD each) @0/4, ColorCode @8, Slot @9 (**0/1, not the usual 1/2 convention**), Name (16 ASCII) @10 |

BCD fields throughout use natural digit order (byte 0 = most significant digit pair) - see
`BcdCodec.cs`. An all-`0xFF` (erased) BCD field decodes to a large nonsense value, not zero - useful
for detecting blank/erased slots when no used-bitmap exists (each `0xFF` byte contributes 165 per
BCD digit pair on decode).

## VFO pseudo-channels

Channel numbers 4001 and 4002 (flat indices 4000/4001, the last two slots of bank 31) are the
radio's live VFO A/B tuned-frequency pseudo-slots, not real CPS-editable channels. They always
decode to a plausible-looking frequency (whatever's currently tuned), so anything inferring channel
occupancy by frequency range needs to explicitly exclude channel numbers above 4000.

## Per-model quirks

The **contact ID routing table** - what the firmware actually consults for DMR call routing, as
opposed to the display-only talkgroup list - lives at **`0x04800000`** on the D878UV II Plus
(device identifier containing `878`). Several sources, including qdmr's base D868UV-family class,
describe `0x04340000` instead - that address is real, but for a different, related model in the
same family, not this one. Writing the routing table to the wrong address is a quiet failure mode:
the channel display and talkgroup list both look completely correct, but the radio transmits on the
wrong talkgroup. Confirmed via captured USB traffic from official AnyTone CPS writing to a real
D878UV II Plus.

## Write reliability, not just correctness

Independent of getting an address right, this project found empirically that **large
single-session writes have a reliability ceiling somewhere around 200-250KB** - past that, some
writes silently fail to commit even though every individual `WriteMemory` command is ACKed without
error. This is why the talkgroup list, channel banks (bundled with TX Color Code), and the three
shared erase blocks above are all isolated into their own smaller, independently
committed-and-verified sessions rather than bundled into one large write. `WriteMemory` itself is
also purely buffered - nothing commits until `EndProgrammingSession`, so a same-session
read-after-write is a guaranteed false positive for verification, and ending *any* session (even a
read-only one) causes the radio to drop off USB and re-enumerate, typically 10-30 seconds.
