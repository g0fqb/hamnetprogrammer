# Wishlist

Small improvements noticed in passing - not scoped/prioritized like the README's Roadmap, just a
running list to pick up whenever.

- **Cancel button on Restore Radio Memory's "Identifying radio..." step.** Found 2026-07-23: if the
  radio isn't actually listening yet (e.g. still mid-boot/stuck on a hardware test screen), the
  identify step (`Open()` + `StartProgrammingSession()` + `ReadDeviceId()` in
  `RadioPage.xaml.cs`'s `OnRestoreRadioMemoryClicked`) just hangs with no way out except force-
  closing the whole app. Safe to force-close at that specific point (no backup/write has started
  yet, so nothing destructive is in flight, and Windows releases the COM port on process exit
  regardless) - but a proper Cancel button (wire up a `CancellationToken` through the identify
  `Task.Run`) would be much better UX. Same gap likely exists on Write Codeplug's and Read
  Codeplug's identify steps too - worth checking those while in there.
