# Session handoff - 2026-07-23, VM session → dev machine

Started as "carry on setting up dev tools" (finished .NET9 SDK + VS Build Tools install on this
VM), turned into a real-hardware recovery incident on the user's actual AT-D878UV II. Radio is not
yet fully recovered - the recovery is continuing on the dev machine because of suspected VMware USB
passthrough flakiness on this VM. This file is the context a fresh Claude session on the dev machine
needs to pick this up without re-deriving it.

## Where things stand right now

- The physical radio is idle, powered (on charge), and connected via COM4 - to the VM as of this
  writing, likely being moved to the dev machine directly next.
- Its codeplug content is currently at **factory defaults** (default VHF/UHF analog 2m/70cm
  channels), not the user's real configuration. It got here via an "Initialize Radio" + "default
  calibrate date" prompt the user accepted after a failed restore attempt threw mid-write - almost
  certainly the radio's own firmware reacting to an inconsistent internal state, not intentional
  data loss. The radio boots cleanly and shows sensible content, which is a good sign the actual
  hardware/calibration is fine - it just needs its real codeplug restored.
- **A full "Restore Radio Memory" run has NOT yet completed successfully** against the fixed code
  (see bugs below - each one was found via a live attempt against this real radio, and the last
  attempt before this handoff was blocked by "stuck on Identifying radio" with the radio confirmed
  idle and on the confirmed-correct port - see "Suspected VM issue" below).
- A safety backup gets taken automatically at the start of every restore attempt, always
  successfully so far (711/711 regions each time) - nothing has been left in a state with no way
  back.

## The target: what to restore

