#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
#include <wchar.h>
#include <string.h>
#include <ctype.h>
#include <archive.h>
#include <archive_entry.h>

#ifdef __cplusplus
extern "C" {
#endif

// The __declspec(dllexport) keyword makes this function visible outside the DLL.
__declspec(dllexport) uint64_t GetVolumeUSNJournalID(const char* driveLetter) {
    // Construct the volume path, e.g., "\\.\C:"
    char volumePath[10] = "\\\\.\\X:";
    volumePath[4] = driveLetter[0];

    // Ask the kernel for direct volume access
    HANDLE hVol = CreateFileA(
        volumePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        0,
        NULL
    );

    if (hVol == INVALID_HANDLE_VALUE) {
        return 0; // Return 0 on failure (e.g., no Administrator privileges)
    }

    USN_JOURNAL_DATA_V0 journalData;
    DWORD bytesReturned;

    // Ask the kernel for the USN Journal data
    BOOL success = DeviceIoControl(
        hVol,
        FSCTL_QUERY_USN_JOURNAL,
        NULL,
        0,
        &journalData,
        sizeof(journalData),
        &bytesReturned,
        NULL
    );

    CloseHandle(hVol);

    if (success) {
        return journalData.UsnJournalID; // Return the 64-bit integer ID
    }

    return 0;
}

// The next step: Enumerate the MFT to quickly count all files on the drive!
__declspec(dllexport) uint64_t CountFilesInDrive(const char* driveLetter) {
    char volumePath[10] = "\\\\.\\X:";
    volumePath[4] = driveLetter[0];

    HANDLE hVol = CreateFileA(
        volumePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        0,
        NULL
    );

    if (hVol == INVALID_HANDLE_VALUE) {
        return 0;
    }

    // We first need the HighUsn (the latest journal sequence number) from the journal
    USN_JOURNAL_DATA_V0 journalData;
    DWORD bytesReturned;
    if (!DeviceIoControl(hVol, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &journalData, sizeof(journalData), &bytesReturned, NULL)) {
        CloseHandle(hVol);
        return 0;
    }

    // Setup the enumeration parameters
    MFT_ENUM_DATA_V0 enumData;
    enumData.StartFileReferenceNumber = 0; // Start at the very beginning of the drive
    enumData.LowUsn = 0;
    enumData.HighUsn = journalData.NextUsn; // Up to the current latest change

    // Allocate a large 1MB buffer to receive thousands of records per kernel call
    #define BUF_LEN (1024 * 1024)
    char* buffer = (char*)malloc(BUF_LEN);
    uint64_t fileCount = 0;

    // Keep asking the kernel for the next chunk of files until it says there are no more
    while (DeviceIoControl(hVol, FSCTL_ENUM_USN_DATA, &enumData, sizeof(enumData), buffer, BUF_LEN, &bytesReturned, NULL)) {
        // The output buffer format starts with an 8-byte reference number for the NEXT call
        DWORDLONG nextID = *((DWORDLONG*)buffer);
        DWORD offset = sizeof(DWORDLONG);

        // Loop through all USN_RECORDs returned in this chunk
        while (offset < bytesReturned) {
            USN_RECORD* record = (USN_RECORD*)(buffer + offset);

            // You could extract record->FileName here in the future
            fileCount++;

            // Move to the next record in the buffer
            offset += record->RecordLength;
        }

        // Update the starting ID for the next DeviceIoControl kernel call
        enumData.StartFileReferenceNumber = nextID;
    }

    free(buffer);
    CloseHandle(hVol);
    return fileCount;
}

// New Function: Search the MFT for a specific file extension
__declspec(dllexport) uint64_t SearchByExtension(const char* driveLetter, const wchar_t* ext) {
    char volumePath[10] = "\\\\.\\X:";
    volumePath[4] = driveLetter[0];

    HANDLE hVol = CreateFileA(
        volumePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        0,
        NULL
    );

    if (hVol == INVALID_HANDLE_VALUE) return 0;

    USN_JOURNAL_DATA_V0 journalData;
    DWORD bytesReturned;
    if (!DeviceIoControl(hVol, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &journalData, sizeof(journalData), &bytesReturned, NULL)) {
        CloseHandle(hVol);
        return 0;
    }

    MFT_ENUM_DATA_V0 enumData;
    enumData.StartFileReferenceNumber = 0;
    enumData.LowUsn = 0;
    enumData.HighUsn = journalData.NextUsn;

    #define BUF_LEN (1024 * 1024)
    char* buffer = (char*)malloc(BUF_LEN);
    uint64_t matchCount = 0;
    size_t extLen = wcslen(ext);

    while (DeviceIoControl(hVol, FSCTL_ENUM_USN_DATA, &enumData, sizeof(enumData), buffer, BUF_LEN, &bytesReturned, NULL)) {
        DWORDLONG nextID = *((DWORDLONG*)buffer);
        DWORD offset = sizeof(DWORDLONG);

        while (offset < bytesReturned) {
            USN_RECORD* record = (USN_RECORD*)(buffer + offset);

            // Extract filename using the offsets provided by the kernel
            int nameLen = record->FileNameLength / sizeof(WCHAR);
            WCHAR* namePtr = (WCHAR*)((char*)record + record->FileNameOffset);

            if (nameLen >= extLen) {
                // _wcsnicmp does a case-insensitive comparison
                if (_wcsnicmp(namePtr + nameLen - extLen, ext, extLen) == 0) {
                    if (matchCount < 10) {
                        // %.*ls prints exactly 'nameLen' characters from the non-terminated wide string
                        printf("  -> Found: %.*ls\n", nameLen, namePtr);
                    } else if (matchCount == 10) {
                        printf("  -> (Found more, truncating output to avoid terminal spam...)\n");
                    }
                    matchCount++;
                }
            }

            offset += record->RecordLength;
        }
        enumData.StartFileReferenceNumber = nextID;
    }

    free(buffer);
    CloseHandle(hVol);
    return matchCount;
}

// Define the signature for our C-to-Python/C# callback (Returns 1 to continue, 0 to abort)
typedef int (*FileFoundCallback)(uint64_t fileId, uint64_t parentId, const wchar_t* fileName);

// New Function: Stream every file and its MFT references to Python in real-time
__declspec(dllexport) uint64_t ScanDriveWithCallback(const char* driveLetter, FileFoundCallback callback) {
    char volumePath[10] = "\\\\.\\X:";
    volumePath[4] = driveLetter[0];

    HANDLE hVol = CreateFileA(
        volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL, OPEN_EXISTING, 0, NULL
    );
    if (hVol == INVALID_HANDLE_VALUE) return 0;

    USN_JOURNAL_DATA_V0 journalData;
    DWORD bytesReturned;
    if (!DeviceIoControl(hVol, FSCTL_QUERY_USN_JOURNAL, NULL, 0, &journalData, sizeof(journalData), &bytesReturned, NULL)) {
        CloseHandle(hVol);
        return 0;
    }

    MFT_ENUM_DATA_V0 enumData;
    enumData.StartFileReferenceNumber = 0;
    enumData.LowUsn = 0;
    enumData.HighUsn = journalData.NextUsn;

    char* buffer = (char*)malloc(BUF_LEN);
    uint64_t fileCount = 0;
    WCHAR nameBuffer[32768]; // Max NTFS path size

    while (DeviceIoControl(hVol, FSCTL_ENUM_USN_DATA, &enumData, sizeof(enumData), buffer, BUF_LEN, &bytesReturned, NULL)) {
        DWORDLONG nextID = *((DWORDLONG*)buffer);
        DWORD offset = sizeof(DWORDLONG);

        while (offset < bytesReturned) {
            // Cast to USN_RECORD_V2 to guarantee access to the TimeStamp property
            USN_RECORD_V2* record = (USN_RECORD_V2*)(buffer + offset);

            int nameLen = record->FileNameLength / sizeof(WCHAR);
            WCHAR* namePtr = (WCHAR*)((char*)record + record->FileNameOffset);

            if (nameLen < 32768) {
                memcpy(nameBuffer, namePtr, record->FileNameLength);
                nameBuffer[nameLen] = L'\0'; // Null-terminate for Python

                // Fire the callback, handing the IDs and string to the backend language
                // If the backend returns 0 (false), immediately abort the scan!
                if (callback(record->FileReferenceNumber, record->ParentFileReferenceNumber, nameBuffer) == 0) {
                    free(buffer);
                    CloseHandle(hVol);
                    return fileCount;
                }
                fileCount++;
            }

            offset += record->RecordLength;
        }
        enumData.StartFileReferenceNumber = nextID;
    }

    free(buffer);
    CloseHandle(hVol);
    return fileCount;
}

// New Function: High-speed grep for file contents using Memory-Mapped I/O
__declspec(dllexport) int FastGrepFile(const char* filePath, const char* searchTerm, int caseSensitive) {
    // 1. Open a direct handle to the file
    HANDLE hFile = CreateFileA(
        filePath,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL
    );

    if (hFile == INVALID_HANDLE_VALUE) return 0;

    // 2. Get the exact size of the file
    LARGE_INTEGER fileSize;
    if (!GetFileSizeEx(hFile, &fileSize) || fileSize.QuadPart == 0) {
        CloseHandle(hFile);
        return 0;
    }

    // 3. Ask Windows to map the file into RAM
    HANDLE hMap = CreateFileMappingA(hFile, NULL, PAGE_READONLY, 0, 0, NULL);
    if (!hMap) {
        CloseHandle(hFile);
        return 0;
    }

    const char* fileData = (const char*)MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, 0);
    int found = 0;

    if (fileData) {
        size_t termLen = strlen(searchTerm);
        if (fileSize.QuadPart >= termLen) {
            const char* end = fileData + fileSize.QuadPart - termLen;
            const char* current = fileData;

            // 4. Ultra-fast byte search using standard C memory functions
            if (caseSensitive) {
                while (current <= end) {
                    current = (const char*)memchr(current, searchTerm[0], end - current + 1);
                    if (!current) break; // The first letter wasn't found anywhere in the remaining bytes

                    if (memcmp(current, searchTerm, termLen) == 0) {
                        found = 1; // Match found!
                        break;
                    }
                    current++;
                }
            } else {
                char lowerFirst = searchTerm[0];
                char upperFirst = (char)toupper((unsigned char)lowerFirst);
                while (current <= end) {
                    if (*current == lowerFirst || *current == upperFirst) {
                        int match = 1;
                        for (size_t i = 1; i < termLen; i++) {
                            if (tolower((unsigned char)current[i]) != (unsigned char)searchTerm[i]) {
                                match = 0;
                                break;
                            }
                        }
                        if (match) {
                            found = 1;
                            break;
                        }
                    }
                    current++;
                }
            }
        }
        UnmapViewOfFile(fileData);
    }

    CloseHandle(hMap);
    CloseHandle(hFile);
    return found;
}

