import ctypes
import os
import sys
import subprocess
import threading
import queue
import concurrent.futures

# Prevent PyInstaller --windowed invalid file descriptor crashes from native C libraries
if sys.stdout is None:
    sys.stdout = open(os.devnull, 'w')
if sys.stderr is None:
    sys.stderr = open(os.devnull, 'w')

try:
    import pypdfium2 as pdfium
    HAS_PDFIUM = True
except ImportError:
    HAS_PDFIUM = False

try:
    import win32gui
    import win32ui
    import win32con
    import win32com.shell.shell as shell
    import win32com.shell.shellcon as shellcon
    from PIL import Image, ImageTk
    HAS_PYWIN32_PILLOW = True
except ImportError:
    HAS_PYWIN32_PILLOW = False
    print("[!] WARNING: pywin32 or Pillow not found. Icons disabled. Run 'pip install pywin32 pillow'")

icon_cache = {}

def get_icon(ext, is_folder=False):
    if not HAS_PYWIN32_PILLOW:
        return None

    cache_key = "folder" if is_folder else ext
    if cache_key in icon_cache:
        return icon_cache[cache_key]

    try:
        flags = shellcon.SHGFI_ICON | shellcon.SHGFI_SMALLICON
        if is_folder:
            path_to_check = "C:\\"
            attrs = win32con.FILE_ATTRIBUTE_DIRECTORY
        else:
            flags |= shellcon.SHGFI_USEFILEATTRIBUTES
            path_to_check = ext if ext else ".txt"
            attrs = win32con.FILE_ATTRIBUTE_NORMAL

        retval, info = shell.SHGetFileInfo(path_to_check, attrs, flags)
        hicon = info[0]

        if not hicon:
            return None

        icon_info = win32gui.GetIconInfo(hicon)
        hbm_color = icon_info[4]
        hbm_mask = icon_info[3]

        img = None
        if hbm_color:
            bmp = win32ui.CreateBitmapFromHandle(hbm_color)
            bmp_info = bmp.GetInfo()
            bmp_bits = bmp.GetBitmapBits(True)
            w, h = bmp_info['bmWidth'], bmp_info['bmHeight']

            if bmp_info['bmBitsPixel'] == 32 and len(bmp_bits) == w * h * 4:
                img = Image.frombuffer('RGBA', (w, h), bmp_bits, 'raw', 'BGRA', 0, 1)
            elif bmp_info['bmBitsPixel'] == 24 and len(bmp_bits) == w * h * 3:
                img = Image.frombuffer('RGB', (w, h), bmp_bits, 'raw', 'BGR', 0, 1)

        win32gui.DestroyIcon(hicon)
        if hbm_color: win32gui.DeleteObject(hbm_color)
        if hbm_mask: win32gui.DeleteObject(hbm_mask)

        if img:
            photo = ImageTk.PhotoImage(img)
            icon_cache[cache_key] = photo
            return photo
    except Exception:
        pass

    icon_cache[cache_key] = None
    return None

# The PDFium C++ library is not thread-safe and will cause segmentation faults if accessed concurrently
pdf_lock = threading.Lock()

def search_pdf(filepath, search_bytes, case_sensitive):
    if not HAS_PDFIUM:
        # print(f"[!] WARNING: PDF search skipped for {filepath}. Run '.\\.env\\Scripts\\pip install pypdfium2'")
        return False
    try:
        with pdf_lock:
            search_str = search_bytes.decode('utf-8')
            if not case_sensitive:
                search_str = search_str.lower()

            pdf = pdfium.PdfDocument(filepath)
            for page in pdf:
                textpage = page.get_textpage()
                text = textpage.get_text_bounded()
                if text:
                    if not case_sensitive:
                        text = text.lower()
                    if search_str in text:
                        return True
    except Exception as e:
        # print(f"[!] Error parsing PDF {filepath}: {e}")
        pass
    return False

