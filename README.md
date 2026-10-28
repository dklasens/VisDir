# VisDir

<img width="1437" height="866" alt="image" src="https://github.com/user-attachments/assets/0a03713f-3a6c-4192-9495-61d601c08150" />

> A fast, modern disk space visualizer and analyzer for Windows, inspired by DaisyDisk.

Built with **C# (.NET 10)**, **WPF**, and **SkiaSharp** for high-performance harmonic sunburst rendering, paired with a **raw NTFS Master File Table ($MFT)** scanning engine.

---

## Features

- **Interactive Sunburst Visualization**: Real-time multi-ring sector rendering with harmonic palettes, center directory summaries, and smooth drill-down navigation.
- **Dual Scanning Engines**:
  - **Fast NTFS (MFT Engine)**: Reads raw `$MFT` structures sequentially in 32 MiB streaming buffers for fast whole-drive indexing.
  - **Compatible Engine**: High-speed multi-threaded batch scanner (`GetFileInformationByHandleEx`) supporting subfolders, external drives (FAT32/exFAT), and network UNC paths.
- **Consistent hardlink accounting**: Both engines retain real directory entries and attribute shared allocation to the same deterministic path within the selected scan. NTFS metadata and restricted locations can still add entries visible only to Fast NTFS.
- **Explorer & Shell Integration**:
  - Right-click any sector on the sunburst or item in the contents list to instantly **Reveal in File Explorer** or **Copy Full Path**.
- **Simple Browsing**: Click segments to explore, expand smaller objects, filter every matching entry, and open the selected item in Explorer. Lists use 200-item pages and the chart has a fixed rendering budget, including folders of cloud placeholders.
- **Accurate Space Accounting**:
  - Accounts for physical cluster rounding, resident `$DATA` exclusion, sparse/compressed on-disk sizes, and Alternate Data Streams (ADS).
  - Reparse-tag-gated junction/symlink protection — cloud placeholders (OneDrive/Dropbox) scan as ordinary folders, with non-resident file content counting zero on-disk bytes.
  - Deterministic hardlink attribution (same folder sizes on every rescan); MFT failures fall back to the Compatible engine instead of failing the scan.
- **Privacy & Performance**: Zero telemetry and analytics. VisDir only connects to GitHub to check for updates, and otherwise processes scan data locally.

---

## Download & Quick Start

Download standalone self-contained packages from the [Releases](https://github.com/dklasens/VisDir/releases) page:

1. Download **`VisDir-win-x64.zip`** (or `VisDir-win-arm64.zip` for ARM devices), or use the x64 MSI installer.
2. If using a ZIP, extract the archive.
3. Run **`VisDir.App.exe`** (the MSI adds a Start menu shortcut).

When VisDir opens, Windows requests administrator permission before the main window appears. Approve it once, then choose a disk and click **Scan**, or choose a folder. Automatic NTFS drive scans read the MFT directly without another UAC prompt; folder scans and other filesystems use Compatible by default. The scanner runs in a separate process and inherits the app?s permissions. Cancelling the startup UAC prompt closes the app. Unsupported or incomplete MFT reads fall back to Compatible and report the reason.