// New Function: Search inside ZIP, 7z, RAR, DOCX, PPTX using libarchive
__declspec(dllexport) int FastGrepArchive(const char* archivePath, const char* searchTerm, int caseSensitive) {
    struct archive *a = archive_read_new();

    // Tell libarchive to automatically figure out if it's ZIP, 7z, RAR, etc.
    archive_read_support_filter_all(a);
    archive_read_support_format_all(a);

    if (archive_read_open_filename(a, archivePath, 10240) != ARCHIVE_OK) {
        archive_read_free(a);
        return 0; // Failed to open archive
    }

    struct archive_entry *entry;
    size_t termLen = strlen(searchTerm);
    int found = 0;

    // Loop through every internal file in the archive
    while (archive_read_next_header(a, &entry) == ARCHIVE_OK) {
        const char* entryName = archive_entry_pathname(entry);
        if (entryName) {
            size_t nameLen = strlen(entryName);
            if (nameLen >= termLen) {
                const char* current = entryName;
                const char* end = current + nameLen - termLen;
                if (caseSensitive) {
                    while (current <= end) {
                        current = (const char*)memchr(current, searchTerm[0], end - current + 1);
                        if (!current) break; // First letter not found

                        if (memcmp(current, searchTerm, termLen) == 0) {
                            found = 1; // Match found!
                            break;
                        }
                        current++;
                    }
                } else {
                    char lowerFirst = (char)tolower((unsigned char)searchTerm[0]);
                    char upperFirst = (char)toupper((unsigned char)lowerFirst);
                    while (current <= end) {
                        if (*current == lowerFirst || *current == upperFirst) {
                            int match = 1;
                            for (size_t i = 1; i < termLen; i++) {
                                if (tolower((unsigned char)current[i]) != tolower((unsigned char)searchTerm[i])) {
                                    match = 0;
                                    break;
                                }
                            }
                            if (match) {
                                found = 1;
                                break;
                            }
                        }
                        current++;
                    }
                }
            }
        }
        if (found) break; // Stop checking if we already matched the filename

        const void *buff;
        size_t size;
        int64_t offset;

        while (archive_read_data_block(a, &buff, &size, &offset) == ARCHIVE_OK) {
            if (size >= termLen) {
                const char* current = (const char*)buff;
                const char* end = current + size - termLen;

                if (caseSensitive) {
                    while (current <= end) {
                        current = (const char*)memchr(current, searchTerm[0], end - current + 1);
                        if (!current) break; // First letter not found

                        if (memcmp(current, searchTerm, termLen) == 0) {
                            found = 1; // Match found!
                            break;
                        }
                        current++;
                    }
                } else {
                    char lowerFirst = searchTerm[0];
                    char upperFirst = (char)toupper((unsigned char)lowerFirst);
                    while (current <= end) {
                        if (*current == lowerFirst || *current == upperFirst) {
                            int match = 1;
                            for (size_t i = 1; i < termLen; i++) {
                                if (tolower((unsigned char)current[i]) != (unsigned char)searchTerm[i]) {
                                    match = 0;
                                    break;
                                }
                            }
                            if (match) {
                                found = 1;
                                break;
                            }
                        }
                        current++;
                    }
                }
            }
            if (found) break; // Stop reading blocks if we found a match
        }
        if (found) break; // Stop opening internal files if we found a match
    }

    archive_read_close(a);
    archive_read_free(a);
    return found;
}

