# HamNetProgrammer

An open-source, SQLite-backed codeplug programmer for DMR radios, starting with the AnyTone AT-D878UV.

## Why

Anytone CPS and RT Systems both have real problems: CPS's Zones/Scan Lists reference channels by
identity, so any reimport of a restructured channel CSV silently breaks them and forces a manual
rebuild; RT Systems' contact importer runs out of memory on the full ~309k-row radioid.net database;
and both tools store your codeplug as an opaque binary blob. This project aims to replace both,
using SQLite as an open, inspectable source of truth instead.

## Status

Early scaffolding. `HamNetProgrammer.Cli` currently does a read-only protocol handshake: opens the
radio's serial port, starts a programming session, reads the device identifier, and reads the first
channel record — no writes yet.

## Architecture

- `src/HamNetProgrammer.Core` - domain model, SQLite persistence, binary codec, and radio transport
  (`Radios/AnyTone/AnyToneD878Transport.cs` implements the AT-D878UV's serial protocol).
- `src/HamNetProgrammer.Cli` - console entry point for testing against real hardware.
- A WinUI3 front end is planned once the data layer and protocol are proven out.

## Protocol & memory layout references

The AT-D878UV's USB protocol and binary codeplug layout are not officially documented by AnyTone.
This project relies on two independent reverse-engineering efforts (both GPL-licensed, referenced
for understanding only):

- [hmatuschek/qdmr](https://github.com/hmatuschek/qdmr) - full-featured C++ codeplug editor (no
  Windows binary, Linux-only flatpak releases).
- [reald/anytone-flash-tools](https://github.com/reald/anytone-flash-tools) - Wireshark-derived
  protocol (`at-d878uv_protocol.md`) and memory layout (`at-d878uv_memory.md`) notes, plus a Python
  radio emulator for safe testing over a virtual COM port pair (e.g. com0com on Windows).

## Build

```
dotnet build
dotnet run --project src/HamNetProgrammer.Cli -- COM5
```

## Roadmap

1. ~~Serial handshake proof of concept (read-only)~~
2. Full memory read of a real radio -> dump to SQLite as a known-good baseline
3. SQLite schema + domain model, CSV importer seeded from existing RT Systems codeplug CSVs
4. Binary encoder (SQLite model -> D878UV memory image), tested against the Python emulator first
5. Write path to real hardware, starting with low-stakes single-field writes
6. WinUI3 front end