FILE_TYPE_EXTS = {
    "Audio": ('.aac', '.ac3', '.aif', '.aifc', '.aiff', '.au', '.cda', '.dts', '.fla', '.flac', '.it', '.m1a', '.m2a', '.m3u', '.m4a', '.m4b', '.m4p', '.mid', '.midi', '.mka', '.mod', '.mp2', '.mp3', '.mpa', '.ogg', '.ra', '.rmi', '.snd', '.spc', '.umx', '.voc', '.wav', '.wma', '.xm'),
    "Compressed": ('.7z', '.ace', '.arj', '.bz2', '.cab', '.gz', '.gzip', '.jar', '.r00', '.r01', '.r02', '.r03', '.r04', '.r05', '.r06', '.r07', '.r08', '.r09', '.r10', '.r11', '.r12', '.r13', '.r14', '.r15', '.r16', '.r17', '.r18', '.r19', '.r20', '.r21', '.r22', '.r23', '.r24', '.r25', '.r26', '.r27', '.r28', '.r29', '.rar', '.tar', '.tgz', '.z', '.zip'),
    "Document": ('.c', '.chm', '.cpp', '.csv', '.cxx', '.doc', '.docm', '.docx', '.dot', '.dotm', '.dotx', '.h', '.hpp', '.htm', '.html', '.hxx', '.ini', '.java', '.lua', '.mht', '.mhtml', '.odt', '.pdf', '.potx', '.potm', '.ppam', '.ppsm', '.ppsx', '.pps', '.ppt', '.pptm', '.pptx', '.rtf', '.sldm', '.sldx', '.thmx', '.txt', '.vsd', '.wpd', '.wps', '.wri', '.xlam', '.xls', '.xlsb', '.xlsm', '.xlsx', '.xltm', '.xltx', '.xml'),
    "Executable": ('.bat', '.cmd', '.exe', '.msi', '.msp', '.scr'),
    "Image": ('.ani', '.bmp', '.gif', '.ico', '.jpe', '.jpeg', '.jpg', '.pcx', '.png', '.psd', '.tga', '.tif', '.tiff', '.webp', '.wmf'),
    "Video": ('.3g2', '.3gp', '.3gp2', '.3gpp', '.amr', '.amv', '.asf', '.avi', '.bdmv', '.bik', '.d2v', '.divx', '.drc', '.dsa', '.dsm', '.dss', '.dsv', '.evo', '.f4v', '.flc', '.fli', '.flic', '.flv', '.hdmov', '.ifo', '.ivf', '.m1v', '.m2p', '.m2t', '.m2ts', '.m2v', '.m4v', '.mkv', '.mp2v', '.mp4', '.mp4v', '.mpe', '.mpeg', '.mpg', '.mpls', '.mpv2', '.mpv4', '.mov', '.mts', '.ogm', '.ogv', '.pss', '.pva', '.qt', '.ram', '.ratdvd', '.rm', '.rmm', '.rmvb', '.roq', '.rpm', '.smil', '.smk', '.swf', '.tp', '.tpr', '.ts', '.vob', '.vp6', '.webm', '.wm', '.wmp', '.wmv')
}

# # Offload the application to the last 2 CPU cores (Commented out to maximize thread pool speed)
# num_cores = os.cpu_count()
# if num_cores and num_cores >= 2:
#     affinity_mask = (1 << (num_cores - 1)) | (1 << (num_cores - 2))
#
#     # Configure ctypes signatures for 64-bit safety
#     ctypes.windll.kernel32.GetCurrentProcess.restype = ctypes.c_void_p
#     ctypes.windll.kernel32.SetProcessAffinityMask.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
#     ctypes.windll.kernel32.SetProcessAffinityMask.restype = ctypes.c_int
#
#     success = ctypes.windll.kernel32.SetProcessAffinityMask(ctypes.windll.kernel32.GetCurrentProcess(), affinity_mask)
#     if success:
#         print(f"[*] Process successfully bound to CPU cores {num_cores-2} and {num_cores-1}")
#     else:
#         print("[!] Failed to set CPU affinity.")

def get_resource_path(relative_path):
    """ Get absolute path to resource, works for dev and for PyInstaller """
    try:
        # PyInstaller creates a temp folder and stores path in _MEIPASS
        base_path = sys._MEIPASS
        # When bundled by PyInstaller, the DLL is flattened into the root of _MEIPASS
        if "fast_search.dll" in relative_path:
            return os.path.join(base_path, "fast_search.dll")
        if "icon.ico" in relative_path:
            return os.path.join(base_path, "icon.ico")
    except Exception:
        # If not running as a PyInstaller app, use the normal current directory
        base_path = os.path.abspath(".")
    return os.path.join(base_path, relative_path)

def is_admin():
    try:
        return ctypes.windll.shell32.IsUserAnAdmin() != 0
    except Exception:
        return False

# 1. Load your newly created DLL
dll_path = get_resource_path(os.path.join("..", "Engine", "fast_search.dll"))
try:
    # Use CDLL for standard C calling convention (cdecl)
    my_dll = ctypes.CDLL(dll_path)
except OSError as e:
    print(f"Failed to load DLL. Ensure it exists here: {dll_path}\nError: {e}")
    try:
        import tkinter as tk
        from tkinter import messagebox
        root_err = tk.Tk()
        root_err.withdraw()
        messagebox.showerror(
            "DLL Load Error",
            f"Failed to load the search engine DLL.\n\n"
            f"Ensure it exists at:\n{dll_path}\n\n"
            f"Error: {e}"
        )
    except Exception:
        pass
    exit(1)

# 2. Define the C signatures for safety
# Our C function takes a const char* and returns a uint64_t
my_dll.GetVolumeUSNJournalID.argtypes = [ctypes.c_char_p]
my_dll.GetVolumeUSNJournalID.restype = ctypes.c_uint64

my_dll.CountFilesInDrive.argtypes = [ctypes.c_char_p]
my_dll.CountFilesInDrive.restype = ctypes.c_uint64

my_dll.SearchByExtension.argtypes = [ctypes.c_char_p, ctypes.c_wchar_p]
my_dll.SearchByExtension.restype = ctypes.c_uint64

my_dll.FastGrepFile.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_void_p]
my_dll.FastGrepFile.restype = ctypes.c_int

