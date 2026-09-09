# Changelog

All notable changes to VisDir are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.2.4] - 2026-09-09

### Fixed

- Both engines bill recall-marked but resident files instead of zeroing them.
  Seen live: a 189 GB game install carrying `RECALL_ON_OPEN` with fully
  allocated runs was hidden to 0 bytes by the placeholder rule. Allocation
  numbers are on-disk truth; dehydrated placeholders already report 0 either
  way, so both engines now agree with Explorer and each other.
- Split-namespace names merge by rank across extension records: a base record
  holding only a DOS-mangled `$FILE_NAME` (`CALLOF~1`) adopts the WIN32 name
  from its extension instead of rendering the mangled name.

## [1.2.3] - 2026-09-08

### Fixed

- Updater download genuinely fixed: v1.2.1 chased concurrent temp paths, but
  the full progress bar showed the bytes always landed and the failure came
  after — at the final rename, which needs DELETE access that a just-finished
  antivirus/indexer scan withholds. The artifact is now verified and used in
  place (no rename); lock retries cover slow scans (sharing and denied,
  ~11 s envelope); the finished archive is cleaned up after staging.
- Download failures now log type, HResult and stack to
  `%TEMP%\VisDir\Updates\download-error.log` and show the error kind in the
  dialog, so any recurrence is diagnosable without guessing.

## [1.2.2] - 2026-09-08

### Fixed

- FAST NTFS no longer bills `$BadClus` at volume size (1.9 TB phantom):
  record #8 is zeroed unconditionally after the extension fold. Its `$Bad`
  stream is named, so it never sets the sparse flag that gated the old guard,
  and fold ADS-summing could resurrect bytes past a pre-fold guard.
- FAST NTFS no longer silently drops the MFT tail: a short pipelined read
  falls through to the synchronous resume, and unexpected short/failed reads
  fail closed into the generic fallback instead of presenting a partial tree.
  Record-key collisions are now counted (`MFTSTAT dup=`).

## [1.2.1] - 2026-09-08

### Fixed

- Updater no longer fails with "being used by another process" when two
  downloads overlap (second instance, reopened dialog, cancel-then-retry):
  each attempt writes a unique partial path, the dialog refuses re-entrant
  downloads, and file open/move ride out transient locks (AV/indexer) with
  retries. Stale partials older than two days are swept at download start.

## [1.2.0] - 2026-09-08

### Fixed

- MFT engine no longer drops cloud-placeholder subtrees (OneDrive/Dropbox):
  descent is gated on the reparse tag (mount point/symlink only), and the tag
  is plumbed through `MftEntryInfo.ReparseTag`.
- Sparse and compressed files report on-disk bytes (offset `0x40`) instead of
  the hole-inclusive VCN range; `$BadClus` zeroing kept as a sparse-only guard.
- Closed an unsigned-overflow out-of-bounds read in the resident bounds checks;
  fixup derives the sector stride from the record instead of assuming 512 B.
- Hardlinked files take name and parent from the same `$FILE_NAME`; DOS 8.3
  names no longer inflate the link count; extension records fold name/parent.
- Generic engine attributes hardlinks deterministically (lexicographically
  first path keeps the bytes); placeholder handling no longer depends on
  per-process disguise luck; extd-class downgrade is scoped per volume.
- MFT failure falls back to the generic engine instead of exiting with code 3.
- Snapshot reader clamps `childCount` against nodes remaining (no giant
  preallocation from a crafted file). `SizeFormatter` gains TB/PB.

### Performance

- Elevated scans run in-process (no child + snapshot round trip); worker
  snapshots load with `recomputeTotals: false`; deleted MFT slots skip parsing.
- Generic per-worker buffers drop from 1 MiB to 64 KiB; `$MFT` handle uses
  `SEQUENTIAL_SCAN`.
- Navigation layouts run off the UI thread with stale-sequence abandon;
  search reconciles in bulk under a 1000-item cap.
- Benchmark measures the dictionary sink with deleted/extension records and
  per-phase timing; `--diff` tolerance tightened 5% to 1% with the engine
  accuracy contract documented (MFT counts index + ADS, generic unnamed
  `$DATA` only).

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
