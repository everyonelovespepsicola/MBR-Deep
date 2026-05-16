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

namespace AppDrawerXAML
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

        [DllImport("shell32.dll")]
        public static extern int SHGetSpecialFolderLocation(IntPtr hwndOwner, int nFolder, out IntPtr ppidl);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint SHGFI_LARGEICON = 0x000000000; // 0x0 implies Large icon
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint SHGFI_PIDL = 0x00000008;
        private const int CSIDL_DRIVES = 0x0011; // My Computer / This PC

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

        private string currentSearchTerm = "";
        private CancellationTokenSource? _searchCts;

        // --- System Tray Icon ---
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        // Flag to determine if we are actually quitting the app
        private bool _isExplicitExit = false;
        private DateTime _lastDeactivated;
        private bool _isAdvSearching = false;

        public ImageSource? IconThisPC { get; set; }
        public ImageSource? IconControlPanel { get; set; }
        public ImageSource? IconDevices { get; set; }
        public ImageSource? IconDefaultApps { get; set; }
        public ImageSource? IconPerformance { get; set; }

        public AppDrawerWindow()
        {
            InitializeComponent();
            LoadSidebarIcons();
            SearchResults = new ObservableCollection<SearchResult>();

            // Set up CollectionViewSource for Grouping
            var cvs = new CollectionViewSource { Source = SearchResults };
            cvs.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
            ResultsList.ItemsSource = cvs.View;

            LoadRecents();

            // Load Start Menu apps in the background instantly
            _ = LoadDefaultAppDrawerAsync();

            // Load God Mode items in the background
            _ = Task.Run(() => LoadGodModeItems());

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
                this.Hide();
            };

            // 3. Focus the search box instantly when the window is shown
            this.IsVisibleChanged += (s, e) =>
            {
                if (this.IsVisible)
                {
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                }
                else
                {
                    // Reset the UI cleanly when the window is hidden
                    SearchBox.Text = "";

                    AdvName1.Text = "";
                    AdvName2.Text = "";
                    AdvContent1.Text = "";
                    AdvContent2.Text = "";
                    AdvLocation.Text = "";
                    AdvFileType.SelectedIndex = 0;
                    AdvCaseSensitive.IsChecked = false;

                    foreach (var child in AdvDrivePanel.Children)
                    {
                        if (child is System.Windows.Controls.RadioButton rb && rb.Content?.ToString() == "All")
                        {
                            rb.IsChecked = true;
                            break;
                        }
                    }

                    if (_isAdvSearching)
                    {
                        _searchCts?.Cancel();
                        _isAdvSearching = false;
                        AdvSearchActionBtn.Content = "Search";
                        AdvSearchActionBtn.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#0078D7")!;
                    }

                    AdvancedPanel.Visibility = Visibility.Collapsed;
                    BasicSearchPanel.Visibility = Visibility.Visible;

                    if (TabSettings.IsChecked == true)
                    {
                        SortMode_Changed(this, new RoutedEventArgs());
                    }
                    else
                    {
                        ShowDefaultApps();
                    }
                }
            };

            // 4. Hook the Keyboard via our fast unmanaged C-Engine
            _toggleCallback = () =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Prevent immediate re-opening if clicking the Start Button is what caused the window to lose focus and hide
                    if ((DateTime.Now - _lastDeactivated).TotalMilliseconds < 200) return;

                    if (this.IsVisible) this.Hide();
                    else
                    {
                        this.Show();
                        this.Activate();
                    }
                }));
            };
            InstallSystemHooks(_toggleCallback);

            // Populate Drive RadioButtons
            var rbAll = new System.Windows.Controls.RadioButton
            {
                Content = "All",
                GroupName = "AdvDriveGroup",
                IsChecked = true,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 16,
                Margin = new Thickness(0, 0, 15, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            AdvDrivePanel.Children.Add(rbAll);

            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                {
                    var rb = new System.Windows.Controls.RadioButton
                    {
                        Content = drive.Name.Substring(0, 1),
                        GroupName = "AdvDriveGroup",
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 16,
                        Margin = new Thickness(0, 0, 15, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    AdvDrivePanel.Children.Add(rb);
                }
            }
        }

        private void NotifyIcon_MouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                // Prevent immediate re-opening if clicking the tray icon is what caused the window to lose focus and hide
                if ((DateTime.Now - _lastDeactivated).TotalMilliseconds < 200) return;

                if (this.IsVisible) this.Hide();
                else
                {
                    this.Show();
                    this.Activate(); // Bring window to the front
                }
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
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                if (this.IsVisible)
                {
                    this.Hide();
                }
                else
                {
                    this.Show();
                    this.Activate(); // Bring window to the front
                }
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
                this.Hide();
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

                            shortcutPaths.Add(file);
                        }
                    }
                    catch { /* Ignore access exceptions if any folders are locked */ }
                }

                // Grab all standard Windows Start Menu locations
                AddShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
                AddShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.Programs));

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
                AdvSearchActionBtn.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#0078D7")!;
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
                    AdvSearchActionBtn.Background = (SolidColorBrush)new BrushConverter().ConvertFrom("#0078D7")!;
                }
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
                        this.Hide();
                        return;
                    }

                    // Opens Windows Explorer and selects the specific file
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.FileName}\"") { UseShellExecute = true });
                    this.Hide();
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
                        this.Hide();
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
                    this.Hide();
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
                    this.Hide();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Could not open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // --- Sidebar Navigation Click Handlers ---
        private void Sidebar_Computer_Click(object sender, RoutedEventArgs e)
        {
            // Use the native shell GUID to instantly open "This PC"
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_Computer_Manage_Click(object sender, RoutedEventArgs e)
        {
            // Launch Computer Management (Requires UAC)
            try { Process.Start(new ProcessStartInfo("compmgmt.msc") { UseShellExecute = true, Verb = "runas" }); this.Hide(); } catch { }
        }

        private void Sidebar_Computer_Classic_Click(object sender, RoutedEventArgs e)
        {
            // Launch classic System Properties (sysdm.cpl)
            try { Process.Start(new ProcessStartInfo("sysdm.cpl") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_Computer_Properties_Click(object sender, RoutedEventArgs e)
        {
            // Launch System Properties (maps to modern Settings > About on Win 10/11)
            try { Process.Start(new ProcessStartInfo("control.exe", "system") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_ControlPanel_Click(object sender, RoutedEventArgs e)
        {
            // Launch the classic Control Panel
            try { Process.Start(new ProcessStartInfo("control.exe") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_ControlPanel_Network_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.NetworkAndSharingCenter") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_ControlPanel_Mouse_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "main.cpl") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_ControlPanel_Power_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "powercfg.cpl") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_ControlPanel_Programs_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "appwiz.cpl") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_ControlPanel_Sound_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_ControlPanel_Users_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.UserAccounts") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_ControlPanel_GodMode_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_Devices_Click(object sender, RoutedEventArgs e)
        {
            // Launch the classic Devices and Printers panel
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_DefaultApps_Click(object sender, RoutedEventArgs e)
        {
            // Launch the classic Default Programs panel
            try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.DefaultPrograms") { UseShellExecute = true }); this.Hide(); } catch { }
        }

        private void Sidebar_Performance_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); this.Hide(); } catch { }
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