my_dll.FastGrepArchive.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_void_p]
my_dll.FastGrepArchive.restype = ctypes.c_int

# Define the Callback type (takes file ID, parent ID, and string)
CALLBACK_TYPE = ctypes.CFUNCTYPE(None, ctypes.c_uint64, ctypes.c_uint64, ctypes.c_wchar_p)

my_dll.ScanDriveWithCallback.argtypes = [ctypes.c_char_p, CALLBACK_TYPE]
my_dll.ScanDriveWithCallback.restype = ctypes.c_uint64

# 3. Build the Graphical User Interface (GUI)
import time
import datetime
import tkinter as tk
from tkinter import ttk
from tkinter import filedialog
import string

cancel_event = threading.Event()
ui_queue = queue.Queue()

current_results = []

def insert_result(data, use_tree):
    file_name, size_str, created_str, full_path, match_count, is_folder, ext = data
    tags = ("even",) if match_count % 2 == 0 else ()
    kwargs = {}
    if HAS_PYWIN32_PILLOW:
        photo = get_icon(ext, is_folder)
        if photo:
            kwargs['image'] = photo

    if use_tree:
        parts = full_path.split('\\')

        # Ensure root drive exists
        drive_iid = parts[0] + "\\"
        if not tree.exists(drive_iid):
            folder_kwargs = {}
            if HAS_PYWIN32_PILLOW:
                folder_photo = get_icon("", True)
                if folder_photo:
                    folder_kwargs['image'] = folder_photo
            tree.insert('', 'end', iid=drive_iid, text=f" {drive_iid}", values=("", "", drive_iid), open=True, **folder_kwargs)

        parent_iid = drive_iid
        for i in range(1, len(parts) - 1):
            current_iid = "\\".join(parts[:i+1])
            if not tree.exists(current_iid):
                folder_kwargs = {}
                if HAS_PYWIN32_PILLOW:
                    folder_photo = get_icon("", True)
                    if folder_photo:
                        folder_kwargs['image'] = folder_photo
                tree.insert(parent_iid, 'end', iid=current_iid, text=f" {parts[i]}", values=("", "", current_iid), open=True, **folder_kwargs)
            parent_iid = current_iid

        file_iid = full_path
        if not tree.exists(file_iid):
            tree.insert(parent_iid, 'end', iid=file_iid, text=f" {file_name}", values=(size_str, created_str, full_path), tags=tags, **kwargs)
        else:
            tree.item(file_iid, text=f" {file_name}", values=(size_str, created_str, full_path), tags=tags, **kwargs)
    else:
        file_iid = full_path
        if not tree.exists(file_iid):
            tree.insert('', 'end', iid=file_iid, text=f" {file_name}", values=(size_str, created_str, full_path), tags=tags, **kwargs)
        else:
            tree.item(file_iid, text=f" {file_name}", values=(size_str, created_str, full_path), tags=tags, **kwargs)

def start_search():
    if getattr(start_search, "is_running", False): return
    start_search.is_running = True
    cancel_event.clear()
    btn_search.config(text="Cancel", command=cancel_search)

    lbl_status.config(text="Scanning Master File Table... Please wait.", foreground="#00BFFF")

    name_query = entry_name_1.get().lower().split() + entry_name_2.get().lower().split()
    not_name_query = entry_not_name.get().lower().split()

    is_case_sensitive = var_case_sensitive.get()
    selected_type = combo_type.get()
    content_1_str = entry_content_1.get()
    content_2_str = entry_content_2.get()
    not_content_str = entry_not_content.get()
    filter_folder = selected_folder.get().lower()

    # Enforce at least one search criterion to prevent scanning the entire drive without filters
    if (not name_query and not not_name_query and 
        not content_1_str and not content_2_str and not not_content_str and 
        not filter_folder):
        from tkinter import messagebox
        messagebox.showwarning(
            "No Search Criteria Specified",
            "Please enter at least one search parameter (e.g. File Name, Content, or Location) before starting a search."
        )
        start_search.is_running = False
        btn_search.config(text="Search!", command=start_search)
        if is_admin():
            lbl_status.config(text="Ready. (Administrator Mode)", foreground="#55FF55")
        else:
            lbl_status.config(text="WARNING: Not running as Administrator! Search will be incomplete.", foreground="#FF5555")
        return

    if not is_case_sensitive:
        content_1_str = content_1_str.lower()
        content_2_str = content_2_str.lower()
        not_content_str = not_content_str.lower()

    content_1 = content_1_str.encode('utf-8')
    content_2 = content_2_str.encode('utf-8')
    not_content = not_content_str.encode('utf-8')

    if filter_folder and not filter_folder.endswith('\\'):
        filter_folder += '\\'

    selected_drive = combo_drive.get()
    if not selected_drive:
        start_search.is_running = False
        btn_search.config(text="Search!", command=start_search)
        return

    time_filter = combo_time.get()
    custom_days = 0
    if time_filter == "Custom":
        try:
            custom_days = int(var_custom_days.get())
        except ValueError:
            custom_days = 0

    drives_to_scan = []
    if selected_drive == "All":
        drives_to_scan = [d for d in available_drives if d != "All"]
    else:
        drives_to_scan = [selected_drive]

    # Clear old results
    for item in tree.get_children():
        tree.delete(item)

    global current_results
    current_results.clear()

    print("\n" + "="*50 + "\n--- Starting New Search ---\n" + "="*50)

    def poll_queue():
        try:
            for _ in range(50):  # Process up to 50 items per tick to prevent UI thread starvation
                msg_type, data = ui_queue.get_nowait()
                if msg_type == "match":
                    current_results.append(data)
                    insert_result(data, var_tree_view.get())
                elif msg_type == "status":
                    lbl_status.config(text=data[0], foreground=data[1])
                elif msg_type == "done":
                    start_search.is_running = False
                    btn_search.config(text="Search!", command=start_search)
                    if cancel_event.is_set():
                        lbl_status.config(text="Search Canceled.", foreground="#FFA500")
                    return
        except queue.Empty:
            pass
        root.after(50, poll_queue)

    poll_queue()
    threading.Thread(target=search_worker, args=(drives_to_scan, name_query, not_name_query, is_case_sensitive, selected_type, content_1, content_2, not_content, filter_folder, cancel_event, time_filter, custom_days), daemon=True).start()

