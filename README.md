# HamNetProgrammer

An open-source, SQLite-backed codeplug programmer for DMR radios, starting with the AnyTone AT-D878UV.

## Why

Anytone CPS and RT Systems both have real problems: CPS's Zones/Scan Lists reference channels by
identity, so any reimport of a restructured channel CSV silently breaks them and forces a manual
rebuild; RT Systems' contact importer runs out of memory on the full ~309k-row radioid.net database;
and both tools store your codeplug as an opaque binary blob. This project aims to replace both,
using SQLite as an open, inspectable source of truth instead.

## Status

A working WinUI3 desktop application, hardware-verified against a real AT-D878UV. Not yet a general
release - see [Roadmap](#roadmap).

### Features

- **Zones, Channels, Scan Lists, Group Lists, Roaming Zones** - full read/write, with each of
  Scan/Group/Roaming independently enable-able (or fully skipped) from the **Lists** page, and a
  "Sync Lists with Zones" automation that derives them from zone membership rather than maintaining
  them by hand.
- **Digital Contacts** - talkgroup import from a shared backend (Brandmeister + TGIF + FreeDMR,
  merged and deduped by DMR ID - the radio has no concept of "network", so the same number from two
  sources is one contact, not two), with live search across the imported list.
- **Radio Settings** - GPS and APRS (Digital reporting via a DMR talkgroup), written to the radio.
  A few fields (GPS Mode, APRS Sending Text, GPS Template Text) are saved but deliberately not
  written yet - no independently-confirmed byte offset exists for them on the D878UV in either
  reference source checked, and this project would rather leave a field unwritten than guess at an
  unverified flash offset.
- **Write safety** - a risk-tiered disclaimer scaled to how well a given radio model's memory map is
  actually verified (typed confirmation required for anything not hardware-validated), an automatic
  pre/post-write memory backup on every write, and a one-click "Restore Previous Codeplug" that
  undoes the last write using that backup.
- **Diagnostic reporting** - if a write goes wrong, the automatic backup + a log of every region
  written can be sent with one click for investigation.
- **Contribute a memory-map sample** - read-only capture for radio models this project has no
  hardware to validate against, uploaded to help extend support to them.

### Supported radios

| Model | Status |
|---|---|
| AnyTone AT-D878UV | Full read/write, hardware-verified. |
| AnyTone AT-D868UV / AT-D578UV / AT-D890UV | Believed protocol-compatible (same vendor SDK family) but **not hardware-verified** - writes require typed confirmation of the risk. Read-only memory-map contribution is the safest way to help extend real support. |
| TYT MD-380 / MD-390 / MD-UV380 / MD-UV390 / MD2017, Baofeng DM-1701 | DFU-mode transport layer proven against real MD-380 hardware (see `HamNetProgrammer.Cli`'s `tyt-identify`/`tyt-dump` commands) - **read-only, CLI-only, no codeplug decode/write yet.** This is a real reverse-engineered protocol (see qdmr's `tyt_codeplug.cc`), just not wired into the desktop app or GUI. |

## Architecture

- `src/HamNetProgrammer.Core` - domain model, SQLite persistence, binary codecs, and radio
  transports:
  - `Radios/AnyTone/` - the AT-D878UV's serial (COM-port) protocol, encoder/decoder, and
    read-modify-write handling for flash regions shared with other data.
  - `Radios/TyT/` - the MD-380-family DFU/USB protocol (`TytDfuTransport`), transport-layer only.
  - `Online/` - talkgroup import from the shared backend.
  - `Diagnostics/` - write-session audit logging, risk-tier catalog, diagnostic report packaging.
- `src/HamNetProgrammer.Desktop` - the WinUI3 application.
- `src/HamNetProgrammer.Cli` - console entry point, used for hardware validation and one-off
  operations (`dump`, `write-codeplug`, `tyt-identify`, `tyt-dump`, etc.) without a full UI session.

## Protocol & memory layout references

The AT-D878UV's USB protocol and binary codeplug layout are not officially documented by AnyTone.
This project relies on two independent reverse-engineering efforts (both GPL-licensed, referenced
for understanding only):

- [hmatuschek/qdmr](https://github.com/hmatuschek/qdmr) - full-featured C++ codeplug editor covering
  AnyTone, TYT/Retevis, Radioddity, and others (no Windows binary, Linux-only flatpak releases) -
  also the source for the MD-380-family DFU/TyT protocol and memory layout.
- [reald/anytone-flash-tools](https://github.com/reald/anytone-flash-tools) - Wireshark-derived
  protocol (`at-d878uv_protocol.md`) and memory layout (`at-d878uv_memory.md`) notes, plus a Python
  radio emulator for safe testing over a virtual COM port pair (e.g. com0com on Windows).

## Build & run

```
dotnet build
```

### Packaging an installer

```
powershell -File installer\build.ps1
```

Publishes a fresh self-contained build (the exact flags this WinUI3 app shape needs are baked into
the script and `HamNetProgrammer.Desktop.csproj`'s comments - a plain `dotnet publish` produces a
build that runs here but fails on a clean machine) and wraps it with
[Inno Setup](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) into a
single installer under `dist\`. Per-user install, no admin/UAC prompt required.

For hardware testing without the full UI:

```
dotnet run --project src/HamNetProgrammer.Cli -- COM5              # read-only handshake + dump
dotnet run --project src/HamNetProgrammer.Cli -- write-codeplug COM5
dotnet run --project src/HamNetProgrammer.Cli -- tyt-identify      # MD-380-family, DFU mode
```

## Contributing a memory-map sample

If you own an AnyTone or TYT-family radio not listed as hardware-verified above, the desktop app's
Radio page has a **Contribute a memory-map sample** button - it only reads (the same operation
Backup uses), never writes, and uploads the result to help extend real support to your model.

## Roadmap

1. ~~Serial handshake proof of concept (read-only)~~
2. ~~Full memory read of a real radio -> dump to SQLite as a known-good baseline~~
3. ~~SQLite schema + domain model~~
4. ~~Binary encoder (SQLite model -> D878UV memory image)~~
5. ~~Write path to real hardware~~
6. ~~WinUI3 front end~~
7. ~~Digital Contacts (shared talkgroup backend)~~
8. Remaining CPS-parity settings categories (Digital/VOX, Key Func/Hot Key, boot logo)
9. Real read/write support for a second radio family (MD-380-family codeplug decode, building on
   the proven DFU transport)
10. Packaged installer / first public release
