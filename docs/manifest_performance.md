# Performance Dashboard Manifest

## Core Philosophy
- **Zero Dependencies:** No third-party charting libraries.
- **Zero Overhead:** Utilize native Windows APIs (P/Invoke) over heavy WMI queries.
- **Battery Conscious:** Strictly pause all hardware polling when the UI is hidden or the user is looking at the Apps tab.

## Phase 1: The UI (WPF XAML)
We will create a `PerformanceGrid` that sits perfectly on top of (or swaps with) the `ResultsList` and `SearchBox`.

### Layout
- A clean, symmetrical grid (either 2x2 or stacked vertically).
- Each tile contains:
  - A Title (e.g., "CPU", "Memory").
  - A large, real-time percentage text block (e.g., "14%").
  - A WPF `Canvas` containing a `Polyline` and a `Polygon` (for the shaded area underneath the line).

### Theming (Matching MBR-Deep Dark Mode)
- **Backgrounds:** `#2b2b2b` (Standard UI background)
- **Grid Lines:** Faint `#4a4a4a` (matching the scrollbar/borders)
- **CPU Graph:** Azure Blue (`#0078D7`)
- **RAM Graph:** Vibrant Green (`#55FF55`)

## Phase 2: Native Data Gathering (P/Invoke)
We will implement a static `NativeMonitor` class to bypass standard .NET overhead.

- **CPU Usage:** `GetSystemTimes`
  - *Logic:* We grab the system's total `IdleTime`, `KernelTime`, and `UserTime`. On the next tick, we compare the deltas to mathematically determine the exact overall CPU percentage.
- **Memory Usage:** `GlobalMemoryStatusEx`
  - *Logic:* Gives us total physical RAM and available physical RAM instantly. We calculate `(Total - Available) / Total`.
- **Disk/Network:** 
  - *Note:* Pure P/Invoke for per-process/global disk I/O and network bandwidth is notoriously complex. We will start with CPU/RAM. If we want Disk/Net, we can fallback to lightweight `PerformanceCounter` instances specifically for those two.

## Phase 3: The Engine / Lifecycle
- **The Poller:** A `DispatcherTimer` set to fire every `1000ms`.
- **Data Storage:** A fixed-size array or `Queue<double>` holding the last 60 seconds of data (60 integers per hardware component).
- **The Render Loop:** 
  1. Timer ticks.
  2. Call `NativeMonitor.GetCpuUsage()`.
  3. Push value to the queue, pop the oldest.
  4. Mathematically map the 60 data points to the `Width` and `Height` of the `Canvas` (e.g., if canvas is 100px high, 50% CPU maps to Y=50).
  5. Update the `Polyline.Points` collection. WPF hardware acceleration automatically draws it.

## Phase 4: State Management
- **Trigger In:** Clicking the Task Manager / Performance icon on the sidebar toggles the view, collapses the Search box, and *starts* the timer.
- **Trigger Out:** Pressing `Esc`, clicking the Home icon, or the window losing focus (`Deactivated`) hides the view and *stops* the timer instantly.

## Phase 5: Hardware Temperatures & Telemetry (Ring-0)
Gathering accurate CPU/GPU temperatures natively in Windows requires reading hardware MSRs via a Ring-0 Kernel Driver, which conflicts with our UI's standard-user, zero-dependency rules. 

### The Client-Server Solution
- **Backend Service (Elevated):** We integrate the open-source `LibreHardwareMonitorLib` into the background service. Because the service runs as SYSTEM/Admin, it can silently load the required `.sys` driver in the background with no UAC prompts.
- **Telemetry Pipe:** The backend exposes a secondary IPC Named Pipe (e.g., `MBRDeepTelemetryPipe`).
- **UI Integration:** The WPF app connects to this pipe only when the "Performance" tab is active, reading the JSON stream of temperature data.

### Maintaining "Zero Overhead"
To ensure the system isn't bogged down by constant sensor polling:
- **On-Demand Activation:** The kernel driver sits completely dormant (0.0% CPU) while the App Drawer is hidden.
- **Trigger In/Out:** When the user clicks the Performance tab, the UI sends a `StartTelemetry` command over the pipe. The backend wakes the driver and pipes data at 1Hz. When the drawer is closed, a `StopTelemetry` command puts the driver back to sleep.

## Phase 6: True HLSL Animation Pipeline & Dynamic Settings
To elevate the UI to a premium desktop environment feel without introducing CPU overhead, we will implement a custom GPU-accelerated rendering pipeline and a dynamic settings architecture.

### The Animation Pipeline (Option 1 - True HLSL)
- **Technology:** We will use WPF `ShaderEffect` backed by custom DirectX `.ps` (Pixel Shader) files. This pushes all transition math (UV coordinate distortion) purely to the GPU.
- **Default Effect:** **Genie** (The window bends and sucks down into the taskbar when closing, and springs up when opening).
- **Future Expansion:** The architecture allows seamlessly dropping in new `.ps` files (e.g., Burn, Beam, Matrix) later.

### The Settings UI
- **Access Point:** A sleek SVG Gear icon placed in the extreme upper-right corner of the window, on the exact same plane as the Mode Tabs (Apps, System-Tasks, Performance). Clicking it overlays the Settings Grid.
- **Theming Engine:** All hardcoded hex colors will be migrated to a `DynamicResource` dictionary, allowing instant, real-time repainting of the app without restarts.
- **Configuration Options:**
  - **Effects:** A selector to choose the entrance/exit animation (Default: Genie), and a slider for Animation Speed.
  - **Color Palettes:** Pre-defined dark themes (MBR-Deep Dark, OLED Pitch Black) and custom Accent Color pickers.
  - **UI Scaling:** Sliders to dynamically adjust the size of the Main Drawer Icons, the Left Sidebar Icons, and the overall UI padding.
  - **Visuals:** Sliders for App Drawer background opacity and UI blur/drop-shadow intensity.