def cancel_search():
    if not getattr(start_search, "is_running", False): return
    print("\n--- Canceling Search ---")
    ui_queue.put(("status", ("Canceling...", "#FFA500")))
    cancel_event.set()

def search_worker(drives_to_scan, name_query, not_name_query, is_case_sensitive, selected_type, content_1, content_2, not_content, filter_folder, cancel_event, time_filter, custom_days):
    match_count = 0
    match_lock = threading.Lock()
    start_time = time.time()

    hour_start = start_time - 3600
    today_start = datetime.datetime.now().replace(hour=0, minute=0, second=0, microsecond=0).timestamp()
    yesterday_start = today_start - 86400
    last_week_start = today_start - 7 * 86400
    last_month_start = today_start - 30 * 86400
    custom_start = today_start - custom_days * 86400

    for d in drives_to_scan:
        if cancel_event.is_set(): break

        drive = d.encode('utf-8')
        print(f"\n[*] Querying Master File Table on Drive {d}:\\ ...")
        ui_queue.put(("status", (f"Querying MFT on Drive {d}:\\ ...", "#00BFFF")))

        # Safety check for Admin privileges
        if my_dll.GetVolumeUSNJournalID(drive) == 0:
            if selected_drive != "All":
                ui_queue.put(("status", (f"ERROR: Cannot access drive {d}! (Needs Admin, or not NTFS)", "#FF5555")))
                ui_queue.put(("done", None))
                return
            continue

        found_files = []
        mft_table = {}

        # Stream files from C to Python
        def custom_search_filter(file_id, parent_id, file_name):
            mft_table[file_id] = (parent_id, file_name)
            name_lower = file_name.lower()

            # Pre-filter by name here so we don't Grep the entire hard drive
            # all() ensures every word typed in the box is found somewhere in the filename
            if all(part in name_lower for part in name_query):
                if not_name_query and any(part in name_lower for part in not_name_query):
                    return
                found_files.append(file_id)

        c_callback = CALLBACK_TYPE(custom_search_filter)

        my_dll.ScanDriveWithCallback(drive, c_callback)

        print(f"[*] MFT Scan on {d}:\\ complete. Inspecting {len(found_files)} potential name matches...")
        ui_queue.put(("status", (f"Checking contents of {len(found_files)} files on {d}:\\ ...", "#00BFFF")))

        def get_full_path(file_id):
            path_parts = []
            current_id = file_id
            while current_id in mft_table:
                parent_id, name = mft_table[current_id]
                path_parts.append(name)
                if parent_id == current_id or parent_id == 0: break
                current_id = parent_id
            path_parts.reverse()
            if path_parts and path_parts[0] in (".", ""): path_parts = path_parts[1:]
            return drive.decode() + ":\\" + "\\".join(path_parts)

        def process_file(file_id):
            nonlocal match_count
            if cancel_event.is_set(): return

            full_path = get_full_path(file_id)

            # Filter by location before performing heavy content checks
            if filter_folder and not full_path.lower().startswith(filter_folder):
                return

            if selected_type == "Folder":
                if not os.path.isdir(full_path):
                    return
            elif selected_type != "Everything":
                exts = FILE_TYPE_EXTS.get(selected_type, ())
                if not full_path.lower().endswith(exts):
                    return

            created_str = ""
            try:
                ctime = os.path.getctime(full_path)
                created_str = datetime.datetime.fromtimestamp(ctime).strftime('%Y-%m-%d %H:%M')
                if time_filter != "None":
                    if time_filter == "Hour" and ctime < hour_start:
                        return
                    elif time_filter == "Today" and ctime < today_start:
                        return
                    elif time_filter == "Yesterday" and not (yesterday_start <= ctime < today_start):
                        return
                    elif time_filter == "Last Week" and ctime < last_week_start:
                        return
                    elif time_filter == "Last Month" and ctime < last_month_start:
                        return
                    elif time_filter == "Custom" and ctime < custom_start:
                        return
            except Exception:
                if time_filter != "None":
                    return
                created_str = "Unknown"

            # Highly intensive print disabled to prevent PyInstaller --windowed buffer crashes
            # print(f"[>] Checking: {full_path}")

            try:
                # Pass the file path to the appropriate C-engine scanner
                search_archives = selected_type in ("Everything", "Document", "Compressed")
                is_archive = search_archives and full_path.lower().endswith(('.zip', '.7z', '.rar', '.docx', '.pptx', '.xlsx'))
                is_pdf = full_path.lower().endswith('.pdf')

                # Pass a null pointer for Python for now since Python uses an internal threading model
                null_ptr = ctypes.c_void_p(0)

                if content_1:
                    if is_pdf:
                        if not search_pdf(full_path, content_1, is_case_sensitive): return
                    elif is_archive:
                        if not my_dll.FastGrepArchive(full_path.encode('utf-8'), content_1, is_case_sensitive, null_ptr): return
                    else:
                        if not my_dll.FastGrepFile(full_path.encode('utf-8'), content_1, is_case_sensitive, null_ptr): return

                if content_2:
                    if is_pdf:
                        if not search_pdf(full_path, content_2, is_case_sensitive): return
                    elif is_archive:
                        if not my_dll.FastGrepArchive(full_path.encode('utf-8'), content_2, is_case_sensitive, null_ptr): return
                    else:
                        if not my_dll.FastGrepFile(full_path.encode('utf-8'), content_2, is_case_sensitive, null_ptr): return

                if not_content:
                    if is_pdf:
                        if search_pdf(full_path, not_content, is_case_sensitive): return
                    elif is_archive:
                        if my_dll.FastGrepArchive(full_path.encode('utf-8'), not_content, is_case_sensitive, null_ptr): return
                    else:
                        if my_dll.FastGrepFile(full_path.encode('utf-8'), not_content, is_case_sensitive, null_ptr): return

                file_name = mft_table[file_id][1]
                is_folder = os.path.isdir(full_path)
                ext = ""

                if is_folder:
                    size_str = "Folder"
                else:
                    file_size = os.path.getsize(full_path)
                    if file_size < 1024:
                        size_str = f"{file_size} B"
                    elif file_size < 1024 * 1024:
                        size_str = f"{file_size / 1024:.1f} KB"
                    else:
                        size_str = f"{file_size / (1024 * 1024):.1f} MB"
                    ext = os.path.splitext(full_path)[1].lower()

                # print(f"  ---> MATCH FOUND: {full_path}")
                with match_lock:
                    match_count += 1
                    current_match = match_count
                ui_queue.put(("match", (file_name, size_str, created_str, full_path, current_match, is_folder, ext)))
            except Exception as e:
                # print(f"  ---> ERROR reading {full_path}: {e}")
                pass

        # Execute file checks concurrently in small chunks to support responsive cancellation
        chunk_size = 100
        with concurrent.futures.ThreadPoolExecutor() as executor:
            for i in range(0, len(found_files), chunk_size):
                if cancel_event.is_set():
                    break
                chunk = found_files[i:i+chunk_size]
                list(executor.map(process_file, chunk))

        if cancel_event.is_set(): break

    if not cancel_event.is_set():
        elapsed = time.time() - start_time
        print(f"\n--- Search Complete: {match_count} matches in {elapsed:.2f} seconds ---")
        ui_queue.put(("status", (f"Search Complete: Found {match_count} matches in {elapsed:.2f} seconds!", "#55FF55")))
    ui_queue.put(("done", None))