// --- Keyboard Hook Global State ---
typedef void (*ToggleUICallback)();
static HHOOK g_hKeyboardHook = NULL;
static HHOOK g_hMouseHook = NULL;
static ToggleUICallback g_ToggleCallback = NULL;
static int g_winKeyIsDown = 0;
static int g_otherKeyPressed = 0;

LRESULT CALLBACK LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode >= 0) {
        KBDLLHOOKSTRUCT* kbdStruct = (KBDLLHOOKSTRUCT*)lParam;
        DWORD vkCode = kbdStruct->vkCode;

        if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) {
            if (vkCode == VK_LWIN || vkCode == VK_RWIN) {
                g_winKeyIsDown = 1;
                g_otherKeyPressed = 0;
            } else if (g_winKeyIsDown) {
                // User pressed a combo like Win + E
                g_otherKeyPressed = 1;
            }
        } else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP) {
            if (vkCode == VK_LWIN || vkCode == VK_RWIN) {
                g_winKeyIsDown = 0;

                if (!g_otherKeyPressed && g_ToggleCallback) {
                    // Trick Windows into thinking a combo was pressed to abort the native Start Menu.
                    // By using 'B' (0x42) instead of a dummy key, we simulate "Win + B".
                    // This native shortcut seamlessly forces the Taskbar to appear over fullscreen games!
                    keybd_event(0x42, 0, 0, 0); // 'B' key down
                    keybd_event(0x42, 0, KEYEVENTF_KEYUP, 0); // 'B' key up

                    // Tell the C# UI to toggle itself
                    g_ToggleCallback();
                }
            }
        }
    }
    return CallNextHookEx(g_hKeyboardHook, nCode, wParam, lParam);
}

