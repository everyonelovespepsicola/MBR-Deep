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