# Setup Main Window
root = tk.Tk()
root.title("MBR-Deep-Classic")
root.geometry("1200x550")

try:
    root.iconbitmap(get_resource_path(os.path.join("..", "..", "icon.ico")))
except Exception:
    pass

# Apply Windows 10/11 Dark Mode to the title bar
if os.name == 'nt':
    try:
        root.update() # Required to generate the window handle
        HWND = ctypes.windll.user32.GetParent(root.winfo_id())
        dark_mode = ctypes.c_int(1)
        # 20 is DWMWA_USE_IMMERSIVE_DARK_MODE
        ctypes.windll.dwmapi.DwmSetWindowAttribute(HWND, 20, ctypes.byref(dark_mode), ctypes.sizeof(dark_mode))
    except Exception as e:
        print(f"[!] Failed to set dark title bar: {e}")

class CustomContextMenu(tk.Toplevel):
    def __init__(self, parent):
        super().__init__(parent)
        self.overrideredirect(True)
        self.config(bg="#4a4a4a") # 1px Border color
        self.attributes("-topmost", True)
        self.withdraw()

        self.inner_frame = tk.Frame(self, bg="#2b2b2b")
        self.inner_frame.pack(fill="both", expand=True, padx=1, pady=1)

        self.bind("<FocusOut>", lambda e: self.withdraw())

    def add_command(self, label, command):
        btn = tk.Label(self.inner_frame, text=label, bg="#2b2b2b", fg="#ffffff", anchor="w", padx=15, pady=4, font=("Segoe UI", 9))
        btn.pack(fill="x")

        btn.bind("<Enter>", lambda e, b=btn: b.config(bg="#4a4a4a"))
        btn.bind("<Leave>", lambda e, b=btn: b.config(bg="#2b2b2b"))
        btn.bind("<ButtonRelease-1>", lambda e, c=command: self._execute(c))

    def add_separator(self):
        tk.Frame(self.inner_frame, bg="#4a4a4a", height=1).pack(fill="x", padx=5, pady=2)

    def _execute(self, command):
        self.withdraw()
        command()

    def tk_popup(self, x, y):
        self.geometry(f"+{x}+{y}")
        self.deiconify()
        self.focus_set()

