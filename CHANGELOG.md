# Changelog

All notable changes to VisDir are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.1] - 2026-09-07

Incremental release: lighter scans, cleaner chart, harder updater.

### Performance

- Cap scanner thread-local buffers at 4 MiB (were 16 MiB, never released).
- Size the MFT entry dictionary from the volume instead of a fixed 1M buckets;
  count files/folders inline rather than re-enumerating millions of entries.
- Replace per-file cross-core atomics and the single shared hardlink-dedup map
  with thread-local tallies and sharded dedup.
- Throttle MFT progress reports to 4 Hz; fold extension-record merges in one
  pass instead of up to eight sweeps.

### UI / UX

- Remove wedge labels from the sunburst; the center readout plus the contents
  list already carry names and sizes.
- Fix the scan progress bar: it now sweeps on real MFT-read progress and shows
  a live marquee with file/folder counts otherwise, instead of sitting near
  zero for whole folder scans.
- Cap sunburst layout to the six visible rings (was: whole subtree on the UI
  thread); drill-in animation snaps on charts over ~1500 nodes.
- Debounce the contents-list filter, drop per-row gauges, release the full
  scan tree when returning to the landing view.

### Correctness

- Saturate `TreeOps` totals instead of wrapping on overflow.
- Iterative `FsNode` path building with ownership guards; snapshot reader
  validates counts, flags and aggregates; writer supports non-seekable streams.
- MFT engine now matches the compatible engine on cloud placeholders, symlinks
  and offline files, and prunes whole-volume results to the requested subpath.

### Security

- Updater verifies Authenticode signatures (pinned thumbprint, fails closed)
  and cross-checks the published checksums manifest as a second channel.
- Elevated update helper runs from an ACL'd temp root with a validated plan;
  scanner CLI validates `--out`/`--mftprobe` arguments.
- Release workflow pins all actions to commit SHAs and splits build
  (`contents: read`) from publish (`contents: write`).

## [1.1.0] - 2026-08

- In-app auto-updater with GitHub release discovery and atomic restart.
- See GitHub Releases for earlier notes.
