# MBR-Deep

MBR-Deep is a high-performance Windows file search utility that queries the NTFS Master File Table (MFT) directly. By bypassing the standard Windows API file iteration and using a custom C-engine DLL, it achieves lightning-fast file discovery across entire hard drives. 

Coupled with a modern, dark-themed Python/Tkinter GUI, it supports advanced deep-content searching (grepping) concurrently, including searching inside PDF files and archives.

## ✨ Features

- **Ultra-Fast Discovery:** Reads directly from the Windows NTFS Master File Table (MFT) using a low-level C DLL.
- **Deep Content Search:** Grep through file contents rapidly using multi-threaded execution.
- **PDF & Archive Support:** Extracts and searches text within PDFs (via `pypdfium2`) and compressed archives.
- **Modern UI:** Beautiful Windows dark-mode integration using `sv_ttk`.
- **Native System Icons:** Extracts and displays real Windows shell icons for files using `pywin32` and `Pillow`.
- **Live Results:** Streams matches to the UI in real-time as the search progresses.
- **Explorer Integration:** Right-click context menus to open files, show in Explorer, or use "Open With...".

## ⚠️ Prerequisites

- **Operating System:** Windows 10 / 11 (NTFS file system required).
- **Administrator Privileges:** Direct access to the volume's MFT *requires* the application to be run as an Administrator.
- **Python 3.x:** To run the GUI and scripts.
- **C/C++ Compiler:** To build the `fast_search.dll` (if not already compiled).

## 🚀 Getting Started

The project includes PowerShell automation scripts to make setup seamless.

### 1. Running the Application (Development)

Open a PowerShell terminal **as Administrator** and execute the run script:

```powershell
.\run.ps1
```

**What this script does:**
1. Creates an isolated Python virtual environment (`.env`).
2. Automatically installs missing dependencies (`sv_ttk`, `pypdfium2`, `pywin32`, `pillow`).
3. Verifies that the C-engine DLL (`fast_search.dll`) exists.
4. Launches the GUI application (`main.py`).

### 2. Building a Standalone Executable (Production)

To package the entire application (Python script, dependencies, and the C DLL) into a single, portable `.exe` file, run:

```powershell
.\publish.ps1
```

**What this script does:**
1. Installs `pyinstaller`.
2. Bundles the application, embedding the `fast_search.dll`, custom icons, and UI themes.
3. Outputs the compiled `.exe` into the `dist/` directory.

## 🛠️ Architecture

MBR-Deep uses a hybrid architecture for maximum performance and a responsive user experience:

1. **The UI (Python):** `main.py` provides the graphical interface using `tkinter`. It handles user inputs, launches background worker threads to avoid freezing the UI, and updates the results table concurrently using a thread-safe Queue.
2. **The Engine (C/C++):** `fast_search.dll` does the heavy lifting. It uses the Windows `FSCTL_ENUM_USN_DATA` control code to stream the entire file table incredibly fast. It invokes a callback back to Python when files are found.
3. **Thread Pool (Python):** When content searching is requested, Python dispatches the discovered file paths to a `concurrent.futures.ThreadPoolExecutor`, executing the file I/O and text grepping simultaneously across CPU cores.

## 🐛 Troubleshooting

- **"Cannot access drive (Needs Admin, or not NTFS)"**: You must run your PowerShell terminal or the compiled `.exe` as an Administrator. Windows blocks user-level processes from reading the raw volume data.
- **Icons are missing**: Ensure the `pywin32` and `Pillow` libraries installed successfully. The `run.ps1` script should handle this automatically.
- **"fast_search.dll not found"**: You need to compile the C/C++ backend into a DLL before running the Python script. (Usually done via a `build.ps1` or a Makefile, depending on your C++ environment).

## 📄 License

This project utilizes several open-source libraries:
- `Pillow` (HPND License)
- `pywin32` (Python Software Foundation License)
- `sv_ttk` (MIT License)