def add_entry_context_menu(widget):
    menu = CustomContextMenu(widget)
    menu.add_command(label="Cut", command=lambda: (widget.focus_set(), widget.event_generate("<<Cut>>")))
    menu.add_command(label="Copy", command=lambda: (widget.focus_set(), widget.event_generate("<<Copy>>")))
    menu.add_command(label="Paste", command=lambda: (widget.focus_set(), widget.event_generate("<<Paste>>")))
    menu.add_separator()
    menu.add_command(label="Select All", command=lambda: (widget.focus_set(), widget.select_range(0, tk.END)))

    def show_menu(event):
        menu.tk_popup(event.x_root, event.y_root)

    widget.bind("<Button-3>", show_menu)

selected_folder = tk.StringVar()

def browse_folder():
    folder = filedialog.askdirectory(title="Select Folder to Search")
    if folder:
        folder = folder.replace('/', '\\')
        drive_letter = folder[0].upper()
        if drive_letter in available_drives:
            combo_drive.set(drive_letter)
        selected_folder.set(folder)

def clear_folder():
    selected_folder.set("")

# Enable native system theming
try:
    import sv_ttk
    sv_ttk.set_theme("dark") # Instantly applies modern Windows Dark Mode
    USING_SV_TTK = True
except ImportError:
    USING_SV_TTK = False
    style = ttk.Style(root)
    if "vista" in style.theme_names():
        style.theme_use("vista")

# Create a custom style for a Dark Theme Scrollbar (bypassing native Windows styling)
if not USING_SV_TTK:
    style = ttk.Style(root)
    if "clam" in style.theme_names():
        try:
            style.element_create("Dark.Vertical.Scrollbar.trough", "from", "clam")
            style.element_create("Dark.Vertical.Scrollbar.thumb", "from", "clam")
            style.element_create("Dark.Vertical.Scrollbar.uparrow", "from", "clam")
            style.element_create("Dark.Vertical.Scrollbar.downarrow", "from", "clam")

            style.layout("Dark.Vertical.TScrollbar", [
                ('Dark.Vertical.Scrollbar.trough', {'children': [
                    ('Dark.Vertical.Scrollbar.uparrow', {'side': 'top', 'sticky': ''}),
                    ('Dark.Vertical.Scrollbar.downarrow', {'side': 'bottom', 'sticky': ''}),
                    ('Dark.Vertical.Scrollbar.thumb', {'unit': '1', 'sticky': 'nswe'})
                ], 'sticky': 'nswe'})
            ])

            style.configure("Dark.Vertical.TScrollbar",
                            background="#4a4a4a",
                            darkcolor="#4a4a4a",
                            lightcolor="#4a4a4a",
                            troughcolor="#2b2b2b",
                            bordercolor="#2b2b2b",
                            arrowcolor="#ffffff")
            style.map("Dark.Vertical.TScrollbar", background=[("active", "#5a5a5a")])
        except Exception:
            pass

frame_controls = ttk.Frame(root, padding=10)
frame_controls.pack(fill="x")

available_drives = ["All"] + [f"{d}" for d in string.ascii_uppercase if os.path.exists(f"{d}:\\")]

ttk.Label(frame_controls, text="Drive:").grid(row=0, column=0, padx=5)
combo_drive = ttk.Combobox(frame_controls, values=available_drives, width=4, state="readonly")
combo_drive.grid(row=0, column=1, padx=5)
if available_drives:
    combo_drive.set("All")

var_case_sensitive = tk.BooleanVar(value=False)
ttk.Checkbutton(frame_controls, text="Case Sensitive", variable=var_case_sensitive).grid(row=1, column=0, padx=5, sticky="w")

type_options = ["Everything", "Audio", "Document", "Executable", "Folder", "Image", "Video", "Compressed"]
combo_type = ttk.Combobox(frame_controls, values=type_options, width=12, state="readonly")
combo_type.grid(row=1, column=1, padx=5, sticky="w")
combo_type.set("Everything")

ttk.Label(frame_controls, text="File Name Contains:").grid(row=0, column=2, padx=5, sticky="e")
entry_name_1 = ttk.Entry(frame_controls)
entry_name_1.grid(row=0, column=3, padx=5, sticky="ew")
add_entry_context_menu(entry_name_1)

