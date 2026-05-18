using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MBRDeepDrawer
{
    public partial class AppDrawerWindow : Window
    {
        // --- Windows API for Native Icons ---
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(IntPtr ppidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("shell32.dll")]
        public static extern int SHGetSpecialFolderLocation(IntPtr hwndOwner, int nFolder, out IntPtr ppidl);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        // Used to simulate the Windows Key press
        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        private const int DWMWA_CLOAKED = 14;

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint SHGFI_LARGEICON = 0x000000000; // 0x0 implies Large icon
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint SHGFI_PIDL = 0x00000008;
        private const int CSIDL_DRIVES = 0x0011; // My Computer / This PC

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;

        // --- Windows API for Global Hotkey ---
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001; // ALT
        private const uint VK_SPACE = 0x20;  // SPACE bar
        private const int WM_HOTKEY = 0x0312;
        private IntPtr _windowHandle;

        // --- Native C-Engine Keyboard Hook ---
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ToggleUICallback();

        [DllImport("fast_search.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void InstallSystemHooks(ToggleUICallback callback);

        [DllImport("fast_search.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void UninstallSystemHooks();

        // Pin the delegate so it doesn't get garbage collected
        private ToggleUICallback? _toggleCallback;

        // Cache for icons to keep memory usage low and the UI snappy
        private Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        // The collection that automatically updates the XAML UI when items are added
        public ObservableCollection<SearchResult> SearchResults { get; set; }

        // Cache for the Start Menu shortcuts so they appear instantly when clearing the search
        private List<SearchResult> _defaultAppDrawerCache = new List<SearchResult>();

        // Cache for Windows Control Panel / God Mode tools
        private List<SearchResult> _godModeCache = new List<SearchResult>();

        // Analytics tracking for dynamic top row
        private Dictionary<string, int> _appOpenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private string _recentsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBR-Deep", "recents.json");

        // --- Settings State ---
        private AppSettings _appSettings = new AppSettings();
        private string _settingsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBR-Deep", "config.json");
        private bool _isInitializingSettings = false;

        public static readonly DependencyProperty DrawerIconSizeProperty =
            DependencyProperty.Register("DrawerIconSize", typeof(double), typeof(AppDrawerWindow), new PropertyMetadata(64.0));
        public double DrawerIconSize
        {
            get { return (double)GetValue(DrawerIconSizeProperty); }
            set { SetValue(DrawerIconSizeProperty, value); }
        }

        public static readonly DependencyProperty SidebarIconSizeProperty =
            DependencyProperty.Register("SidebarIconSize", typeof(double), typeof(AppDrawerWindow), new PropertyMetadata(72.0));
        public double SidebarIconSize
        {
            get { return (double)GetValue(SidebarIconSizeProperty); }
            set { SetValue(SidebarIconSizeProperty, value); }
        }

        // --- Focus Management ---
        private System.Windows.Threading.DispatcherTimer? _focusCheckTimer;
        private uint _myPid;

        // --- Performance Dashboard State ---
        private System.Windows.Threading.DispatcherTimer? _perfTimer;
        private CancellationTokenSource? _telemetryCts;
        private double[] _cpuHistory = new double[60];
        private double[] _ramHistory = new double[60];
        private double[] _gpuHistory = new double[60];
        private double[] _diskHistory = new double[60];
        private double[] _netHistory = new double[60];
        private int _historyIndex = 0;

        // --- Detailed Performance Dashboard State ---
        private bool _isDetailedCpuView = false;
        private bool _isDetailedRamView = false;
        private bool _isDetailedGpuView = false;
        private bool _isDetailedDiskView = false;
        private bool _isDetailedNetView = false;
        private double[][]? _coreHistory;
        private Canvas[]? _coreCanvases;
        private System.Windows.Shapes.Polyline[]? _coreLines;
        private System.Windows.Shapes.Polygon[]? _coreShades;
        private TextBlock[]? _coreTexts;
        private TextBlock[]? _coreTempTexts;

        private double[][]? _detailedDiskHistory;
        private Canvas[]? _detailedDiskCanvases;
        private System.Windows.Shapes.Polyline[]? _detailedDiskLines;
        private System.Windows.Shapes.Polygon[]? _detailedDiskShades;
        private TextBlock[]? _detailedDiskActiveTexts;
        private TextBlock[]? _detailedDiskReadTexts;
        private TextBlock[]? _detailedDiskWriteTexts;

        private string currentSearchTerm = "";
        private CancellationTokenSource? _searchCts;

        // --- System Tray Icon ---
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        // Flag to determine if we are actually quitting the app
        private bool _isExplicitExit = false;
        private DateTime _lastDeactivated;
        private DateTime _lastToggle = DateTime.MinValue;
        private bool _isAdvSearching = false;

        public ImageSource? IconThisPC { get; set; }
        public ImageSource? IconControlPanel { get; set; }
        public ImageSource? IconDevices { get; set; }
        public ImageSource? IconDefaultApps { get; set; }
        public ImageSource? IconPerformance { get; set; }

        private bool _isAnimating = false;
        private Shaders.GenieEffect? _sharedGenieEffect;
        private static readonly BitmapCache _sharedBitmapCache = new BitmapCache { EnableClearType = false, SnapsToDevicePixels = true };

        private void HideDrawer()
        {
            if (!this.IsVisible || _isAnimating) return;

            // Fallback to instant hide if the user selected "None" in the Settings
            if (_appSettings.TransitionEffect == "None" || _appSettings.AnimationSpeed <= 0.0)
            {
                this.Hide();
                return;
            }

            _isAnimating = true;

            try
            {
                double targetX = 0.5;
                if (GetCursorPos(out POINT pt))
                {
                    targetX = (double)pt.X / SystemParameters.PrimaryScreenWidth;
                    targetX = Math.Max(0.0, Math.Min(1.0, targetX));
                }

                if (_sharedGenieEffect == null) _sharedGenieEffect = new Shaders.GenieEffect();

                _sharedGenieEffect.TargetX = targetX;
                _sharedGenieEffect.BeginAnimation(Shaders.GenieEffect.ProgressProperty, null); // Clear old animation
                _sharedGenieEffect.Progress = 0.0;

                RootGrid.CacheMode = _sharedBitmapCache;
                RootGrid.Effect = _sharedGenieEffect; // Apply the GPU shader directly to the UI tree

                var anim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(_appSettings.AnimationSpeed))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                anim.Completed += (s, e) =>
                {
                    RootGrid.Effect = null;
                    RootGrid.CacheMode = null;
                    this.Hide();
                    _isAnimating = false;
                };
                _sharedGenieEffect.BeginAnimation(Shaders.GenieEffect.ProgressProperty, anim);
            }
            catch { this.Hide(); _isAnimating = false; }
        }

        private void ShowDrawer()
        {
            if (_isAnimating) return;

            if (_appSettings.TransitionEffect == "None" || _appSettings.AnimationSpeed <= 0.0)
            {
                this.Show();
                ForceForeground();
                return;
            }
            _isAnimating = true;

            try
            {
                double targetX = 0.5;
                if (GetCursorPos(out POINT pt))
                {
                    targetX = (double)pt.X / SystemParameters.PrimaryScreenWidth;
                    targetX = Math.Max(0.0, Math.Min(1.0, targetX));
                }

                if (_sharedGenieEffect == null) _sharedGenieEffect = new Shaders.GenieEffect();

                _sharedGenieEffect.TargetX = targetX;
                _sharedGenieEffect.BeginAnimation(Shaders.GenieEffect.ProgressProperty, null); // Clear old animation
                _sharedGenieEffect.Progress = 1.0;

                RootGrid.CacheMode = _sharedBitmapCache;
                RootGrid.Effect = _sharedGenieEffect;

                // Show the window AFTER setting Progress = 1.0 to prevent a 1-frame visual pop/flicker
                this.Show();
                ForceForeground();

                var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(_appSettings.AnimationSpeed))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                anim.Completed += (s, e) =>
                {
                    RootGrid.Effect = null;
                    RootGrid.CacheMode = null;
                    _isAnimating = false;
                };
                _sharedGenieEffect.BeginAnimation(Shaders.GenieEffect.ProgressProperty, anim);
            }
            catch { this.Show(); ForceForeground(); _isAnimating = false; }
        }

        private void ForceForeground()
        {
            IntPtr targetHwnd = new WindowInteropHelper(this).Handle;
            IntPtr foregroundHwnd = GetForegroundWindow();

            // 1. Explicitly Force Foreground Z-Ordering as Topmost
            SetWindowPos(targetHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            if (targetHwnd != foregroundHwnd && foregroundHwnd != IntPtr.Zero)
            {
                uint targetThreadId = GetWindowThreadProcessId(targetHwnd, out _);
                uint foregroundThreadId = GetWindowThreadProcessId(foregroundHwnd, out _);

                // Trick Windows by attaching our thread to the active foreground thread
                if (foregroundThreadId != targetThreadId)
                {
                    AttachThreadInput(foregroundThreadId, targetThreadId, true);
                    SetForegroundWindow(targetHwnd);
                    AttachThreadInput(foregroundThreadId, targetThreadId, false);
                }
                else
                {
                    SetForegroundWindow(targetHwnd);
                }
            }
            else
            {
                SetForegroundWindow(targetHwnd);
            }

            this.Activate();
            this.Focus();
            SearchBox.Focus();
        }

        private static bool IsWindowActuallyVisible(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd)) return false;

            // UWP apps (like the Start Menu) are often kept running and WS_VISIBLE,
            // but are "cloaked" by the DWM when closed. We must check the cloaked state!
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
            {
                return cloaked == 0;
            }
            return true;
        }

        private bool DismissNativeStartMenuIfVisible()
        {
            bool dismissed = false;

            // Windows 10 / 11 Native Start Menu
            IntPtr nativeStart1 = FindWindow("Windows.UI.Core.CoreWindow", "Start");
            if (nativeStart1 != IntPtr.Zero && IsWindowActuallyVisible(nativeStart1)) dismissed = true;

            // Alternate Windows 11 Start Menu host class
            IntPtr nativeStart2 = FindWindow("Windows.UI.Composition.DesktopWindowContentBridge", "Start");
            if (nativeStart2 != IntPtr.Zero && IsWindowActuallyVisible(nativeStart2)) dismissed = true;

            if (dismissed)
            {
                // The native menu is currently open on top of us! Send ESC to naturally dismiss it.
                keybd_event(0x1B, 0, 0, 0);       // VK_ESCAPE Down
                keybd_event(0x1B, 0, 0x0002, 0);  // VK_ESCAPE Up
                return true;
            }
            return false;
        }

        private void SetTaskbarTopmost(bool isTopmost)
        {
            IntPtr flag = isTopmost ? HWND_TOPMOST : HWND_NOTOPMOST;
            uint flags = isTopmost ? (SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW) : (SWP_NOMOVE | SWP_NOSIZE);

            IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
            if (taskbarHwnd != IntPtr.Zero)
            {
                SetWindowPos(taskbarHwnd, flag, 0, 0, 0, 0, flags);
            }

            // Loop to handle all secondary monitors' taskbars
            IntPtr secTaskbarHwnd = IntPtr.Zero;
            while ((secTaskbarHwnd = FindWindowEx(IntPtr.Zero, secTaskbarHwnd, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                SetWindowPos(secTaskbarHwnd, flag, 0, 0, 0, 0, flags);
            }
        }

        private async Task StartTelemetryAsync(CancellationToken token)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", "MBRDeepTelemetryPipe", PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(1000, token);

                using var reader = new StreamReader(pipeClient, System.Text.Encoding.UTF8, leaveOpen: true);

                while (pipeClient.IsConnected && !token.IsCancellationRequested)
                {
                    string? json = await reader.ReadLineAsync(token);
                    if (json == null) break;

                    var data = JsonSerializer.Deserialize<TelemetryData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (data != null)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            string cpuTempStr = data.CpuTemp > 0 ? $"{data.CpuTemp:F0} °C" : "-- °C";
                            string gpuTempStr = data.GpuTemp > 0 ? $"{data.GpuTemp:F0} °C" : "-- °C";

                            CpuTempText.Text = cpuTempStr;
                            GpuTempText.Text = gpuTempStr;

                            var redBrush = new SolidColorBrush(System.Windows.Media.Colors.Red);
                            var grayBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#aaaaaa"));
                            var whiteBrush = new SolidColorBrush(System.Windows.Media.Colors.White);

                            CpuTempText.Foreground = data.CpuTemp >= 90 ? redBrush : grayBrush;
                            GpuTempText.Foreground = data.GpuTemp >= 90 ? redBrush : grayBrush;

                            if (DetailedCpuTempText != null)
                            {
                                DetailedCpuTempText.Text = cpuTempStr;
                                DetailedCpuTempText.Foreground = data.CpuTemp >= 90 ? redBrush : grayBrush;
                            }
                            if (DetailedGpuTempText != null)
                            {
                                DetailedGpuTempText.Text = gpuTempStr;
                                DetailedGpuTempText.Foreground = data.GpuTemp >= 90 ? redBrush : whiteBrush;
                            }

                            if (_isDetailedCpuView && data.CoreTemps != null && _coreTempTexts != null)
                            {
                                for (int i = 0; i < _coreTempTexts.Length; i++)
                                {
                                    float temp = 0;
                                    if (data.CoreTemps.TryGetValue(i, out float exactTemp)) temp = exactTemp;
                                    else if (data.CoreTemps.TryGetValue(i / 2, out float htTemp)) temp = htTemp; // HT mapping fallback
                                    else if (data.CoreTemps.Count > 0) temp = data.CoreTemps.Values.First(); // Catch-all fallback

                                    if (temp > 0)
                                    {
                                        _coreTempTexts[i].Text = $"{temp:F0} °C";
                                        _coreTempTexts[i].Foreground = temp >= 90 ? redBrush : grayBrush;
                                    }
                                }
                            }
                        });
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Backend might not be running or busy */ }
        }

        public AppDrawerWindow()
        {
            InitializeComponent();
            AlignUIElements();
            SetupDarkContextMenus();
            LoadSidebarIcons();
            SearchResults = new ObservableCollection<SearchResult>();

            _myPid = (uint)Process.GetCurrentProcess().Id;
            _focusCheckTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _focusCheckTimer.Tick += (s, e) =>
            {
                if (this.IsVisible)
                {
                    IntPtr targetHwnd = new WindowInteropHelper(this).Handle;
                    IntPtr fgHwnd = GetForegroundWindow();

                    if (fgHwnd != targetHwnd && fgHwnd != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(fgHwnd, out uint fgPid);

                        if (fgPid != _myPid)
                        {
                            _lastDeactivated = DateTime.Now;
                            HideDrawer();
                        }
                    }
                }
            };

            _perfTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _perfTimer.Tick += PerfTimer_Tick;

            // Set up CollectionViewSource for Grouping
            var cvs = new CollectionViewSource { Source = SearchResults };
            cvs.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
            ResultsList.ItemsSource = cvs.View;

            // Populate Drive RadioButtons
            var rbAll = new System.Windows.Controls.RadioButton
            {
                Content = "All",
                GroupName = "AdvDriveGroup",
                IsChecked = true,
                FontSize = 16,
                Margin = new Thickness(0, 0, 15, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            rbAll.Checked += AdvDrive_Checked;
            AdvDrivePanel.Children.Add(rbAll);

            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                {
                    var rb = new System.Windows.Controls.RadioButton
                    {
                        Content = drive.Name.Substring(0, 1),
                        GroupName = "AdvDriveGroup",
                        FontSize = 16,
                        Margin = new Thickness(0, 0, 15, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center
                    };
                    rb.Checked += AdvDrive_Checked;
                    AdvDrivePanel.Children.Add(rb);
                }
            }

            LoadRecents();
            LoadSettings();

            // Load Start Menu apps in the background instantly
            _ = LoadDefaultAppDrawerAsync();

            // Load God Mode items in the background
            _ = Task.Run(() => LoadGodModeItems());

            // Pre-load performance counters in the background so the Performance tab opens instantly
            _ = Task.Run(() =>
            {
                NativeMonitor.InitializeExtraCounters();
                NativeMonitor.GetGpuUsageAndMemory();
            });

            // Size the window to fill the screen but respect the taskbar
            this.Width = SystemParameters.WorkArea.Width;
            this.Height = SystemParameters.WorkArea.Height;
            this.Top = SystemParameters.WorkArea.Top;
            this.Left = SystemParameters.WorkArea.Left;

            // 1. Setup the System Tray Icon
            _notifyIcon = new System.Windows.Forms.NotifyIcon();

            try
            {
                // Extracts the default Windows executable icon for the tray
                string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath ?? "");
            }
            catch
            {
                // Fallback icon if extraction fails (common in .NET Core)
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            _notifyIcon.Text = "MBR-Deep Search (Click to toggle)";
            _notifyIcon.Visible = true;
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;

            // Create a context menu for the tray icon so the user can actually quit
            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit MBR-Deep");
            exitItem.Click += (s, e) =>
            {
                _isExplicitExit = true;
                System.Windows.Application.Current.Shutdown();
            };
            contextMenu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = contextMenu;

            // 2. Hide the window when it loses focus (like a true native overlay)
            this.Deactivated += (s, e) =>
            {
                _lastDeactivated = DateTime.Now;
                HideDrawer();
            };

            // 3. Focus the search box instantly when the window is shown
            this.IsVisibleChanged += (s, e) =>
            {
                if (this.IsVisible)
                {
                    // Force the taskbar to appear over any borderless fullscreen games
                    SetTaskbarTopmost(true);

                    _focusCheckTimer?.Start();

                    if (TabPerf.IsChecked == true)
                    {
                        // Re-establish baselines so we don't average out hardware rates over the time the drawer was hidden
                        NativeMonitor.GetCpuUsage();
                        NativeMonitor.GetDiskUsage();
                        NativeMonitor.GetNetworkUsage();
                        NativeMonitor.GetGpuUsageAndMemory();
                        if (_isDetailedCpuView) NativeMonitor.GetCoreUsages();
                        _perfTimer?.Start();

                        _telemetryCts?.Cancel();
                        _telemetryCts = new CancellationTokenSource();
                        _ = StartTelemetryAsync(_telemetryCts.Token);
                    }
                    else
                    {
                        SearchBox.Focus();
                        SearchBox.SelectAll();
                    }
                }
                else
                {
                    // Restore the taskbar's default (non-topmost) state
                    SetTaskbarTopmost(false);

                    _focusCheckTimer?.Stop();
                    _perfTimer?.Stop();
                    _telemetryCts?.Cancel();

                    // Reset the UI cleanly when the window is hidden
                    SearchBox.Text = "";

                    if (_isAdvSearching)
                    {
                        _searchCts?.Cancel();
                        _isAdvSearching = false;
                        AdvSearchActionBtn.Content = "Search";
                        AdvSearchActionBtn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ControlBackgroundBrush");
                    }

                    if (TabPerf.IsChecked != true)
                    {
                        AdvancedPanel.Visibility = Visibility.Collapsed;
                        BasicSearchPanel.Visibility = Visibility.Visible;
                    }
                }
            };

            // 4. Hook the Keyboard via our fast unmanaged C-Engine
            _toggleCallback = () =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if ((DateTime.Now - _lastToggle).TotalMilliseconds < 300) return;
                    _lastToggle = DateTime.Now;

                    // Prevent immediate re-opening if clicking the Start Button is what caused the window to lose focus and hide
                    if ((DateTime.Now - _lastDeactivated).TotalMilliseconds < 200)
                    {
                        // Give the native menu a split second to appear, then kill it
                        await Task.Delay(100);
                        DismissNativeStartMenuIfVisible();
                        return;
                    }

                    bool nativeClosed = DismissNativeStartMenuIfVisible();

                    if (this.IsVisible && !nativeClosed) HideDrawer();
                    else ShowDrawer();
                }));
            };
            InstallSystemHooks(_toggleCallback);
        }

        private void AlignUIElements()
        {
            void CenterComboBox(System.Windows.Controls.ComboBox? cb)
            {
                if (cb == null) return;
                cb.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
                cb.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
                foreach (var item in cb.Items)
                {
                    if (item is System.Windows.Controls.ComboBoxItem cbi)
                    {
                        cbi.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
                        cbi.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
                    }
                }
            }

            CenterComboBox(SettingsColorPalette);
            CenterComboBox(SettingsFontColorMode);
            CenterComboBox(SettingsFontFamily);
            CenterComboBox(SettingsEffect);
            CenterComboBox(AdvFileType);

            // Ensure text inside input boxes is perfectly centered vertically
            if (SearchBox != null) SearchBox.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
            if (AdvName1 != null) AdvName1.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
            if (AdvName2 != null) AdvName2.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
            if (AdvContent1 != null) AdvContent1.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
            if (AdvContent2 != null) AdvContent2.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
            if (AdvLocation != null) AdvLocation.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
        }

        private void SetupDarkContextMenus()
        {
            void ApplyDarkMenu(System.Windows.Controls.TextBox textBox)
            {
                var ctxMenu = new System.Windows.Controls.ContextMenu
                {
                    BorderThickness = new Thickness(1)
                };

                ctxMenu.SetBinding(System.Windows.Controls.Control.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Window), 1) });
                ctxMenu.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "PrimaryTextBrush");
                ctxMenu.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "ControlBorderBrush");

                var cut = new System.Windows.Controls.MenuItem { Header = "Cut", Command = System.Windows.Input.ApplicationCommands.Cut };
                var copy = new System.Windows.Controls.MenuItem { Header = "Copy", Command = System.Windows.Input.ApplicationCommands.Copy };
                var paste = new System.Windows.Controls.MenuItem { Header = "Paste", Command = System.Windows.Input.ApplicationCommands.Paste };
                var selectAll = new System.Windows.Controls.MenuItem { Header = "Select All", Command = System.Windows.Input.ApplicationCommands.SelectAll };

                ctxMenu.Items.Add(cut);
                ctxMenu.Items.Add(copy);
                ctxMenu.Items.Add(paste);

                var sep = new System.Windows.Controls.Separator();
                sep.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ControlBorderBrush");
                ctxMenu.Items.Add(sep);

                ctxMenu.Items.Add(selectAll);

                textBox.ContextMenu = ctxMenu;
            }

            if (SearchBox != null) ApplyDarkMenu(SearchBox);
            if (AdvName1 != null) ApplyDarkMenu(AdvName1);
            if (AdvName2 != null) ApplyDarkMenu(AdvName2);
            if (AdvContent1 != null) ApplyDarkMenu(AdvContent1);
            if (AdvContent2 != null) ApplyDarkMenu(AdvContent2);
            if (AdvLocation != null) ApplyDarkMenu(AdvLocation);
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) _appSettings = loaded;
                }
            }
            catch { }

            // Migrate opacity to new 0-100 scale if it's currently on the old 0-1 scale
            if (_appSettings.BackgroundOpacity <= 1.0 && _appSettings.BackgroundOpacity > 0.0)
            {
                _appSettings.BackgroundOpacity *= 100.0;
            }

            DrawerIconSize = _appSettings.DrawerIconSize;
            SidebarIconSize = _appSettings.SidebarIconSize;
            UpdateBackgroundColor();
            UpdateFontFamily();

            _isInitializingSettings = true;
            if (SettingsOpacitySlider != null) SettingsOpacitySlider.Value = _appSettings.BackgroundOpacity;
            if (SettingsSpeedSlider != null) SettingsSpeedSlider.Value = _appSettings.AnimationSpeed;
            if (SettingsDrawerIconSize != null) SettingsDrawerIconSize.Value = _appSettings.DrawerIconSize;
            if (SettingsSidebarIconSize != null) SettingsSidebarIconSize.Value = _appSettings.SidebarIconSize;
            if (SettingsFontBold != null) SettingsFontBold.IsChecked = _appSettings.FontBold;

            if (SettingsColorPalette != null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in SettingsColorPalette.Items)
                {
                    if (item.Content?.ToString() == _appSettings.ColorPalette)
                    {
                        SettingsColorPalette.SelectedItem = item;
                        break;
                    }
                }
            }

            if (SettingsFontColorMode != null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in SettingsFontColorMode.Items)
                {
                    if (item.Content?.ToString() == _appSettings.FontColorMode)
                    {
                        SettingsFontColorMode.SelectedItem = item;
                        break;
                    }
                }
            }

            if (SettingsFontFamily != null)
            {
                bool fontFound = false;
                foreach (System.Windows.Controls.ComboBoxItem item in SettingsFontFamily.Items)
                {
                    if (item.Content?.ToString() == _appSettings.FontFamily)
                    {
                        SettingsFontFamily.SelectedItem = item;
                        fontFound = true;
                        break;
                    }
                }
                if (!fontFound)
                    SettingsFontFamily.SelectedIndex = 0;
            }

            if (SettingsEffect != null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in SettingsEffect.Items)
                {
                    if (item.Content?.ToString() == _appSettings.TransitionEffect)
                    {
                        SettingsEffect.SelectedItem = item;
                        break;
                    }
                }
            }
            if (CustomColorPanel != null)
            {
                CustomColorPanel.Visibility = _appSettings.ColorPalette == "Custom" ? Visibility.Visible : Visibility.Collapsed;

                try
                {
                    var col = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_appSettings.CustomColorHex ?? "#1C1C1C");

                    // Temporarily disable the event handler to prevent loops
                    _isInitializingSettings = true;
                    var hsl = RgbToHsl(col);
                    if (SliderHue != null) SliderHue.Value = hsl.H;
                    if (SliderLight != null) SliderLight.Value = hsl.L * 100.0;

                    if (TextHue != null) TextHue.Text = Math.Round(hsl.H).ToString();
                    if (TextLight != null) TextLight.Text = Math.Round(hsl.L * 100.0).ToString();

                    if (ColorPreview != null) ColorPreview.Background = new SolidColorBrush(col);
                    _isInitializingSettings = false;
                }
                catch { }
            }

            if (CustomFontColorPanel != null)
            {
                CustomFontColorPanel.Visibility = _appSettings.FontColorMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;

                try
                {
                    var col = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_appSettings.CustomFontColorHex ?? "#FFFFFF");

                    _isInitializingSettings = true;
                    var hsl = RgbToHsl(col);
                    if (SliderFontHue != null) SliderFontHue.Value = hsl.H;
                    if (SliderFontLight != null) SliderFontLight.Value = hsl.L * 100.0;

                    if (TextFontHue != null) TextFontHue.Text = Math.Round(hsl.H).ToString();
                    if (TextFontLight != null) TextFontLight.Text = Math.Round(hsl.L * 100.0).ToString();

                    if (FontColorPreview != null) FontColorPreview.Background = new SolidColorBrush(col);
                    _isInitializingSettings = false;
                }
                catch { }
            }

            if (_appSettings.SortMode == "Categories" && SortCategory != null) SortCategory.IsChecked = true;
            else if (_appSettings.SortMode == "Favorites" && SortFav != null) SortFav.IsChecked = true;
            else if (SortAZ != null) SortAZ.IsChecked = true;

            if (AdvCaseSensitive != null) AdvCaseSensitive.IsChecked = _appSettings.AdvCaseSensitive;
            if (AdvLocation != null) AdvLocation.Text = _appSettings.AdvLocation ?? "";
            if (AdvName1 != null) AdvName1.Text = _appSettings.AdvName1 ?? "";
            if (AdvName2 != null) AdvName2.Text = _appSettings.AdvName2 ?? "";
            if (AdvContent1 != null) AdvContent1.Text = _appSettings.AdvContent1 ?? "";
            if (AdvContent2 != null) AdvContent2.Text = _appSettings.AdvContent2 ?? "";

            if (AdvFileType != null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in AdvFileType.Items)
                {
                    if (item.Content?.ToString() == _appSettings.AdvFileType)
                    {
                        AdvFileType.SelectedItem = item;
                        break;
                    }
                }
            }

            if (AdvDrivePanel != null)
            {
                foreach (var child in AdvDrivePanel.Children)
                {
                    if (child is System.Windows.Controls.RadioButton rb && rb.Content?.ToString() == _appSettings.AdvDrive)
                    {
                        rb.IsChecked = true;
                        break;
                    }
                }
            }
            _isInitializingSettings = false;
        }

        private void SaveSettings()
        {
            if (_isInitializingSettings) return;
            try
            {
                var dir = Path.GetDirectoryName(_settingsFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
                File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(_appSettings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private async void NotifyIcon_MouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                if ((DateTime.Now - _lastToggle).TotalMilliseconds < 300) return;
                _lastToggle = DateTime.Now;

                // Prevent immediate re-opening if clicking the tray icon is what caused the window to lose focus and hide
                if ((DateTime.Now - _lastDeactivated).TotalMilliseconds < 200)
                {
                    await Task.Delay(100);
                    DismissNativeStartMenuIfVisible();
                    return;
                }

                bool nativeClosed = DismissNativeStartMenuIfVisible();

                if (this.IsVisible && !nativeClosed) HideDrawer();
                else ShowDrawer();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _windowHandle = new WindowInteropHelper(this).Handle;
            HwndSource? source = HwndSource.FromHwnd(_windowHandle);
            source?.AddHook(HwndHook);

            // Register Alt + Space as a global hotkey
            RegisterHotKey(_windowHandle, HOTKEY_ID, MOD_ALT, VK_SPACE);

            // Hide window immediately if launched via Startup folder shortcut
            string[] args = Environment.GetCommandLineArgs();
            if (args.Contains("-hidden", StringComparer.OrdinalIgnoreCase))
            {
                this.Hide();
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                if ((DateTime.Now - _lastToggle).TotalMilliseconds < 300) { handled = true; return IntPtr.Zero; }
                _lastToggle = DateTime.Now;

                bool nativeClosed = DismissNativeStartMenuIfVisible();

                if (this.IsVisible && !nativeClosed) HideDrawer();
                else ShowDrawer();
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // If the user tries to close the window (e.g. Alt+F4), just hide it instead
            // unless they clicked "Exit" from the tray icon menu.
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                HideDrawer();
            }
            else
            {
                base.OnClosing(e);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Unregister the global hotkey
            if (_windowHandle != IntPtr.Zero)
            {
                UnregisterHotKey(_windowHandle, HOTKEY_ID);
            }

            // Unhook the C-Engine low-level keyboard and mouse hooks
            UninstallSystemHooks();

            // Clean up the tray icon so it doesn't linger after closing the app
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            base.OnClosed(e);
        }

        private void LoadRecents()
        {
            try
            {
                if (File.Exists(_recentsFilePath))
                {
                    var json = File.ReadAllText(_recentsFilePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (loaded != null) _appOpenCounts = new Dictionary<string, int>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
        }

        private void SaveRecents()
        {
            try
            {
                var dir = Path.GetDirectoryName(_recentsFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
                File.WriteAllText(_recentsFilePath, JsonSerializer.Serialize(_appOpenCounts));
            }
            catch { }
        }

        private void TrackAppOpen(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _appOpenCounts[path] = _appOpenCounts.ContainsKey(path) ? _appOpenCounts[path] + 1 : 1;
            SaveRecents();

            // Silently rebuild the background cache so the drawer is perfectly updated next time it is summoned
            _ = LoadDefaultAppDrawerAsync();
        }

        private void LoadGodModeItems()
        {
            // Spin up a dedicated background STA thread to prevent the UI from freezing during startup
            Thread staThread = new Thread(() =>
            {
                var godModeApps = new List<SearchResult>();
                try
                {
                    // Grab the physical directory where the installer extracted our perfect shortcuts
                    string targetDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "GodModeLinks");

                    // Fallback path for when running locally in development via 'dotnet run'
                    if (!Directory.Exists(targetDirectory))
                    {
                        targetDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GodModeLinks");
                    }

                    if (Directory.Exists(targetDirectory))
                    {
                        foreach (var path in Directory.EnumerateFiles(targetDirectory, "*.lnk", SearchOption.TopDirectoryOnly))
                        {
                            string name = Path.GetFileNameWithoutExtension(path);
                            ImageSource? icon = null;

                            // Our existing bulletproof icon extractor grabs the embedded system icons perfectly
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                icon = GetSpecificFileIcon(path);
                            });

                            string subCategory = "General Tasks";
                            if (!string.IsNullOrEmpty(name))
                            {
                                var parts = name.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length > 0)
                                {
                                    subCategory = parts[0];
                                    if (subCategory.Equals("Change", StringComparison.OrdinalIgnoreCase) ||
                                        subCategory.Equals("View", StringComparison.OrdinalIgnoreCase) ||
                                        subCategory.Equals("Add", StringComparison.OrdinalIgnoreCase) ||
                                        subCategory.Equals("Set", StringComparison.OrdinalIgnoreCase) ||
                                        subCategory.Equals("Turn", StringComparison.OrdinalIgnoreCase) ||
                                        subCategory.Equals("Allow", StringComparison.OrdinalIgnoreCase) ||
                                        subCategory.Equals("Create", StringComparison.OrdinalIgnoreCase) ||
                                        subCategory.Equals("Manage", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (parts.Length > 1) subCategory = parts[1];
                                    }
                                }
                            }

                            godModeApps.Add(new SearchResult { FileName = path, DisplayName = name, Icon = icon, Category = "System-Tasks", MainCategory = "System-Tasks", SubCategory = subCategory });
                        }
                    }
                }
                catch { }

                godModeApps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                _godModeCache = godModeApps;

                // If the user is actively viewing the God Mode tab, refresh it instantly when the background load finishes
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (TabSettings.IsChecked == true)
                    {
                        SortMode_Changed(this, new RoutedEventArgs());
                    }
                });
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();
        }

        private async Task LoadDefaultAppDrawerAsync()
        {
            await Task.Run(() =>
            {
                var apps = new List<SearchResult>();
                var shortcutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string userStartup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

                void AddShortcuts(string directory)
                {
                    if (!Directory.Exists(directory)) return;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(directory, "*.lnk", SearchOption.AllDirectories))
                        {
                            // Filter out uninstaller and help links
                            if (file.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                            if (file.Contains("Help", StringComparison.OrdinalIgnoreCase)) continue;

                            // Filter out startup folders
                            if (!string.IsNullOrEmpty(userStartup) && file.StartsWith(userStartup, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.IsNullOrEmpty(commonStartup) && file.StartsWith(commonStartup, StringComparison.OrdinalIgnoreCase)) continue;

                            shortcutPaths.Add(file);
                        }
                    }
                    catch { /* Ignore access exceptions if any folders are locked */ }
                }

                // Grab all standard Windows Start Menu locations
                AddShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
                AddShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.Programs));

                // Grab UWP Apps (Calculator, Terminal, Snipping Tool, etc.)
                var uwpApps = new List<(string Name, string Path)>();
                try
                {
                    Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
                    if (shellAppType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellAppType)!;
                        dynamic folder = shell.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
                        if (folder != null)
                        {
                            foreach (dynamic item in folder.Items())
                            {
                                string path = item.Path;
                                string name = item.Name;
                                // UWP Apps and other modern Windows components have '!' in their Application User Model ID
                                if (!string.IsNullOrEmpty(path) && path.Contains("!"))
                                {
                                    uwpApps.Add((name, "shell:AppsFolder\\" + path));
                                }
                            }
                        }
                    }
                }
                catch { }

                bool isFavoritesSort = false;
                System.Windows.Application.Current.Dispatcher.Invoke(() => isFavoritesSort = SortFav.IsChecked == true);

                foreach (var path in shortcutPaths)
                {
                    ImageSource? icon = null;
                    System.Windows.Application.Current.Dispatcher.Invoke(() => icon = GetSpecificFileIcon(path));
                    var name = Path.GetFileNameWithoutExtension(path);

                    string subCategory = "General Apps";
                    string? parent = Path.GetFileName(Path.GetDirectoryName(path));
                    if (!string.IsNullOrEmpty(parent) &&
                        !string.Equals(parent, "Programs", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(parent, "Start Menu", StringComparison.OrdinalIgnoreCase))
                    {
                        subCategory = parent;
                    }

                    apps.Add(new SearchResult { FileName = path, DisplayName = name, Icon = icon, Category = "Programs", MainCategory = "Programs", SubCategory = subCategory });
                }

                foreach (var uwp in uwpApps)
                {
                    ImageSource? icon = null;
                    System.Windows.Application.Current.Dispatcher.Invoke(() => icon = GetSpecificFileIcon(uwp.Path));

                    apps.Add(new SearchResult { FileName = uwp.Path, DisplayName = uwp.Name, Icon = icon, Category = "Programs", MainCategory = "Programs", SubCategory = "Windows Apps" });
                }

                if (isFavoritesSort)
                {
                    apps.Sort((a, b) =>
                    {
                        int aCount = _appOpenCounts.TryGetValue(a.FileName ?? "", out int ac) ? ac : 0;
                        int bCount = _appOpenCounts.TryGetValue(b.FileName ?? "", out int bc) ? bc : 0;
                        if (aCount != bCount) return bCount.CompareTo(aCount);
                        return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                    });
                }
                else
                {
                    // Sort alphabetically
                    apps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                }

                _defaultAppDrawerCache = apps;
            });

            // Populate the UI if the user hasn't typed anything yet
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (string.IsNullOrWhiteSpace(SearchBox.Text) && TabApps.IsChecked == true)
                {
                    ShowDefaultApps();
                }
            });
        }

        private void ShowDefaultApps()
        {
            SearchResults.Clear();
            bool showCategories = SortCategory.IsChecked == true;
            foreach (var app in _defaultAppDrawerCache)
            {
                app.Category = showCategories ? app.SubCategory : app.MainCategory;
                SearchResults.Add(app);
            }
        }

        private ImageSource? GetIconForExtension(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) ext = "folder"; // Fallback for unknown extensions

            // Return cached icon if we already generated it for this file type
            if (_iconCache.TryGetValue(ext, out ImageSource? cachedIcon))
            {
                return cachedIcon;
            }

            SHFILEINFO shinfo = new SHFILEINFO();
            // Query Windows for the generic icon associated with this extension
            IntPtr hImg = SHGetFileInfo(ext, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

            if (shinfo.hIcon != IntPtr.Zero)
            {
                var img = Imaging.CreateBitmapSourceFromHIcon(
                    shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                DestroyIcon(shinfo.hIcon); // Prevent memory leaks!
                img?.Freeze();             // Essential for passing the image across threads!

                if (img != null)
                {
                    _iconCache[ext] = img;
                }
                return img;
            }

            return null;
        }

        private ImageSource? GetSpecificFileIcon(string filePath)
        {
            // 0. Handle virtual shell items like UWP Apps (shell:AppsFolder\...)
            if (filePath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (SHParseDisplayName(filePath, IntPtr.Zero, out IntPtr pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                    {
                        SHFILEINFO shinfoShell = new SHFILEINFO();
                        IntPtr hImgShell = SHGetFileInfo(pidl, 0, ref shinfoShell, (uint)Marshal.SizeOf(shinfoShell), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_PIDL);

                        if (shinfoShell.hIcon != IntPtr.Zero)
                        {
                            var img = Imaging.CreateBitmapSourceFromHIcon(
                                shinfoShell.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                            DestroyIcon(shinfoShell.hIcon);
                            img?.Freeze();
                            ILFree(pidl);
                            return img;
                        }

                        ILFree(pidl);
                    }
                }
                catch { /* Ignore parsing errors for shell items */ }
            }

            // 1. If it's a shortcut, read the RAW TARGET to completely bypass the Windows Shell arrow overlay!
            if (filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    IShellLink link = (IShellLink)new ShellLink();
                    ((IPersistFile)link).Load(filePath, 0);

                    link.GetIDList(out IntPtr targetPidl);

                    if (targetPidl != IntPtr.Zero)
                    {
                        SHFILEINFO shinfoTarget = new SHFILEINFO();

                        // Query the target directly - Windows won't add a shortcut arrow to the target itself!
                        IntPtr hImgTarget = SHGetFileInfo(targetPidl, 0, ref shinfoTarget, (uint)Marshal.SizeOf(shinfoTarget), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_PIDL);

                        if (shinfoTarget.hIcon != IntPtr.Zero)
                        {
                            var img = Imaging.CreateBitmapSourceFromHIcon(
                                shinfoTarget.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                            DestroyIcon(shinfoTarget.hIcon);
                            img?.Freeze();
                            ILFree(targetPidl);
                            return img;
                        }

                        ILFree(targetPidl);
                    }
                }
                catch { /* Ignore COM errors and fall back to standard extraction */ }
            }

            // 2. Try standard .NET executable extraction (very fast for EXEs, fails gracefully on virtual UWP targets)
            try
            {
                using System.Drawing.Icon? pureIcon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
                if (pureIcon != null)
                {
                    var img = Imaging.CreateBitmapSourceFromHIcon(
                        pureIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img?.Freeze();
                    return img;
                }
            }
            catch { /* Ignore extraction errors and fallback to the Windows Shell */ }

            // 3. Final Fallback: Ask Windows for the generic file icon (will have an arrow if it's a .lnk)
            SHFILEINFO shinfo = new SHFILEINFO();
            // Query Windows for the *exact* embedded icon of this shortcut file
            IntPtr hImg = SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);

            if (shinfo.hIcon != IntPtr.Zero)
            {
                var img = Imaging.CreateBitmapSourceFromHIcon(
                    shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                DestroyIcon(shinfo.hIcon);
                img?.Freeze();
                return img;
            }

            return null;
        }

        private void LoadSidebarIcons()
        {
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            // Get the exact "This PC" virtual folder icon using its PIDL (Pointer to an Item ID List)
            IntPtr pidl = IntPtr.Zero;
            if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DRIVES, out pidl) == 0 && pidl != IntPtr.Zero)
            {
                SHFILEINFO shinfo = new SHFILEINFO();
                SHGetFileInfo(pidl, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_PIDL);
                if (shinfo.hIcon != IntPtr.Zero)
                {
                    var img = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img?.Freeze();
                    IconThisPC = img;
                    DestroyIcon(shinfo.hIcon);
                }
                Marshal.FreeCoTaskMem(pidl); // Free the memory allocated by the Windows Shell
            }
            IconControlPanel = GetSpecificFileIcon(Path.Combine(sys32, "control.exe"));

            string devicesExe = Path.Combine(sys32, "DeviceDisplayObjectProvider.exe");
            if (!File.Exists(devicesExe)) devicesExe = Path.Combine(sys32, "DevicePairingWizard.exe");
            IconDevices = GetSpecificFileIcon(devicesExe);

            IconDefaultApps = GetSpecificFileIcon(Path.Combine(sys32, "computerdefaults.exe"));
            IconPerformance = GetSpecificFileIcon(Path.Combine(sys32, "taskmgr.exe"));
        }

        private void SortMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded) return;

            if (!_isInitializingSettings)
            {
                if (SortCategory.IsChecked == true) _appSettings.SortMode = "Categories";
                else if (SortFav.IsChecked == true) _appSettings.SortMode = "Favorites";
                else _appSettings.SortMode = "A-Z";
                SaveSettings();
            }

            if (TabSettings.IsChecked == true)
            {
                string currentTerm = SearchBox.Text.ToLower();
                SearchResults.Clear();
                bool showCategories = SortCategory.IsChecked == true;

                var matchingSettings = _godModeCache
                    .Where(s => string.IsNullOrWhiteSpace(currentTerm) || (s.DisplayName != null && s.DisplayName.Contains(currentTerm, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                bool isFavoritesSort = SortFav.IsChecked == true;
                if (isFavoritesSort)
                {
                    matchingSettings.Sort((a, b) =>
                    {
                        int aCount = _appOpenCounts.TryGetValue(a.FileName ?? "", out int ac) ? ac : 0;
                        int bCount = _appOpenCounts.TryGetValue(b.FileName ?? "", out int bc) ? bc : 0;
                        if (aCount != bCount) return bCount.CompareTo(aCount);
                        return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                    });
                }
                else
                {
                    matchingSettings.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var app in matchingSettings)
                {
                    app.Category = showCategories ? app.SubCategory : app.MainCategory;
                    SearchResults.Add(app);
                }
            }
            else
            {
                _ = LoadDefaultAppDrawerAsync();
            }
        }

        private void ModeTab_Changed(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded) return;

            if (SettingsDashboardGrid != null)
                SettingsDashboardGrid.Visibility = Visibility.Collapsed;

            if (TabPerf.IsChecked == true)
            {
                ResultsList.Visibility = Visibility.Collapsed;
                BasicSearchPanel.Visibility = Visibility.Collapsed;
                AdvancedPanel.Visibility = Visibility.Collapsed;

                if (_isDetailedCpuView) DetailedCpuGrid.Visibility = Visibility.Visible;
                else if (_isDetailedRamView) DetailedRamGrid.Visibility = Visibility.Visible;
                else if (_isDetailedGpuView) DetailedGpuGrid.Visibility = Visibility.Visible;
                else if (_isDetailedDiskView) DetailedDiskGrid.Visibility = Visibility.Visible;
                else if (_isDetailedNetView) DetailedNetGrid.Visibility = Visibility.Visible;
                else PerformanceGrid.Visibility = Visibility.Visible;

                NativeMonitor.GetCpuUsage();
                NativeMonitor.InitializeExtraCounters();
                _perfTimer?.Start();

                _telemetryCts?.Cancel();
                _telemetryCts = new CancellationTokenSource();
                _ = StartTelemetryAsync(_telemetryCts.Token);
            }
            else
            {
                PerformanceGrid.Visibility = Visibility.Collapsed;
                DetailedCpuGrid.Visibility = Visibility.Collapsed;
                DetailedRamGrid.Visibility = Visibility.Collapsed;
                DetailedGpuGrid.Visibility = Visibility.Collapsed;
                DetailedDiskGrid.Visibility = Visibility.Collapsed;
                DetailedNetGrid.Visibility = Visibility.Collapsed;
                _perfTimer?.Stop();
                _telemetryCts?.Cancel();

                ResultsList.Visibility = Visibility.Visible;
                BasicSearchPanel.Visibility = Visibility.Visible;

                SearchBox.Clear();
                SearchBox.Focus();

                if (TabSettings.IsChecked == true)
                {
                    SortMode_Changed(sender, e);
                    BtnAdvanced.Visibility = Visibility.Collapsed;
                }
                else
                {
                    BtnAdvanced.Visibility = Visibility.Visible;
                    _ = LoadDefaultAppDrawerAsync();
                }
            }
        }

        private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            currentSearchTerm = SearchBox.Text.ToLower();

            if (string.IsNullOrWhiteSpace(currentSearchTerm))
            {
                _searchCts?.Cancel();
                if (TabSettings.IsChecked == true)
                {
                    SortMode_Changed(sender, new RoutedEventArgs());
                }
                else
                {
                    ShowDefaultApps();
                }
                return;
            }

            if (TabSettings.IsChecked == true)
            {
                SortMode_Changed(sender, new RoutedEventArgs());
                return; // Do NOT trigger IPC when searching settings!
            }

            // 1. Cancel the previous search immediately if the user is still typing
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                // 2. Debounce: Wait 250ms before starting the scan.
                // If the user types another letter within 250ms, this delay throws a TaskCanceledException.
                await Task.Delay(250, token);

                SearchResults.Clear();

                // 3. Search the Start Menu cache first and add to the top
                var matchingApps = _defaultAppDrawerCache
                    .Where(app => app.DisplayName != null && app.DisplayName.Contains(currentSearchTerm, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                bool showCategories = false;
                System.Windows.Application.Current.Dispatcher.Invoke(() => showCategories = SortCategory.IsChecked == true);

                foreach (var app in matchingApps)
                {
                    app.Category = showCategories ? app.SubCategory : app.MainCategory;
                    SearchResults.Add(app);
                }

                // 4. Query the background service over IPC
                await Task.Run(async () =>
                {
                    try
                    {
                        using var pipeClient = new NamedPipeClientStream(".", "MBRDeepSearchPipe", PipeDirection.InOut, PipeOptions.Asynchronous);

                        // Connect with cancellation support
                        await pipeClient.ConnectAsync(1000, token);

                        using var writer = new StreamWriter(pipeClient, System.Text.Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                        using var reader = new StreamReader(pipeClient, System.Text.Encoding.UTF8, leaveOpen: true);

                        // Send the query to the engine
                        var request = new SearchRequest { IsAdvanced = false, BasicQuery = currentSearchTerm };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);

                        // Stream the results as they are piped back
                        string? resultPath;
                        while ((resultPath = await reader.ReadLineAsync(token)) != null)
                        {
                            // Stop processing when the engine tells us the search is done
                            if (resultPath == "---EOF---") break;

                            // Dispatch back to the main UI thread to update the ObservableCollection
                            // Awaiting this safely syncs the background loop with the UI render speed
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                if (SearchResults.Count < 100)
                                {
                                    // BUGFIX: Extracting shell icons MUST be done on the UI (STA) thread!
                                    // Calling SHGetFileInfo from an MTA background thread causes a fatal Access Violation (Terminal Crash)
                                    ImageSource? icon = GetIconForExtension(resultPath);

                                    SearchResults.Add(new SearchResult
                                    {
                                        FileName = resultPath,
                                        DisplayName = Path.GetFileName(resultPath),
                                        Icon = icon,
                                        Category = "Files",
                                        MainCategory = "Files",
                                        SubCategory = "Files"
                                    });
                                }
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // The user typed a new character while we were waiting/reading from the pipe
                    }
                    catch (Exception ex)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            SearchResults.Add(new SearchResult
                            {
                                FileName = "",
                                DisplayName = $"Engine Offline: {ex.Message}",
                                Category = "System"
                            });
                        });
                    }
                }, token);
            }
            catch (TaskCanceledException)
            {
                // The 250ms debounce delay was cancelled by a new keystroke. Ignore safely.
            }
        }

        // --- Advanced Search UI ---

        private void BtnAdvanced_Click(object sender, RoutedEventArgs e)
        {
            BasicSearchPanel.Visibility = Visibility.Collapsed;
            AdvancedPanel.Visibility = Visibility.Visible;
            AdvName1.Focus();
        }

        private void AdvCloseBtn_Click(object sender, RoutedEventArgs e)
        {
            AdvancedPanel.Visibility = Visibility.Collapsed;
            BasicSearchPanel.Visibility = Visibility.Visible;
            SearchBox.Focus();
        }

        private void AdvBrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Folder to Search",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                AdvLocation.Text = dialog.SelectedPath;

                // Optionally set the drive radio button to match the selected folder's drive automatically
                string driveLetter = Path.GetPathRoot(dialog.SelectedPath)?.Substring(0, 1).ToUpper() ?? "";
                foreach (var child in AdvDrivePanel.Children)
                {
                    if (child is System.Windows.Controls.RadioButton rb && rb.Content.ToString() == driveLetter)
                    {
                        rb.IsChecked = true;
                        break;
                    }
                }
            }
        }

        private async void AdvSearchActionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isAdvSearching)
            {
                // Cancel Search
                _searchCts?.Cancel();
                _isAdvSearching = false;
                AdvSearchActionBtn.Content = "Search";
                AdvSearchActionBtn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ControlBackgroundBrush");
                return;
            }

            string selectedFileType = (AdvFileType.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Everything";
            bool hasName = !string.IsNullOrWhiteSpace(AdvName1.Text) || !string.IsNullOrWhiteSpace(AdvName2.Text);
            bool hasContent = !string.IsNullOrWhiteSpace(AdvContent1.Text) || !string.IsNullOrWhiteSpace(AdvContent2.Text);
            bool hasLocation = !string.IsNullOrWhiteSpace(AdvLocation.Text);

            // Prevent streaming the entire hard drive if all filter fields are empty
            if (!hasName && !hasContent && !hasLocation && selectedFileType == "Everything")
            {
                return;
            }

            // Prevent accidentally grepping the entire hard drive's contents by defaulting to Documents
            if (hasContent && !hasName && !hasLocation && selectedFileType == "Everything")
            {
                selectedFileType = "Document";

                // Update the UI to reflect the change
                foreach (System.Windows.Controls.ComboBoxItem item in AdvFileType.Items)
                {
                    if (item.Content?.ToString() == "Document")
                    {
                        AdvFileType.SelectedItem = item;
                        break;
                    }
                }
            }

            // Start Search
            _isAdvSearching = true;
            AdvSearchActionBtn.Content = "Cancel";
            AdvSearchActionBtn.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#D70000")!;

            SearchResults.Clear();

            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            string? selectedDrive = "All";
            foreach (var child in AdvDrivePanel.Children)
            {
                if (child is System.Windows.Controls.RadioButton rb && rb.IsChecked == true)
                {
                    selectedDrive = rb.Content.ToString();
                    break;
                }
            }

            var request = new SearchRequest
            {
                IsAdvanced = true,
                AdvName1 = AdvName1.Text,
                AdvName2 = AdvName2.Text,
                AdvContent1 = AdvContent1.Text,
                AdvContent2 = AdvContent2.Text,
                AdvLocation = AdvLocation.Text,
                AdvCaseSensitive = AdvCaseSensitive.IsChecked == true,
                AdvDrive = selectedDrive,
                AdvFileType = selectedFileType
            };

            try
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        using var pipeClient = new NamedPipeClientStream(".", "MBRDeepSearchPipe", PipeDirection.InOut, PipeOptions.Asynchronous);
                        await pipeClient.ConnectAsync(1000, token);

                        using var writer = new StreamWriter(pipeClient, System.Text.Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                        using var reader = new StreamReader(pipeClient, System.Text.Encoding.UTF8, leaveOpen: true);

                        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);

                        string? resultPath;
                        while ((resultPath = await reader.ReadLineAsync(token)) != null)
                        {
                            if (resultPath == "---EOF---") break;

                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                if (SearchResults.Count < 1000)
                                {
                                    ImageSource? icon = GetIconForExtension(resultPath);

                                    SearchResults.Add(new SearchResult
                                    {
                                        FileName = resultPath,
                                        DisplayName = Path.GetFileName(resultPath),
                                        Icon = icon,
                                        Category = "Advanced Results",
                                        MainCategory = "Advanced Results",
                                        SubCategory = "Advanced Results"
                                    });
                                }
                            });
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            SearchResults.Add(new SearchResult { FileName = "", DisplayName = $"Engine Offline: {ex.Message}", Category = "System" });
                        });
                    }
                }, token);
            }
            finally
            {
                if (_isAdvSearching)
                {
                    _isAdvSearching = false;
                    AdvSearchActionBtn.Content = "Search";
                    AdvSearchActionBtn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ControlBackgroundBrush");
                }
            }
        }

        private void AdvField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (sender == AdvName1) _appSettings.AdvName1 = AdvName1.Text;
            else if (sender == AdvName2) _appSettings.AdvName2 = AdvName2.Text;
            else if (sender == AdvContent1) _appSettings.AdvContent1 = AdvContent1.Text;
            else if (sender == AdvContent2) _appSettings.AdvContent2 = AdvContent2.Text;
            SaveSettings();
        }

        private void AdvCaseSensitive_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingSettings) return;
            _appSettings.AdvCaseSensitive = AdvCaseSensitive.IsChecked == true;
            SaveSettings();
        }

        private void AdvFileType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (AdvFileType.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                _appSettings.AdvFileType = item.Content?.ToString() ?? "Everything";
                SaveSettings();
            }
        }

        private void AdvLocation_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            _appSettings.AdvLocation = AdvLocation.Text;
            SaveSettings();
        }

        private void AdvDrive_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (sender is System.Windows.Controls.RadioButton rb)
            {
                _appSettings.AdvDrive = rb.Content?.ToString() ?? "All";
                SaveSettings();
            }
        }

        private void AdvancedField_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                AdvSearchActionBtn_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Down && SearchResults.Count > 0)
            {
                ResultsList.Focus();
                ResultsList.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Enter && SearchResults.Count > 0)
            {
                if (ResultsList.SelectedIndex == -1)
                    ResultsList.SelectedIndex = 0;

                OpenSelectedResult();
                e.Handled = true;
            }
        }

        private void ResultsList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                OpenSelectedResult();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Up && ResultsList.SelectedIndex == 0)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private void ClearText_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.TemplatedParent is System.Windows.Controls.TextBox tb)
            {
                tb.Clear();
                tb.Focus();
            }
        }

        private void ListBoxItem_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBoxItem item && item.DataContext is SearchResult result)
            {
                OpenResult(result);
            }
        }

        private void OpenSelectedResult()
        {
            if (ResultsList.SelectedItem is SearchResult selectedResult)
            {
                OpenResult(selectedResult);
            }
        }

        private bool OpenShellItem(string rawPath, string verb = "open")
        {
            try
            {
                if (rawPath.StartsWith("::{ED7BA470-8E54-465E-825C-99712043E01C}", StringComparison.OrdinalIgnoreCase))
                {
                    Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
                    if (shellAppType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellAppType)!;
                        dynamic folder = shell.NameSpace("shell:::{ED7BA470-8E54-465E-825C-99712043E01C}");
                        if (folder != null)
                        {
                            foreach (dynamic item in folder.Items())
                            {
                                if (string.Equals(item.Path, rawPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    try { item.InvokeVerb(verb); } catch { }
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private SearchResult? GetResultFromMenuItem(object sender)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.DataContext is SearchResult result)
            {
                return result;
            }
            return null;
        }

        private void MenuItem_Open_Click(object sender, RoutedEventArgs e)
        {
            OpenResult(GetResultFromMenuItem(sender));
        }

        private void MenuItem_OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            var result = GetResultFromMenuItem(sender);
            if (result != null && !string.IsNullOrEmpty(result.FileName))
            {
                try
                {
                    if (result.FileName.StartsWith("::{ED7BA470", StringComparison.OrdinalIgnoreCase))
                    {
                        // God Mode items don't have a physical location to select
                        OpenShellItem(result.FileName, "open");
                        HideDrawer();
                        return;
                    }

                    // Opens Windows Explorer and selects the specific file
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.FileName}\"") { UseShellExecute = true });
                    HideDrawer();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Could not open location: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void MenuItem_RunAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            var result = GetResultFromMenuItem(sender);
            if (result != null && !string.IsNullOrEmpty(result.FileName))
            {
                try
                {
                    if (result.FileName.StartsWith("::{ED7BA470", StringComparison.OrdinalIgnoreCase))
                    {
                        OpenShellItem(result.FileName, "runas");
                        HideDrawer();
                        return;
                    }

                    new Process
                    {
                        StartInfo = new ProcessStartInfo(result.FileName)
                        {
                            UseShellExecute = true,
                            Verb = "runas" // Triggers the UAC elevation prompt
                        }
                    }.Start();
                    HideDrawer();
                }
                catch (Exception)
                {
                    // User likely cancelled the UAC prompt, fail silently.
                }
            }
        }

        private void MenuItem_CopyPath_Click(object sender, RoutedEventArgs e)
        {
            var result = GetResultFromMenuItem(sender);
            if (result != null && !string.IsNullOrEmpty(result.FileName))
            {
                System.Windows.Clipboard.SetText(result.FileName);
            }
        }

        private void OpenResult(SearchResult? result)
        {
            if (result != null && !string.IsNullOrEmpty(result.FileName))
            {
                try
                {
                    TrackAppOpen(result.FileName);

                    if (result.FileName.StartsWith("::{ED7BA470", StringComparison.OrdinalIgnoreCase))
                    {
                        OpenShellItem(result.FileName, "open");
                    }
                    else if (result.FileName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", result.FileName) { UseShellExecute = true });
                    }
                    else
                    {
                        // Bulletproof launch method for UWP .lnk files, Executables, and Documents.
                        // By piping through cmd.exe's native 'start' command, we completely bypass
                        // the .NET Core UseShellExecute bugs that break modern UWP shortcuts!
                        Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{result.FileName}\"")
                        {
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                    }

                    // Hide the app drawer after opening
                    HideDrawer();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Could not open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void UpdateBackgroundColor()
        {
            System.Windows.Media.Color baseColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_appSettings.ColorPalette switch
            {
                "OLED Pitch Black" => "#000000",
                "Light Theme" => "#FFFFFF",
                "Custom" => _appSettings.CustomColorHex ?? "#1C1C1C",
                _ => "#1C1C1C" // MBR-Deep Dark
            });

            SolidColorBrush primaryBrush;
            SolidColorBrush secondaryBrush;

            double luminance = (0.299 * baseColor.R + 0.587 * baseColor.G + 0.114 * baseColor.B) / 255.0;
            bool isLightBg = luminance > 0.6;

            if (_appSettings.FontColorMode == "Custom")
            {
                System.Windows.Media.Color customFontColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_appSettings.CustomFontColorHex ?? "#FFFFFF");
                primaryBrush = new SolidColorBrush(customFontColor);

                System.Windows.Media.Color secondaryColor = customFontColor;
                secondaryColor.A = (byte)(customFontColor.A * 0.6);
                secondaryBrush = new SolidColorBrush(secondaryColor);
            }
            else
            {
                primaryBrush = new SolidColorBrush(isLightBg ? System.Windows.Media.Color.FromRgb(20, 20, 20) : System.Windows.Media.Colors.White);
                secondaryBrush = new SolidColorBrush(isLightBg ? System.Windows.Media.Color.FromRgb(80, 80, 80) : System.Windows.Media.Color.FromRgb(170, 170, 170));
            }

            primaryBrush.Freeze();
            secondaryBrush.Freeze();

            this.Resources["PrimaryTextBrush"] = primaryBrush;
            this.Resources["SecondaryTextBrush"] = secondaryBrush;

            // Create dynamic brushes for UI controls
            var controlBgBrush = new SolidColorBrush(isLightBg ? System.Windows.Media.Color.FromArgb(26, 0, 0, 0) : System.Windows.Media.Color.FromArgb(26, 255, 255, 255));
            var controlHoverBrush = new SolidColorBrush(isLightBg ? System.Windows.Media.Color.FromArgb(51, 0, 0, 0) : System.Windows.Media.Color.FromArgb(51, 255, 255, 255));
            var controlBorderBrush = new SolidColorBrush(isLightBg ? System.Windows.Media.Color.FromArgb(51, 0, 0, 0) : System.Windows.Media.Color.FromArgb(255, 74, 74, 74));
            controlBgBrush.Freeze();
            controlHoverBrush.Freeze();
            controlBorderBrush.Freeze();
            this.Resources["ControlBackgroundBrush"] = controlBgBrush;
            this.Resources["ControlHoverBrush"] = controlHoverBrush;
            this.Resources["ControlBorderBrush"] = controlBorderBrush;

            double opacity = _appSettings.BackgroundOpacity / 100.0;
            baseColor.A = (byte)(Math.Max(0.0, Math.Min(1.0, opacity)) * 255);
            this.Background = new SolidColorBrush(baseColor);
            this.Resources["WindowBackgroundBrush"] = this.Background;
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            TabApps.IsChecked = false;
            TabSettings.IsChecked = false;
            TabPerf.IsChecked = false;

            PerformanceGrid.Visibility = Visibility.Collapsed;
            DetailedCpuGrid.Visibility = Visibility.Collapsed;
            DetailedRamGrid.Visibility = Visibility.Collapsed;
            DetailedGpuGrid.Visibility = Visibility.Collapsed;
            DetailedDiskGrid.Visibility = Visibility.Collapsed;
            DetailedNetGrid.Visibility = Visibility.Collapsed;
            ResultsList.Visibility = Visibility.Collapsed;
            BasicSearchPanel.Visibility = Visibility.Collapsed;
            AdvancedPanel.Visibility = Visibility.Collapsed;

            _perfTimer?.Stop();
            _telemetryCts?.Cancel();

            SettingsDashboardGrid.Visibility = Visibility.Visible;
        }

        private void SettingsColorPalette_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (SettingsColorPalette.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                _appSettings.ColorPalette = item.Content?.ToString() ?? "MBR-Deep Dark";

                if (CustomColorPanel != null)
                {
                    CustomColorPanel.Visibility = _appSettings.ColorPalette == "Custom" ? Visibility.Visible : Visibility.Collapsed;
                }
                UpdateBackgroundColor();
                SaveSettings();
            }
        }

        private void SettingsOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializingSettings) return;
            _appSettings.BackgroundOpacity = SettingsOpacitySlider.Value;
            UpdateBackgroundColor();
            SaveSettings();
        }

        private void CustomColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializingSettings) return;

            double h = SliderHue?.Value ?? 0;
            double l = (SliderLight?.Value ?? 0) / 100.0;

            if (TextHue != null) TextHue.Text = Math.Round(h).ToString();
            if (TextLight != null) TextLight.Text = Math.Round(l * 100.0).ToString();

            var color = HslToRgb(h, 1.0, l);
            if (ColorPreview != null) ColorPreview.Background = new SolidColorBrush(color);

            _appSettings.CustomColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            UpdateBackgroundColor();
            SaveSettings();
        }

        private void SettingsFontColorMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (SettingsFontColorMode.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                _appSettings.FontColorMode = item.Content?.ToString() ?? "Auto";

                if (CustomFontColorPanel != null)
                {
                    CustomFontColorPanel.Visibility = _appSettings.FontColorMode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
                }
                UpdateBackgroundColor();
                SaveSettings();
            }
        }

        private void CustomFontColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializingSettings) return;

            double h = SliderFontHue?.Value ?? 0;
            double l = (SliderFontLight?.Value ?? 0) / 100.0;

            if (TextFontHue != null) TextFontHue.Text = Math.Round(h).ToString();
            if (TextFontLight != null) TextFontLight.Text = Math.Round(l * 100.0).ToString();

            var color = HslToRgb(h, 1.0, l);
            if (FontColorPreview != null) FontColorPreview.Background = new SolidColorBrush(color);

            _appSettings.CustomFontColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            UpdateBackgroundColor();
            SaveSettings();
        }

        private void UpdateFontFamily()
        {
            try
            {
                this.FontFamily = new System.Windows.Media.FontFamily(_appSettings.FontFamily);
                this.FontWeight = _appSettings.FontBold ? FontWeights.Bold : FontWeights.Normal;
            }
            catch
            {
                this.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
                this.FontWeight = FontWeights.Normal;
            }
        }

        private void SettingsFontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (SettingsFontFamily.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                _appSettings.FontFamily = item.Content?.ToString() ?? "Segoe UI";
                UpdateFontFamily();
                SaveSettings();
            }
        }

        private void SettingsFontBold_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingSettings) return;
            _appSettings.FontBold = SettingsFontBold.IsChecked == true;
            UpdateFontFamily();
            SaveSettings();
        }

        private static System.Windows.Media.Color HslToRgb(double h, double s, double l)
        {
            byte r = 0, g = 0, b = 0;
            if (s == 0)
            {
                r = g = b = (byte)(l * 255);
            }
            else
            {
                double v1, v2;
                double hue = h / 360.0;

                v2 = (l < 0.5) ? (l * (1 + s)) : ((l + s) - (l * s));
                v1 = 2 * l - v2;

                r = (byte)(255 * HueToRgb(v1, v2, hue + (1.0 / 3.0)));
                g = (byte)(255 * HueToRgb(v1, v2, hue));
                b = (byte)(255 * HueToRgb(v1, v2, hue - (1.0 / 3.0)));
            }
            return System.Windows.Media.Color.FromRgb(r, g, b);
        }

        private static double HueToRgb(double v1, double v2, double vH)
        {
            if (vH < 0) vH += 1;
            if (vH > 1) vH -= 1;
            if ((6 * vH) < 1) return (v1 + (v2 - v1) * 6 * vH);
            if ((2 * vH) < 1) return v2;
            if ((3 * vH) < 2) return (v1 + (v2 - v1) * ((2.0 / 3.0) - vH) * 6);
            return v1;
        }

        private static (double H, double S, double L) RgbToHsl(System.Windows.Media.Color color)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double min = Math.Min(r, Math.Min(g, b));
            double max = Math.Max(r, Math.Max(g, b));
            double delta = max - min;

            double l = (max + min) / 2.0;
            double h = 0, s = 0;

            if (delta > 0)
            {
                s = (l < 0.5) ? (delta / (max + min)) : (delta / (2.0 - max - min));
                double del_r = (((max - r) / 6.0) + (delta / 2.0)) / delta;
                double del_g = (((max - g) / 6.0) + (delta / 2.0)) / delta;
                double del_b = (((max - b) / 6.0) + (delta / 2.0)) / delta;

                if (r == max) h = del_b - del_g;
                else if (g == max) h = (1.0 / 3.0) + del_r - del_b;
                else if (b == max) h = (2.0 / 3.0) + del_g - del_r;

                if (h < 0) h += 1;
                if (h > 1) h -= 1;
            }
            return (h * 360, s, l);
        }

        private void SettingsEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingSettings) return;
            if (SettingsEffect.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                _appSettings.TransitionEffect = item.Content?.ToString() ?? "Genie";
                SaveSettings();
            }
        }

        private void SettingsSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializingSettings) return;
            _appSettings.AnimationSpeed = SettingsSpeedSlider.Value;
            SaveSettings();
        }

        private void SettingsDrawerIconSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializingSettings) return;
            _appSettings.DrawerIconSize = SettingsDrawerIconSize.Value;
            DrawerIconSize = _appSettings.DrawerIconSize;
            SaveSettings();
        }

        private void SettingsSidebarIconSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializingSettings) return;
            _appSettings.SidebarIconSize = SettingsSidebarIconSize.Value;
            SidebarIconSize = _appSettings.SidebarIconSize;
            SaveSettings();
        }

        // --- Sidebar Navigation Click Handlers ---
        private void Sidebar_Computer_Click(object sender, RoutedEventArgs e)
        {
            // Use the native shell GUID to instantly open "This PC"
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_Computer_Manage_Click(object sender, RoutedEventArgs e)
        {
            // Launch Computer Management (Requires UAC)
            try { Process.Start(new ProcessStartInfo("compmgmt.msc") { UseShellExecute = true, Verb = "runas" }); HideDrawer(); } catch { }
        }

        private void Sidebar_Computer_Classic_Click(object sender, RoutedEventArgs e)
        {
            // Launch classic System Properties (sysdm.cpl)
            try { Process.Start(new ProcessStartInfo("sysdm.cpl") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_Computer_Properties_Click(object sender, RoutedEventArgs e)
        {
            // Launch System Properties (maps to modern Settings > About on Win 10/11)
            try { Process.Start(new ProcessStartInfo("control.exe", "system") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_ControlPanel_Click(object sender, RoutedEventArgs e)
        {
            // Launch the classic Control Panel
            try { Process.Start(new ProcessStartInfo("control.exe") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_ControlPanel_Network_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.NetworkAndSharingCenter") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_ControlPanel_Mouse_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "main.cpl") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_ControlPanel_Power_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "powercfg.cpl") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_ControlPanel_Programs_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "appwiz.cpl") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_ControlPanel_Sound_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_ControlPanel_Users_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.UserAccounts") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_ControlPanel_GodMode_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_Devices_Click(object sender, RoutedEventArgs e)
        {
            // Launch the classic Devices and Printers panel
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_DefaultApps_Click(object sender, RoutedEventArgs e)
        {
            // Launch the classic Default Programs panel
            try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.DefaultPrograms") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private void Sidebar_Performance_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); HideDrawer(); } catch { }
        }

        private async void Sidebar_Start_Click(object sender, RoutedEventArgs e)
        {
            HideDrawer();

            // Give Windows a moment to shift focus after the drawer hides,
            // otherwise the synthetic keystroke gets swallowed!
            await Task.Delay(100);

            // Use Ctrl + Esc, which is deeply hardcoded into Windows as a
            // bulletproof way to summon the Start Menu
            keybd_event(0x11, 0, 0, 0);       // VK_CONTROL Down
            keybd_event(0x1B, 0, 0, 0);       // VK_ESCAPE Down
            keybd_event(0x1B, 0, 0x0002, 0);  // VK_ESCAPE Up
            keybd_event(0x11, 0, 0x0002, 0);  // VK_CONTROL Up
        }

        private void Sidebar_Power_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void Sidebar_Power_Logout_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", "/l") { UseShellExecute = true }); } catch { }
        }

        private void Sidebar_Power_Restart_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = true }); } catch { }
        }

        private void Sidebar_Power_Shutdown_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = true }); } catch { }
        }

        private void CpuTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PerformanceGrid.Visibility = Visibility.Collapsed;
            DetailedCpuGrid.Visibility = Visibility.Visible;
            _isDetailedCpuView = true;

            NativeMonitor.GetCoreUsages(); // Initialize baseline to guarantee an accurate first read

            int coreCount = Environment.ProcessorCount;
            if (_coreHistory == null)
            {
                _coreHistory = new double[coreCount][];
                _coreCanvases = new Canvas[coreCount];
                _coreLines = new System.Windows.Shapes.Polyline[coreCount];
                _coreShades = new System.Windows.Shapes.Polygon[coreCount];
                _coreTexts = new TextBlock[coreCount];
                _coreTempTexts = new TextBlock[coreCount];

                CpuCoresPanel.Children.Clear();

                for (int i = 0; i < coreCount; i++)
                {
                    _coreHistory[i] = new double[60];
                    var border = new Border { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(5) };
                    border.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                    var grid = new Grid { Margin = new Thickness(10) };
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                    var headerPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    headerPanel.Children.Add(new TextBlock { Text = $"Core {i}", Foreground = System.Windows.Media.Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold });
                    var pctText = new TextBlock { Text = "0%", Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0078D7")), FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 0) };
                    var tempText = new TextBlock { Text = "-- °C", Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#aaaaaa")), FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(15, 0, 0, 0) };
                    _coreTexts[i] = pctText;
                    _coreTempTexts[i] = tempText;
                    headerPanel.Children.Add(pctText);
                    headerPanel.Children.Add(tempText);
                    grid.Children.Add(headerPanel);

                    var canvas = new Canvas { Margin = new Thickness(0, 10, 0, 0), ClipToBounds = true };
                    Grid.SetRow(canvas, 1);
                    _coreCanvases[i] = canvas;

                    var shade = new System.Windows.Shapes.Polygon { Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#330078D7")) };
                    _coreShades[i] = shade;
                    canvas.Children.Add(shade);
                    var line = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CC0078D7")), StrokeThickness = 1 };
                    _coreLines[i] = line;
                    canvas.Children.Add(line);
                    grid.Children.Add(canvas);
                    border.Child = grid;
                    int index = i;
                    canvas.SizeChanged += (s, ev) => { if (_isDetailedCpuView) DrawGraph(canvas, line, shade, _coreHistory[index], _historyIndex); };
                    CpuCoresPanel.Children.Add(border);
                }
            }
        }

        private void RamTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PerformanceGrid.Visibility = Visibility.Collapsed;
            DetailedRamGrid.Visibility = Visibility.Visible;
            _isDetailedRamView = true;
        }

        private void GpuTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PerformanceGrid.Visibility = Visibility.Collapsed;
            DetailedGpuGrid.Visibility = Visibility.Visible;
            _isDetailedGpuView = true;
        }

        private void DiskTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PerformanceGrid.Visibility = Visibility.Collapsed;
            DetailedDiskGrid.Visibility = Visibility.Visible;
            _isDetailedDiskView = true;

            var disks = NativeMonitor.GetDetailedDiskUsages();
            int diskCount = disks.Length;

            if (_detailedDiskHistory == null || _detailedDiskHistory.Length != diskCount)
            {
                _detailedDiskHistory = new double[diskCount][];
                _detailedDiskCanvases = new Canvas[diskCount];
                _detailedDiskLines = new System.Windows.Shapes.Polyline[diskCount];
                _detailedDiskShades = new System.Windows.Shapes.Polygon[diskCount];
                _detailedDiskActiveTexts = new TextBlock[diskCount];
                _detailedDiskReadTexts = new TextBlock[diskCount];
                _detailedDiskWriteTexts = new TextBlock[diskCount];

                DetailedDiskPanel.Children.Clear();

                for (int i = 0; i < diskCount; i++)
                {
                    _detailedDiskHistory[i] = new double[60];
                    var border = new Border { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(5) };
                    border.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                    var grid = new Grid { Margin = new Thickness(10) };
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var headerPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    headerPanel.Children.Add(new TextBlock { Text = disks[i].Name, Foreground = System.Windows.Media.Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });

                    var activeText = new TextBlock { Text = "0%", Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D70078")), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(15, 0, 0, 0) };
                    _detailedDiskActiveTexts[i] = activeText;
                    headerPanel.Children.Add(activeText);
                    grid.Children.Add(headerPanel);

                    var canvas = new Canvas { Margin = new Thickness(0, 10, 0, 10), ClipToBounds = true };
                    Grid.SetRow(canvas, 1);
                    _detailedDiskCanvases[i] = canvas;

                    var shade = new System.Windows.Shapes.Polygon { Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#33D70078")) };
                    _detailedDiskShades[i] = shade;
                    canvas.Children.Add(shade);
                    var line = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCD70078")), StrokeThickness = 1 };
                    _detailedDiskLines[i] = line;
                    canvas.Children.Add(line);
                    grid.Children.Add(canvas);

                    var footerPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                    var readText = new TextBlock { Text = "R: 0 KB/s", Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#aaaaaa")), FontSize = 14, Margin = new Thickness(0, 0, 15, 0) };
                    var writeText = new TextBlock { Text = "W: 0 KB/s", Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#aaaaaa")), FontSize = 14 };
                    _detailedDiskReadTexts[i] = readText;
                    _detailedDiskWriteTexts[i] = writeText;
                    footerPanel.Children.Add(readText);
                    footerPanel.Children.Add(writeText);
                    Grid.SetRow(footerPanel, 2);
                    grid.Children.Add(footerPanel);

                    border.Child = grid;
                    int index = i;
                    canvas.SizeChanged += (s, ev) => { if (_isDetailedDiskView) DrawGraph(canvas, line, shade, _detailedDiskHistory[index], _historyIndex); };
                    DetailedDiskPanel.Children.Add(border);
                }
            }
        }

        private void NetTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PerformanceGrid.Visibility = Visibility.Collapsed;
            DetailedNetGrid.Visibility = Visibility.Visible;
            _isDetailedNetView = true;
        }

        private void DetailedBackBtn_Click(object sender, RoutedEventArgs e)
        {
            DetailedCpuGrid.Visibility = Visibility.Collapsed;
            DetailedRamGrid.Visibility = Visibility.Collapsed;
            DetailedGpuGrid.Visibility = Visibility.Collapsed;
            DetailedDiskGrid.Visibility = Visibility.Collapsed;
            DetailedNetGrid.Visibility = Visibility.Collapsed;
            PerformanceGrid.Visibility = Visibility.Visible;
            _isDetailedCpuView = false;
            _isDetailedRamView = false;
            _isDetailedGpuView = false;
            _isDetailedDiskView = false;
            _isDetailedNetView = false;
        }

        // --- Performance Drawing Logic ---
        private void PerfTimer_Tick(object? sender, EventArgs e)
        {
            if (PerformanceGrid.Visibility != Visibility.Visible &&
                DetailedCpuGrid.Visibility != Visibility.Visible &&
                DetailedRamGrid.Visibility != Visibility.Visible &&
                DetailedGpuGrid.Visibility != Visibility.Visible &&
                DetailedDiskGrid.Visibility != Visibility.Visible &&
                DetailedNetGrid.Visibility != Visibility.Visible)
            {
                _perfTimer?.Stop();
                return;
            }

            double cpu = NativeMonitor.GetCpuUsage();
            var ram = NativeMonitor.GetMemoryUsage();
            var gpu = NativeMonitor.GetGpuUsageAndMemory();
            var disk = NativeMonitor.GetDiskUsage();
            var net = NativeMonitor.GetNetworkUsage();

            _cpuHistory[_historyIndex] = cpu;
            _ramHistory[_historyIndex] = ram.Percentage;
            _gpuHistory[_historyIndex] = gpu.Percentage;
            _diskHistory[_historyIndex] = disk.Percentage;

            // Scale network dynamically (1MB/s minimum top bounds)
            double totalNetBps = net.RecvBps + net.SentBps;
            _netHistory[_historyIndex] = totalNetBps;

            double maxNet = 1024 * 1024;
            foreach (var val in _netHistory) { if (val > maxNet) maxNet = val; }
            double[] scaledNet = new double[60];
            for (int i = 0; i < 60; i++) scaledNet[i] = (_netHistory[i] / maxNet) * 100.0;

            if (_isDetailedCpuView && _coreHistory != null && _coreTexts != null)
            {
                var coreUsages = NativeMonitor.GetCoreUsages();
                int currentIndex = _historyIndex;
                for (int i = 0; i < coreUsages.Length && i < _coreHistory.Length; i++)
                {
                    _coreHistory[i][currentIndex] = coreUsages[i];
                    _coreTexts[i].Text = $"{coreUsages[i]:F0}%";
                }
            }

            if (_isDetailedDiskView && _detailedDiskHistory != null && _detailedDiskActiveTexts != null && _detailedDiskReadTexts != null && _detailedDiskWriteTexts != null)
            {
                var detailedDisks = NativeMonitor.GetDetailedDiskUsages();
                int currentIndex = _historyIndex;
                for (int i = 0; i < detailedDisks.Length && i < _detailedDiskHistory.Length; i++)
                {
                    _detailedDiskHistory[i][currentIndex] = detailedDisks[i].Percentage;
                    _detailedDiskActiveTexts[i].Text = $"{detailedDisks[i].Percentage:F0}%";
                    _detailedDiskReadTexts[i].Text = $"R: {FormatBytes(detailedDisks[i].ReadBps)}";
                    _detailedDiskWriteTexts[i].Text = $"W: {FormatBytes(detailedDisks[i].WriteBps)}";
                }
            }

            _historyIndex = (_historyIndex + 1) % 60;

            CpuText.Text = $"{cpu:F0}%";
            RamText.Text = $"{ram.Percentage:F0}%";
            RamDetailText.Text = $"{ram.UsedGB:F1} / {ram.TotalGB:F1} GB";

            GpuText.Text = $"{gpu.Percentage:F0}%";
            GpuDetailText.Text = $"{gpu.MemoryGB:F1} GB";

            DiskText.Text = $"{disk.Percentage:F0}%";
            DiskDetailText.Text = $"R: {FormatBytes(disk.ReadBps)} / W: {FormatBytes(disk.WriteBps)}";

            NetText.Text = FormatBytes(totalNetBps);
            NetDetailText.Text = $"R: {FormatBytes(net.RecvBps)} / S: {FormatBytes(net.SentBps)}";

            DrawGraph(CpuCanvas, CpuLine, CpuShade, _cpuHistory, _historyIndex);
            DrawGraph(RamCanvas, RamLine, RamShade, _ramHistory, _historyIndex);
            DrawGraph(GpuCanvas, GpuLine, GpuShade, _gpuHistory, _historyIndex);
            DrawGraph(DiskCanvas, DiskLine, DiskShade, _diskHistory, _historyIndex);
            DrawGraph(NetCanvas, NetLine, NetShade, scaledNet, _historyIndex);

            if (_isDetailedCpuView && _coreHistory != null && _coreCanvases != null && _coreLines != null && _coreShades != null)
            {
                for (int i = 0; i < _coreHistory.Length; i++)
                {
                    DrawGraph(_coreCanvases[i], _coreLines[i], _coreShades[i], _coreHistory[i], _historyIndex);
                }
            }

            if (_isDetailedDiskView && _detailedDiskHistory != null && _detailedDiskCanvases != null && _detailedDiskLines != null && _detailedDiskShades != null)
            {
                for (int i = 0; i < _detailedDiskHistory.Length; i++)
                {
                    DrawGraph(_detailedDiskCanvases[i], _detailedDiskLines[i], _detailedDiskShades[i], _detailedDiskHistory[i], _historyIndex);
                }
            }

            if (DetailedRamGrid.Visibility == Visibility.Visible)
            {
                DetailedRamUsed.Text = $"{ram.UsedGB:F1} GB";
                DetailedRamAvail.Text = $"{(ram.TotalGB - ram.UsedGB):F1} GB";
                DetailedRamTotal.Text = $"{ram.TotalGB:F1} GB";
                DrawGraph(DetailedRamCanvas, DetailedRamLine, DetailedRamShade, _ramHistory, _historyIndex);
            }

            if (DetailedGpuGrid.Visibility == Visibility.Visible)
            {
                DetailedGpuUtil.Text = $"{gpu.Percentage:F0}%";
                DetailedGpuMem.Text = $"{gpu.MemoryGB:F1} GB";
                DrawGraph(DetailedGpuCanvas, DetailedGpuLine, DetailedGpuShade, _gpuHistory, _historyIndex);
            }

            if (DetailedNetGrid.Visibility == Visibility.Visible)
            {
                DetailedNetSend.Text = FormatBytes(net.SentBps);
                DetailedNetRecv.Text = FormatBytes(net.RecvBps);
                DrawGraph(DetailedNetCanvas, DetailedNetLine, DetailedNetShade, scaledNet, _historyIndex);
            }
        }

        private string FormatBytes(double bytes)
        {
            if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024 * 1024):F1} GB/s";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024 * 1024):F1} MB/s";
            if (bytes >= 1024) return $"{bytes / 1024:F1} KB/s";
            return $"{bytes:F0} B/s";
        }

        private void DrawGraph(Canvas canvas, System.Windows.Shapes.Polyline line, System.Windows.Shapes.Polygon shade, double[] history, int headIndex)
        {
            if (canvas.ActualWidth == 0 || canvas.ActualHeight == 0) return;

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;

            // Re-use collections to prevent Garbage Collection (GC) pressure and memory inflation
            var points = line.Points;
            if (points.Count != 60)
            {
                points.Clear();
                for (int i = 0; i < 60; i++) points.Add(new System.Windows.Point(0, height));
            }

            var shadePoints = shade.Points;
            if (shadePoints.Count != 62)
            {
                shadePoints.Clear();
                for (int i = 0; i < 62; i++) shadePoints.Add(new System.Windows.Point(0, height));
            }

            for (int i = 0; i < 60; i++)
            {
                int idx = (headIndex + i) % 60;
                double val = history[idx];

                double x = (width / 59.0) * i;
                double y = height - ((val / 100.0) * height);

                var pt = new System.Windows.Point(x, y);
                points[i] = pt;
                shadePoints[i] = pt;
            }

            shadePoints[60] = new System.Windows.Point(width, height);
            shadePoints[61] = new System.Windows.Point(0, height);
        }

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double maxNet = 1024 * 1024;
            foreach (var val in _netHistory) { if (val > maxNet) maxNet = val; }
            double[] scaledNet = new double[60];
            for (int i = 0; i < 60; i++) scaledNet[i] = (_netHistory[i] / maxNet) * 100.0;

            if (PerformanceGrid.Visibility == Visibility.Visible)
            {
                DrawGraph(CpuCanvas, CpuLine, CpuShade, _cpuHistory, _historyIndex);
                DrawGraph(RamCanvas, RamLine, RamShade, _ramHistory, _historyIndex);
                DrawGraph(GpuCanvas, GpuLine, GpuShade, _gpuHistory, _historyIndex);
                DrawGraph(DiskCanvas, DiskLine, DiskShade, _diskHistory, _historyIndex);
                DrawGraph(NetCanvas, NetLine, NetShade, scaledNet, _historyIndex);
            }

            if (DetailedRamGrid.Visibility == Visibility.Visible) DrawGraph(DetailedRamCanvas, DetailedRamLine, DetailedRamShade, _ramHistory, _historyIndex);
            if (DetailedGpuGrid.Visibility == Visibility.Visible) DrawGraph(DetailedGpuCanvas, DetailedGpuLine, DetailedGpuShade, _gpuHistory, _historyIndex);
            if (DetailedNetGrid.Visibility == Visibility.Visible) DrawGraph(DetailedNetCanvas, DetailedNetLine, DetailedNetShade, scaledNet, _historyIndex);
        }
    }

    // Data model for the XAML Binding
    public class SearchResult
    {
        public string? FileName { get; set; }
        public string? DisplayName { get; set; }
        // Changed from string to ImageSource for direct XAML binding
        public ImageSource? Icon { get; set; }
        public string Category { get; set; } = "Files";
        public string MainCategory { get; set; } = "Files";
        public string SubCategory { get; set; } = "Files";
    }

    public class SearchRequest
    {
        public bool IsAdvanced { get; set; }
        public string? BasicQuery { get; set; }
        public string? AdvName1 { get; set; }
        public string? AdvName2 { get; set; }
        public string? AdvContent1 { get; set; }
        public string? AdvContent2 { get; set; }
        public string? AdvLocation { get; set; }
        public bool AdvCaseSensitive { get; set; }
        public string? AdvDrive { get; set; }
        public string? AdvFileType { get; set; }
    }

    public class TelemetryData
    {
        public float CpuTemp { get; set; }
        public float GpuTemp { get; set; }
        public System.Collections.Generic.Dictionary<int, float>? CoreTemps { get; set; }
    }

    public class AppSettings
    {
        public string ColorPalette { get; set; } = "MBR-Deep Dark";
        public double BackgroundOpacity { get; set; } = 90.0;
        public string CustomColorHex { get; set; } = "#1C1C1C";
        public string FontColorMode { get; set; } = "Auto";
        public string FontFamily { get; set; } = "Segoe UI";
        public bool FontBold { get; set; } = true;
        public string CustomFontColorHex { get; set; } = "#FFFFFF";
        public string TransitionEffect { get; set; } = "None";
        public double AnimationSpeed { get; set; } = 0.5;
        public double DrawerIconSize { get; set; } = 64.0;
        public double SidebarIconSize { get; set; } = 72.0;
        public string SortMode { get; set; } = "A-Z";
        public bool AdvCaseSensitive { get; set; } = false;
        public string AdvFileType { get; set; } = "Everything";
        public string AdvLocation { get; set; } = "";
        public string AdvDrive { get; set; } = "All";
        public string AdvName1 { get; set; } = "";
        public string AdvName2 { get; set; } = "";
        public string AdvContent1 { get; set; } = "";
        public string AdvContent2 { get; set; } = "";
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder ppszFileName);
    }
}