// --- Mouse Hook for the physical Start Button ---
LRESULT CALLBACK LowLevelMouseProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode >= 0 && wParam == WM_LBUTTONDOWN) {
        MSLLHOOKSTRUCT* mouseStruct = (MSLLHOOKSTRUCT*)lParam;

            int isStartClicked = 0;

            // 1. Precise RECT hit-testing (Fixes Win11 XAML transparent proxy issues)
            HWND hTaskbar = FindWindowA("Shell_TrayWnd", NULL);
            if (hTaskbar) {
                HWND hStart = FindWindowExA(hTaskbar, NULL, "Start", NULL);
                if (hStart) {
                    RECT r; GetWindowRect(hStart, &r);
                    if (mouseStruct->pt.x >= r.left && mouseStruct->pt.x <= r.right &&
                        mouseStruct->pt.y >= r.top && mouseStruct->pt.y <= r.bottom) isStartClicked = 1;
                }
            }

            if (!isStartClicked) {
                HWND hSecTaskbar = FindWindowA("Shell_SecondaryTrayWnd", NULL);
                if (hSecTaskbar) {
                    HWND hStart = FindWindowExA(hSecTaskbar, NULL, "Start", NULL);
                    if (hStart) {
                        RECT r; GetWindowRect(hStart, &r);
                        if (mouseStruct->pt.x >= r.left && mouseStruct->pt.x <= r.right &&
                            mouseStruct->pt.y >= r.top && mouseStruct->pt.y <= r.bottom) isStartClicked = 1;
                    }
                }
            }

            // 2. Fallback to WindowFromPoint (For Win10, Start11, OpenShell)
            if (!isStartClicked) {
                HWND hWnd = WindowFromPoint(mouseStruct->pt);
                if (hWnd) {
                    HWND curr = hWnd;
                    int isTaskbar = 0;
                    while (curr) {
                        char ancClass[256] = {0};
                        GetClassNameA(curr, ancClass, sizeof(ancClass));
                        for(int i=0; ancClass[i]; i++) ancClass[i] = (char)tolower((unsigned char)ancClass[i]);

                        if (strstr(ancClass, "shell_traywnd") ||
                            strstr(ancClass, "shell_secondarytraywnd") ||
                            strstr(ancClass, "xamlexplorerhostisland") ||
                            strstr(ancClass, "start11")) {
                            isTaskbar = 1;
                            break; // Found a valid taskbar root
                        }
                        curr = GetParent(curr);
                    }

                    if (isTaskbar) {
                        char cls[256] = {0}, txt[256] = {0};
                        GetClassNameA(hWnd, cls, sizeof(cls));
                        GetWindowTextA(hWnd, txt, sizeof(txt));

                        for(int i=0; cls[i]; i++) cls[i] = (char)tolower((unsigned char)cls[i]);
                        for(int i=0; txt[i]; i++) txt[i] = (char)tolower((unsigned char)txt[i]);

                        if (strstr(cls, "start") || strstr(txt, "start")) {
                            isStartClicked = 1;
                        }
                    }
                }
            }

            if (isStartClicked) {
                if (g_ToggleCallback) {
                    // Trick Windows into aborting the native start menu just in case the mouse click leaks through
                    keybd_event(0xDF, 0, 0, 0); // VK_OEM_8
                    keybd_event(0xDF, 0, KEYEVENTF_KEYUP, 0);

                    g_ToggleCallback();
                    return 1; // Swallow the click!
                }
            }
    }
    return CallNextHookEx(g_hMouseHook, nCode, wParam, lParam);
}

__declspec(dllexport) void InstallSystemHooks(ToggleUICallback callback) {
    if (g_hKeyboardHook == NULL) {
        g_ToggleCallback = callback;
        g_hKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, LowLevelKeyboardProc, GetModuleHandle(NULL), 0);
    }
    if (g_hMouseHook == NULL) {
        g_hMouseHook = SetWindowsHookEx(WH_MOUSE_LL, LowLevelMouseProc, GetModuleHandle(NULL), 0);
    }
}

__declspec(dllexport) void UninstallSystemHooks() {
    if (g_hKeyboardHook != NULL) {
        UnhookWindowsHookEx(g_hKeyboardHook);
        g_hKeyboardHook = NULL;
    }
    if (g_hMouseHook != NULL) {
        UnhookWindowsHookEx(g_hMouseHook);
        g_hMouseHook = NULL;
    }
    g_ToggleCallback = NULL;
}

#ifdef __cplusplus
}
#endif