ttk.Label(frame_controls, text="And Name Contains:").grid(row=1, column=2, padx=5, sticky="e", pady=5)
entry_name_2 = ttk.Entry(frame_controls)
entry_name_2.grid(row=1, column=3, padx=5, sticky="ew", pady=5)
add_entry_context_menu(entry_name_2)

ttk.Label(frame_controls, text="File Content Contains:").grid(row=0, column=4, padx=5, sticky="e")
entry_content_1 = ttk.Entry(frame_controls)
entry_content_1.grid(row=0, column=5, padx=5, sticky="ew")
add_entry_context_menu(entry_content_1)

ttk.Label(frame_controls, text="And Content Contains:").grid(row=1, column=4, padx=5, sticky="e", pady=5)
entry_content_2 = ttk.Entry(frame_controls)
entry_content_2.grid(row=1, column=5, padx=5, sticky="ew", pady=5)
add_entry_context_menu(entry_content_2)

ttk.Label(frame_controls, text="Created:").grid(row=2, column=0, padx=5, sticky="w")
frame_time = ttk.Frame(frame_controls)
frame_time.grid(row=2, column=1, sticky="ew", pady=5)

time_options = ["None", "Hour", "Today", "Yesterday", "Last Week", "Last Month", "Custom"]
combo_time = ttk.Combobox(frame_time, values=time_options, width=10, state="readonly")
combo_time.pack(side="left", fill="x", expand=True, padx=5)
combo_time.set("None")

var_custom_days = tk.StringVar()
entry_custom_days = ttk.Entry(frame_time, textvariable=var_custom_days, width=4)

def on_time_select(event):
    if combo_time.get() == "Custom":
        entry_custom_days.pack(side="left", padx=(0, 5))
        if not var_custom_days.get():
            var_custom_days.set("7")
    else:
        entry_custom_days.pack_forget()

combo_time.bind("<<ComboboxSelected>>", on_time_select)

style = ttk.Style()
style.configure("Red.TLabel", foreground="#FF5555")

frame_not_name = ttk.Frame(frame_controls)
frame_not_name.grid(row=2, column=2, padx=5, sticky="e", pady=5)
ttk.Label(frame_not_name, text="Not", style="Red.TLabel").pack(side="left")
ttk.Label(frame_not_name, text=" Contain Name:").pack(side="left")

entry_not_name = ttk.Entry(frame_controls)
entry_not_name.grid(row=2, column=3, padx=5, sticky="ew", pady=5)
add_entry_context_menu(entry_not_name)

frame_not_content = ttk.Frame(frame_controls)
frame_not_content.grid(row=2, column=4, padx=5, sticky="e", pady=5)
ttk.Label(frame_not_content, text="Not", style="Red.TLabel").pack(side="left")
ttk.Label(frame_not_content, text=" Content Contains:").pack(side="left")

entry_not_content = ttk.Entry(frame_controls)
entry_not_content.grid(row=2, column=5, padx=5, sticky="ew", pady=5)
add_entry_context_menu(entry_not_content)

ttk.Label(frame_controls, text="In Location:").grid(row=3, column=0, padx=5, sticky="w")
frame_loc = ttk.Frame(frame_controls)
frame_loc.grid(row=3, column=1, columnspan=4, sticky="ew", pady=5)

entry_loc = ttk.Entry(frame_loc, textvariable=selected_folder)
entry_loc.pack(side="left", fill="x", expand=True, padx=5)
add_entry_context_menu(entry_loc)

def update_drive_from_folder(*args):
    path = selected_folder.get()
    if len(path) >= 2 and path[1] == ':':
        drive = path[0].upper()
        if drive in available_drives:
            combo_drive.set(drive)

selected_folder.trace_add("write", update_drive_from_folder)

btn_browse = ttk.Button(frame_loc, text="Select...", command=browse_folder)
btn_browse.pack(side="left")

btn_clear = ttk.Button(frame_loc, text="Clear", command=clear_folder)
btn_clear.pack(side="left", padx=5)

btn_search = ttk.Button(frame_controls, text="Search!", command=start_search)
btn_search.grid(row=3, column=5, padx=5, sticky="ew", pady=(0, 5))

frame_controls.columnconfigure(3, weight=1)
frame_controls.columnconfigure(5, weight=1)

# Setup Results Table
frame_results = ttk.Frame(root)