**`diagnostics/20260723_100709_ID878UV2/baseline_after.bin`** (+ matching `.manifest.csv`), right
here in the repo. This is the automatic post-write backup from a **fully successful** Write
Codeplug run this morning (`session_end: success`, 711/711 regions, freshly encoded from this
project's own SQLite source of truth) - the last known-good state, before any of today's
corruption or recovery attempts.

If running a **dev build** (executable under `src/HamNetProgrammer.Desktop/bin/...` with the
`.sln` five levels up - see `AppPaths.cs`), this file should already show up in Restore Radio
Memory's own picker automatically, since dev builds read `diagnostics`/`dumps` straight from the
repo. No copying needed - unlike on the VM, where the **installed** app reads from
`%LOCALAPPDATA%\HamNetProgrammer\` instead, requiring a manual copy of this file into that folder's
`dumps\` subdirectory to make it selectable (that's why the VM's diagnostics logs reference a
`known_good_20260723_091422.bin` filename - same content, different path, copied there as a
workaround).

## Bugs found and fixed today (all in this repo, already committed to the working tree - not yet
git-committed since this project isn't a git repo; OneDrive sync is the only persistence)

All four were found by actually running "Restore Radio Memory" against this real radio and
watching it fail in new ways each time - each one was previously invisible because the feature had
zero automated test coverage and no real verification of its own writes.

1. **Crash bug** (`AnyToneD878CodeplugRestorer.cs`, `WriteRegion` + `PlainRegionsForFullRestore`):
   `PlainRegionsForFullRestore` excluded shared-block sub-regions by NAME only, missing that
   `RawFill_*` gap-filler regions (dump-time artifacts capturing every byte, including
   undocumented gaps) can also fall inside the three shared blocks (ZoneChannelDefaults,
   RoamingBlock, GeneralUsedBitmapsBlock) without matching any known name. One such gap
   (`RawFill_0x025002f4`, 12 bytes, between ZoneAChannel and ZoneBChannel) reached the old
   16-byte-fixed `WriteRegion` loop and threw `ArgumentOutOfRangeException` mid-restore.
   **Fix**: `PlainRegionsForFullRestore` now excludes anything overlapping a shared block by
   address range (`OverlapsAnySharedBlock`), not just by name.

2. **Silent data loss** (`RestorePlainRegions`): the original version bundled ALL plain regions
   into one uncommitted session with zero verification - fine for a normal Write Codeplug (small
   total), but a full restore's plain-region total (with RawFill included) exceeded 1.3MB, which is
   already-proven unsafe territory (see `WriteRegionChunkedAndVerify`'s remarks: writing too much
   in one session gets silently ACKed and discarded, no error, only visible on independent
   read-back). A real run showed `RadioIdList` (contacts/talkgroups), `ZoneNames`, and `HotKeyBlock`
   all silently wrong despite a "successful" run - this is very likely why the user's talkgroups
   were scrambled (description vs. keyed TG mismatched) even after an apparently-clean restore.
   **Fix**: batch plain regions under a 100,000-byte budget
   (`AnyToneD878CodeplugRestorer.PlainRegionMaxBytesPerSession`, chosen because that's the largest
   single commit already proven safe by TalkGroupList's real writes), verify each batch via a fresh
   read-only session, retry up to 3x, throw a clear error if it never verifies.

3. **Splitting one region across sessions is unsafe** (found live, right after fix #2 above): the
   first attempt at fix #2 also split any INDIVIDUAL region bigger than the budget across multiple
   committed chunks (mirroring ChannelBank/TalkGroupList's chunking). `Zones` (128,000 bytes) then
   failed verification the one time this got tried, despite always writing correctly as ONE
   unsplit commit in every prior session ever observed (including normal Write Codeplug runs).
   Almost certainly the same family of bug as this project's other erase-block-disturb findings: a
   second commit into the same erase block wipes out what the first one just wrote.
   **Fix**: a region's own bytes are now NEVER split across sessions, regardless of size. The byte
   budget only controls how many *different* small regions get bundled together; anything bigger
   than the budget by itself just gets its own solo, unsplit, verified session.

4. **RawFill is genuinely unsafe to restore, not just untested** (found live, right after fix #3):
   even written as ONE whole unsplit commit, `RawFill_0x02480240` (261,568 bytes of completely
   undocumented flash, sitting right at the edge of a range this codebase's own notes already flag
   as quirky - the "wide settings sweep" RT Systems is documented writing very carefully) failed
   verification 3/3 times. This size sits in an untested gray zone between the 100,000 bytes
   already proven safe and the ~558,000 already proven unsafe (TalkGroupList).
   **Fix**: `PlainRegionsForFullRestore` now excludes ALL `RawFill_*` regions from restore
   entirely - they're undocumented padding with no user-facing meaning (not a zone, channel,
   contact, or setting anyone configured), captured only so a *backup* is forensically complete.
   Restoring them has now caused real corruption twice; not worth it. `DumpComparer.Compare` was
   also updated to skip `RawFill_*` and any shared-block-overlapping region when comparing dumps,
   so the final post-restore verification doesn't report these expected, by-design differences as
   false failures (previously reported "24 mismatches" that were almost entirely this category,
   burying the two genuine ones).

5. **Cleanup on failure** (`RadioPage.xaml.cs`, `OnRestoreRadioMemoryClicked`'s catch block): didn't
   release the radio if a failure happened before the commit point, leaving it in an open
   programming session. Fixed to `Close()` (without sending `END`) in that case - writes are
   buffered and only actually committed to flash on `EndProgrammingSession` (confirmed on real
   hardware, see `WriteMemory`'s doc comment), so this avoids accidentally committing a known-bad
   partial state.

**All five fixes are built and packaged** in `dist\HamNetProgrammer-Setup-0.2.0.exe` (rebuilt
multiple times today, most recent build includes all five). Should be usable as-is on the dev
machine, or rebuild from source via `installer\build.ps1` to be safe.

## Suspected VM issue (not a code bug)

The last attempt before this handoff got stuck on "Identifying radio..." with the radio confirmed
idle at its normal screen and the port confirmed still COM4 - ruling out the two usual causes
(radio not booted, port renumbered). This smells like VMware USB passthrough flakiness (timing
races in port re-enumeration, or the auto-connect setting not catching every reattachment) rather
than anything in the app. Recommended moving the physical USB connection to the dev machine
(bare metal) to eliminate that whole category of friction - none of the four real bugs above were
VM-related, but this connectivity issue plausibly is.

## Other things touched today, not urgent

- **`README.md`**: Status section and the AT-D878UV table row now distinguish the well-proven
  paths (Read, Write Codeplug, Restore Previous Codeplug) from Restore Radio Memory's actual
  incident history, instead of one blanket "hardware-verified" claim. Worth revisiting once
  Restore Radio Memory has some real test coverage and a calmer track record.
- **`WISHLIST.md`** (new file, repo root): currently has one entry - no Cancel button on Restore
  Radio Memory's "Identifying radio..." step (safe to force-close there today, but bad UX). Also
  flagged in conversation but not yet written up: the progress bar during a full restore counts
  named-region completion, not bytes/time, so it races to ~95% almost immediately (hundreds of
  tiny regions) then appears stuck while the few large, slow operations (ChannelBank/TalkGroupList
  isolated sessions) actually run - a two-tier progress bar (overall + per-phase) was discussed as
  the right fix, not yet implemented or added to the wishlist file.
- **Reboot count**: each isolated write+verify cycle is a real ~10-15s USB drop/re-enumerate. A
  full Restore Radio Memory run is now on the order of 100+ reboots (down somewhat from before
  since RawFill's large blobs no longer need their own sessions, but up from the original ~90
  since the plain-region phase now properly verifies instead of trusting one blind commit). All
  graceful, firmware-initiated reboots (not power-loss risk) but real wall-clock time - run this on
  mains power/charge, not battery.
- Zero automated test coverage still exists for any of this restore logic - every one of the four
  bugs above was found by writing to a real user's radio, not by CI. Worth prioritizing before this
  goes anywhere near a general release, especially given how many real-hardware incidents this one
  feature specifically has caused (2026-07-17, 07-19, and four more today).
