# fast_search.dll Hooks

This document outlines the exported C functions (hooks) provided by `fast_search.dll` for use by the Python application (`main.py`).

## Exported Functions

### `GetVolumeUSNJournalID`
*   **Signature:** `uint64_t GetVolumeUSNJournalID(const char* drive_letter)`
*   **Description:** Retrieves the USN Journal ID for a given drive. It is used primarily as a safety check to verify if the specified drive is formatted as NTFS and if the application has the Administrator privileges required to access the Master File Table.

### `CountFilesInDrive`
*   **Signature:** `uint64_t CountFilesInDrive(const char* drive_letter)`
*   **Description:** Returns the total number of files indexed on the specified drive.

### `SearchByExtension`
*   **Signature:** `uint64_t SearchByExtension(const char* drive_letter, const wchar_t* extension)`
*   **Description:** Searches the specified drive for files matching a specific wide-character file extension.

### `FastGrepFile`
*   **Signature:** `int FastGrepFile(const char* filepath, const char* search_str, int case_sensitive)`
*   **Description:** Scans the raw contents of a standard file for a specific search string. Returns a non-zero value (typically `1`) if the string is found, and `0` otherwise.

### `FastGrepArchive`
*   **Signature:** `int FastGrepArchive(const char* filepath, const char* search_str, int case_sensitive)`
*   **Description:** Works similarly to `FastGrepFile`, but uses libarchive to decompress and scan the contents of supported archive formats (e.g., zip, 7z, rar) in memory.

### `ScanDriveWithCallback`
*   **Signature:** `uint64_t ScanDriveWithCallback(const char* drive_letter, void (*callback)(uint64_t file_id, uint64_t parent_id, const wchar_t* file_name))`
*   **Description:** Initiates a scan of the Master File Table (MFT) on the specified drive. For every file encountered, it triggers the provided callback function, passing along the file's ID, its parent directory's ID, and the file's name. This allows the Python layer to rapidly build a directory tree and filter by filename without crawling the filesystem conventionally.

## Callbacks

### `CALLBACK_TYPE`
*   **Signature:** `void callback(uint64_t file_id, uint64_t parent_id, const wchar_t* file_name)`
*   **Description:** The function signature required by `ScanDriveWithCallback`. 
    *   `file_id`: Unique identifier for the file/folder in the MFT.
    *   `parent_id`: Identifier of the directory containing the file. Used to reconstruct the full path.
    *   `file_name`: The name of the file or directory.