def sortby(tree, col, descending):
    if getattr(sortby, "is_sorting", False):
        return
    sortby.is_sorting = True

    lbl_status.config(text="Sorting results... Please wait.", foreground="#00BFFF")

    # Grab values to sort
    if col == "#0":
        data = [(tree.item(child, 'text'), child) for child in tree.get_children('')]
    else:
        data = [(tree.set(child, col), child) for child in tree.get_children('')]

    # If the data is file sizes, sort mathematically instead of alphabetically
    if col == "Size":
        def parse_size(size_str):
            if size_str == "Folder": return -1
            parts = size_str.split()
            if len(parts) != 2: return 0
            mult = {"B": 1, "KB": 1024, "MB": 1024**2, "GB": 1024**3}.get(parts[1], 1)
            return float(parts[0]) * mult
        data.sort(key=lambda x: parse_size(x[0]), reverse=descending)
    else:
        data.sort(key=lambda x: x[0].lower(), reverse=descending)

    def process_sort_batch(index):
        if index >= len(data):
            if is_admin():
                lbl_status.config(text="Sorting complete.", foreground="#55FF55")
            else:
                lbl_status.config(text="Sorting complete. (WARNING: Not running as Administrator!)", foreground="#FF5555")
            # Switch the heading so the next click will sort in the opposite direction
            tree.heading(col, command=lambda col=col: sortby(tree, col, int(not descending)))
            sortby.is_sorting = False
            return

        # Process in batches of 100 to avoid UI freezing
        end_idx = min(index + 100, len(data))
        for ix in range(index, end_idx):
            item = data[ix]
            tree.move(item[1], '', ix)
            # Re-apply the zebra-striping tag so rows stay nicely alternated
            tags = ("even",) if ix % 2 == 0 else ()
            tree.item(item[1], tags=tags)

        # Schedule the next batch to yield to Tkinter's event loop
        root.after(5, lambda: process_sort_batch(end_idx))

    process_sort_batch(0)

columns = ("Size", "Created", "Location")
tree = ttk.Treeview(frame_results, columns=columns, show="tree headings")

style = ttk.Style()
style.configure("Treeview", font=("Segoe UI", 10), rowheight=25)

tree.heading("#0", text="File", command=lambda c="#0": sortby(tree, c, 0))
tree.column("#0", width=200)

for col in columns:
    tree.heading(col, text=col, command=lambda c=col: sortby(tree, c, 0))
tree.column("Size", width=80, anchor="center") # anchor="center" center-aligns the size numbers
tree.column("Created", width=140, anchor="center")
tree.column("Location", width=500)

if USING_SV_TTK:
    scrollbar = ttk.Scrollbar(frame_results, orient="vertical", command=tree.yview)
else:
    scrollbar = ttk.Scrollbar(frame_results, orient="vertical", command=tree.yview, style="Dark.Vertical.TScrollbar")
tree.configure(yscrollcommand=scrollbar.set)
scrollbar.pack(side="right", fill="y")
tree.pack(side="left", fill="both", expand=True)

tree.tag_configure("even", background="#2a2a2a")

frame_bottom = ttk.Frame(root)
frame_bottom.pack(side="bottom", fill="x", padx=10, pady=5)
frame_results.pack(fill="both", expand=True, padx=10, pady=5)

var_tree_view = tk.BooleanVar(value=False)

def toggle_view():
    for item in tree.get_children():
        tree.delete(item)
    use_tree = var_tree_view.get()
    if use_tree:
        tree.heading("#0", text="Folder / File")
    else:
        tree.heading("#0", text="File")

    for data in current_results:
        insert_result(data, use_tree)

chk_view = ttk.Checkbutton(frame_bottom, text="View", variable=var_tree_view, command=toggle_view)
chk_view.pack(side="left")

if is_admin():
    lbl_status = ttk.Label(frame_bottom, text="Ready. (Administrator Mode)", foreground="#55FF55", anchor="center")
else:
    lbl_status = ttk.Label(frame_bottom, text="WARNING: Not running as Administrator! Search will be incomplete.", foreground="#FF5555", anchor="center")
lbl_status.pack(side="left", fill="x", expand=True, padx=(0, 50)) # Added right padding to perfectly center the text in the window

# Context Menu Actions
def get_selected_path():
    selected = tree.selection()
    if selected:
        return tree.item(selected[0])['values'][2] # Location is in column index 2
    return None

def open_default(*args):
    path = get_selected_path()
    if path: os.startfile(path)

def open_with(*args):
    path = get_selected_path()
    # rundll32.exe's OpenAs_RunDLL requires the path to be passed WITHOUT quotes,
    # even if there are spaces in the file path, or it will silently fail!
    if path: subprocess.Popen(f'rundll32.exe shell32.dll,OpenAs_RunDLL {path}')

def show_in_explorer(*args):
    path = get_selected_path()
    if path: subprocess.Popen(f'explorer /select,"{path}"')

# Build the Right-Click Menu
context_menu = CustomContextMenu(root)
context_menu.add_command(label="Open", command=open_default)
context_menu.add_command(label="Open With...", command=open_with)
context_menu.add_separator()
context_menu.add_command(label="Show in Explorer", command=show_in_explorer)

def show_context_menu(event):
    row = tree.identify_row(event.y)
    if row:
        tree.selection_set(row)
        context_menu.tk_popup(event.x_root, event.y_root)

tree.bind("<Button-3>", show_context_menu) # Right-click
tree.bind("<Double-1>", open_default)      # Double-click

def show_admin_warning():
    from tkinter import messagebox
    messagebox.showwarning(
        "Administrator Rights Required",
        "This application requires Administrator privileges to directly scan the Master File Table (MFT) on NTFS drives.\n\n"
        "Please restart the application as Administrator, otherwise search results will be empty or incomplete."
    )

if not is_admin():
    root.after(100, show_admin_warning)

root.mainloop()
