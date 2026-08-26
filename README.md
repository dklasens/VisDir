# VisDir 💽✨

> **A fast, beautiful, DaisyDisk-inspired disk space visualizer and analyzer for Windows.**

Built with **C# (.NET 8)**, **WPF**, and **SkiaSharp** for GPU-accelerated harmonic sunburst rendering, paired with a WizTree-class **raw NTFS Master File Table ($MFT)** scanning engine.

---

## ✨ Features

- 🌈 **DaisyDisk-Inspired Interactive Sunburst**: Real-time GPU-accelerated multi-ring sector visualization with harmonic pastel palettes and smooth drill-down navigation.
- ⚡ **Blazing Fast Scanning Engines**:
  - **Fast NTFS (MFT Engine)**: Reads raw `$MFT` structures sequentially in 32 MiB streaming buffers for sub-second whole-drive indexing.
  - **Compatible Engine**: High-speed multi-threaded batch scanner (`GetFileInformationByHandleEx`) supporting subfolders, USB keys (FAT32/exFAT), and network shares (UNC).
- 🖱️ **Full Context Menu & Explorer Integration**:
  - Right-click any blob/wedge on the sunburst or item in the contents list to instantly **Reveal in File Explorer** or **Copy Full Path**.
- 🔍 **Live Filtering & Breadcrumbs**: Search and filter large directories instantly with live proportional capacity gauges.
- 🛡️ **Safe & Accurate**:
  - Accurate physical cluster allocation accounting (resident `$DATA`, ADS streams).
  - Reparse point/junction loop protection.
  - Cloud-filter awareness (zeroes offline OneDrive placeholder sizes to reflect true physical disk usage).
- 🔒 **Zero Telemetry & 100% Offline**: No network calls, analytics, or background tracking.

---

## 🚀 Quick Start / Download

Download the latest self-contained standalone zip package from the [Releases](https://github.com/dklasens/VisDir/releases) page:

1. Download **`VisDir-win-x64.zip`**.
2. Extract the archive anywhere.
3. Run **`VisDir.App.exe`**.

> **Note**: To use the instant **Fast NTFS ($MFT)** engine on whole drives, run VisDir as Administrator. When running as a standard user, VisDir automatically uses the multi-threaded Compatible engine.

---

## ⌨️ Shortcuts & Navigation

| Action | Shortcut / Gesture |
| :--- | :--- |
| **Drill Into Folder** | Double-click item or click Sunburst wedge |
| **Drill Up to Parent** | Click Center Circle or press <kbd>Backspace</kbd> |
| **History Navigation** | <kbd>Alt</kbd> + <kbd>←</kbd> / <kbd>Alt</kbd> + <kbd>→</kbd> |
| **Reveal in File Explorer** | Right-click wedge/item → *Reveal in File Explorer* |
| **Copy Path to Clipboard** | Right-click wedge/item → *Copy Full Path* |
| **Filter Contents** | <kbd>Ctrl</kbd> + <kbd>F</kbd> |
| **Rescan Active Folder** | <kbd>F5</kbd> |
| **Cancel Scan / Clear Filter** | <kbd>Esc</kbd> |

---

## 🛠️ Building from Source

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.400 or later)
- Visual Studio 2022 / VS Code / JetBrains Rider with .NET desktop workload

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

### Packaging Release
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Runtime win-x64
```

---

## 🏗️ Architecture Overview

- **`VisDir.Core`**: Core tree models (`FsNode`), binary snapshot serializer (`TreeSerializer`), and scanning engines (`NtfsMftScanner`, `GenericScanner`).
- **`VisDir.App`**: WPF front-end containing the SkiaSharp sunburst canvas (`SunburstControl`), history stack, search filter, and DaisyDisk UI styling.
- **`VisDir.Scanner`**: Standalone CLI worker process for isolated, elevated scanning and benchmarking.
- **`VisDir.Benchmarks`**: Synthetic and real-drive throughput benchmarking tools.
- **`VisDir.Core.Tests`**: Unit test suite covering record parsing, tree aggregation, serialization, and geometry.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
