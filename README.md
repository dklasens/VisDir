# VisDir

> A fast, modern disk space visualizer and analyzer for Windows, inspired by DaisyDisk.

Built with **C# (.NET 8)**, **WPF**, and **SkiaSharp** for GPU-accelerated harmonic sunburst rendering, paired with a WizTree-class **raw NTFS Master File Table ($MFT)** scanning engine.

---

## Features

- **Interactive Sunburst Visualization**: Real-time GPU-accelerated multi-ring sector rendering with harmonic palettes, center directory summaries, and smooth drill-down navigation.
- **Dual Scanning Engines**:
  - **Fast NTFS (MFT Engine)**: Reads raw `$MFT` structures sequentially in 32 MiB streaming buffers for sub-second whole-drive indexing.
  - **Compatible Engine**: High-speed multi-threaded batch scanner (`GetFileInformationByHandleEx`) supporting subfolders, external drives (FAT32/exFAT), and network UNC paths.
- **Explorer & Shell Integration**:
  - Right-click any sector on the sunburst or item in the contents list to instantly **Reveal in File Explorer** or **Copy Full Path**.
- **Live Filtering & Breadcrumbs**: Search and filter large directories in real time with proportional capacity gauges.
- **Accurate Space Accounting**:
  - Accounts for physical cluster rounding, resident `$DATA`, and Alternate Data Streams (ADS).
  - Reparse point and directory junction loop protection.
  - Cloud-filter awareness (zeroes non-resident OneDrive placeholders to reflect true physical disk occupancy).
- **Privacy & Performance**: Fully offline, zero telemetry, zero analytics, and low memory footprint.

---

## Download & Quick Start

Download standalone self-contained packages from the [Releases](https://github.com/dklasens/VisDir/releases) page:

1. Download **`VisDir-win-x64.zip`** (or `VisDir-win-arm64.zip` for ARM devices).
2. Extract the archive.
3. Run **`VisDir.App.exe`**.

> **Note**: To use the instant **Fast NTFS ($MFT)** engine on whole drives, launch VisDir as Administrator. When run as a standard user, VisDir automatically uses the multi-threaded Compatible engine.

---

## Keyboard Shortcuts & Navigation

| Action | Shortcut / Gesture |
| :--- | :--- |
| **Drill Into Folder** | Double-click item or click Sunburst sector |
| **Navigate to Parent** | Click Center Circle or press <kbd>Backspace</kbd> |
| **History Back / Forward** | <kbd>Alt</kbd> + <kbd>←</kbd> / <kbd>Alt</kbd> + <kbd>→</kbd> |
| **Reveal in File Explorer** | Right-click sector/item → *Reveal in File Explorer* |
| **Copy Path to Clipboard** | Right-click sector/item → *Copy Full Path* |
| **Filter Contents** | <kbd>Ctrl</kbd> + <kbd>F</kbd> |
| **Rescan Current Folder** | <kbd>F5</kbd> |
| **Cancel Scan / Clear Filter** | <kbd>Esc</kbd> |

---

## Building from Source

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.400 or later)
- Windows 10/11 (x64 or ARM64)

### Build & Run
```powershell
# Clone repository
git clone https://github.com/dklasens/VisDir.git
cd VisDir

# Build solution
dotnet build VisDir.sln -c Release

# Run tests
dotnet test VisDir.sln

# Launch app
dotnet run --project src/VisDir.App -c Release
```

### Packaging Standalone Releases
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Runtime win-x64
```

---

## Project Structure

- **`src/VisDir.Core`**: Core filesystem data models (`FsNode`), snapshot serializer (`TreeSerializer`), and scanning engines (`NtfsMftScanner`, `GenericScanner`).
- **`src/VisDir.App`**: WPF UI application containing the SkiaSharp sunburst canvas (`SunburstControl`), history navigation, search filters, and dark-themed styles.
- **`src/VisDir.Scanner`**: Standalone CLI worker executable for isolated, elevated scanning and benchmarking.
- **`benchmarks/VisDir.Benchmarks`**: Synthetic and drive throughput benchmarking tools.
- **`tests/VisDir.Core.Tests`**: Test suite covering record parsing, tree aggregation, serialization, and geometry.

---

## License

This project is licensed under the [MIT License](LICENSE).
