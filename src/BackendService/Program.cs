using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32.SafeHandles;
using UglyToad.PdfPig;

namespace MBRDeep.BackendService
{
    class Program
    {
        static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "MBR-Deep Engine Service";
            });

            builder.Services.AddHostedService<SearchEngineWorker>();

            var host = builder.Build();
            host.Run();
        }
    }

    public class SearchEngineWorker : BackgroundService
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.Dictionary<ulong, (ulong parentId, string fileName)>> _globalMftCache = new();

        private static readonly System.Collections.Generic.HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase) { ".aac", ".ac3", ".aif", ".aifc", ".aiff", ".au", ".cda", ".dts", ".fla", ".flac", ".it", ".m1a", ".m2a", ".m3u", ".m4a", ".m4b", ".m4p", ".mid", ".midi", ".mka", ".mod", ".mp2", ".mp3", ".mpa", ".ogg", ".ra", ".rmi", ".snd", ".spc", ".umx", ".voc", ".wav", ".wma", ".xm" };
        private static readonly System.Collections.Generic.HashSet<string> CompressedExts = new(StringComparer.OrdinalIgnoreCase) { ".7z", ".ace", ".arj", ".bz2", ".cab", ".gz", ".gzip", ".jar", ".r00", ".r01", ".r02", ".r03", ".r04", ".r05", ".r06", ".r07", ".r08", ".r09", ".r10", ".r11", ".r12", ".r13", ".r14", ".r15", ".r16", ".r17", ".r18", ".r19", ".r20", ".r21", ".r22", ".r23", ".r24", ".r25", ".r26", ".r27", ".r28", ".r29", ".rar", ".tar", ".tgz", ".z", ".zip" };
        private static readonly System.Collections.Generic.HashSet<string> DocumentExts = new(StringComparer.OrdinalIgnoreCase) { ".c", ".chm", ".cpp", ".csv", ".cxx", ".doc", ".docm", ".docx", ".dot", ".dotm", ".dotx", ".h", ".hpp", ".htm", ".html", ".hxx", ".ini", ".java", ".log", ".lua", ".md", ".mht", ".mhtml", ".odt", ".pdf", ".potx", ".potm", ".ppam", ".ppsm", ".ppsx", ".pps", ".ppt", ".pptm", ".pptx", ".rtf", ".sldm", ".sldx", ".thmx", ".txt", ".vsd", ".wpd", ".wps", ".wri", ".xlam", ".xls", ".xlsb", ".xlsm", ".xlsx", ".xltm", ".xltx", ".xml" };
        private static readonly System.Collections.Generic.HashSet<string> ExecutableExts = new(StringComparer.OrdinalIgnoreCase) { ".bat", ".cmd", ".exe", ".msi", ".msp", ".scr" };
        private static readonly System.Collections.Generic.HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".ani", ".bmp", ".gif", ".ico", ".jpe", ".jpeg", ".jpg", ".pcx", ".png", ".psd", ".tga", ".tif", ".tiff", ".webp", ".wmf" };
        private static readonly System.Collections.Generic.HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase) { ".3g2", ".3gp", ".3gp2", ".3gpp", ".amr", ".amv", ".asf", ".avi", ".bdmv", ".bik", ".d2v", ".divx", ".drc", ".dsa", ".dsm", ".dss", ".dsv", ".evo", ".f4v", ".flc", ".fli", ".flic", ".flv", ".hdmov", ".ifo", ".ivf", ".m1v", ".m2p", ".m2t", ".m2ts", ".m2v", ".m4v", ".mkv", ".mp2v", ".mp4", ".mp4v", ".mpe", ".mpeg", ".mpg", ".mpls", ".mpv2", ".mpv4", ".mov", ".mts", ".ogm", ".ogv", ".pss", ".pva", ".qt", ".ram", ".ratdvd", ".rm", ".rmm", ".rmvb", ".roq", ".rpm", ".smil", ".smk", ".swf", ".tp", ".tpr", ".ts", ".vob", ".vp6", ".webm", ".wm", ".wmp", ".wmv" };

        // Moved from the WPF App
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public delegate bool FileFoundCallback(ulong fileId, ulong parentId, [MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [DllImport("fast_search.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern ulong ScanDriveWithCallback(string driveLetter, FileFoundCallback callback);

        [DllImport("fast_search.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int FastGrepFile([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath, [MarshalAs(UnmanagedType.LPUTF8Str)] string searchTerm, int caseSensitive, IntPtr isCancelled);

        [DllImport("fast_search.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int FastGrepArchive([MarshalAs(UnmanagedType.LPUTF8Str)] string archivePath, [MarshalAs(UnmanagedType.LPUTF8Str)] string searchTerm, int caseSensitive, IntPtr isCancelled);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle Pipe, out uint ClientProcessId);

        private bool SearchPdf(string filepath, string searchStr, bool caseSensitive, CancellationToken token)
        {
            try
            {
                StringComparison comp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                using (PdfDocument document = PdfDocument.Open(filepath))
                {
                    foreach (var page in document.GetPages())
                    {
                        if (token.IsCancellationRequested) return false;
                        string text = page.Text;
                        if (text.Contains(searchStr, comp))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { /* PDF might be encrypted, corrupted, or locked */ }
            return false;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("MBR-Deep Engine Service starting...");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Building Global MFT Cache in RAM for instant keystroke searches...");
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                {
                    try
                    {
                        string d = drive.Name.Substring(0, 1);
                        // Pre-allocate capacity to prevent massive memory re-allocations
                        var table = new System.Collections.Generic.Dictionary<ulong, (ulong parentId, string fileName)>(1_000_000);

                        FileFoundCallback callback = (fileId, parentId, fileName) =>
                        {
                            table[fileId] = (parentId, fileName);
                            return true;
                        };
                        ScanDriveWithCallback(d, callback);
                        GC.KeepAlive(callback); // Pin the delegate to prevent JIT/GC crashes in unmanaged code

                        _globalMftCache[d] = table;
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Error caching drive {drive.Name}: {ex.Message}");
                        Console.ResetColor();
                    }
                }
            }
            Console.ResetColor();

            Console.WriteLine("Waiting for UI Client to connect...");

            int backoffDelay = 10; // Dynamic backoff delay starting at 10ms

            // Keep the server running continuously
            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    // Create security settings for the pipe to allow standard user WPF apps to connect
                    var pipeSecurity = new PipeSecurity();

                    // SECURITY FIX: Explicitly grant the server process Full Control.
                    // Without this, the server revokes its own 'CreateNewInstance' permission after the first connection!
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        WindowsIdentity.GetCurrent().User!,
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));

                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));

                    // Create the IPC Named Pipe Server with the ACL applied
                    pipeServer = NamedPipeServerStreamAcl.Create(
                        "MBRDeepSearchPipe",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);

                    await pipeServer.WaitForConnectionAsync(stoppingToken);

                    // --- SECURITY: Process Verification Check ---
                    // Prevent unmanaged Access Violations by ensuring the pipe hasn't already been broken by a rapid keystroke cancellation
                    if (!pipeServer.IsConnected)
                    {
                        pipeServer.Dispose();
                        continue;
                    }

                    // Ask the Windows Kernel for the true identity of the connected client
                    if (GetNamedPipeClientProcessId(pipeServer.SafePipeHandle, out uint clientProcessId))
                    {
                        try
                        {
                            using var clientProcess = Process.GetProcessById((int)clientProcessId);
                            string? exeName = clientProcess.ProcessName;
                            string? exePath = clientProcess.MainModule?.FileName;

                            bool isAuthorized = false;
                            if (string.Equals(exeName, "dotnet", StringComparison.OrdinalIgnoreCase))
                            {
                                isAuthorized = true; // Allow local developer debugging
                            }
                            else if (exeName != null && exeName.Contains("MBR-Deep", StringComparison.OrdinalIgnoreCase))
                            {
                                if (exePath != null && exePath.Contains("MBR-Deep", StringComparison.OrdinalIgnoreCase))
                                {
                                    isAuthorized = true;
                                }
                            }

                            if (!isAuthorized)
                            {
                                Console.ForegroundColor = ConsoleColor.DarkRed;
                                Console.WriteLine($"[SECURITY] Blocked unauthorized connection from {exePath} (PID: {clientProcessId})");
                                Console.ResetColor();
                                pipeServer.Dispose();
                                continue; // Drop connection, loop back, and wait for a legitimate client
                            }
                        }
                        catch (Exception)
                        {
                            // If we can't verify the process for any reason, fail securely
                            pipeServer.Dispose();
                            continue;
                        }
                    }
                    else
                    {
                        // If the Windows Kernel refuses to give us the PID, drop the connection securely
                        pipeServer.Dispose();
                        continue;
                    }
                    // --------------------------------------------

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("UI Client connected! Ready for queries.");
                    Console.ResetColor();

                    // Hand off the connection to a background task so we can immediately accept the next one!
                    _ = ProcessClientAsync(pipeServer, stoppingToken);

                    // Reset backoff delay on a successful connection
                    backoffDelay = 10;
                }
                catch (OperationCanceledException)
                {
                    pipeServer?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Pipe error: {ex.Message}");
                    Console.ResetColor();
                    pipeServer?.Dispose();

                    // Prevent a tight CPU loop if the OS is momentarily rejecting pipe creation with exponential backoff
                    await Task.Delay(backoffDelay, stoppingToken);
                    backoffDelay = Math.Min(backoffDelay * 2, 1000); // Cap the delay at 1 second max
                }
            }
        }

        private async Task ProcessClientAsync(NamedPipeServerStream pipeServer, CancellationToken stoppingToken)
        {
            try
            {
                using (pipeServer)
                using (var reader = new StreamReader(pipeServer, Encoding.UTF8, leaveOpen: true))
                using (var writer = new StreamWriter(pipeServer, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
                {
                    while (pipeServer.IsConnected && !stoppingToken.IsCancellationRequested)
                    {
                        string? jsonString = await reader.ReadLineAsync(stoppingToken);

                        // If the client cleanly closed their end, query will be null
                        if (jsonString == null) break;
                        if (string.IsNullOrWhiteSpace(jsonString)) continue;

                        SearchRequest? request;
                        try
                        {
                            request = JsonSerializer.Deserialize<SearchRequest>(jsonString);
                        }
                        catch
                        {
                            // Backwards compatibility if plain string is sent
                            request = new SearchRequest { BasicQuery = jsonString };
                        }

                        if (request == null) continue;

                        Console.WriteLine($"[Search Engine] Query received (IsAdvanced: {request.IsAdvanced})");

                        var drives = new List<string>();
                        if (request.IsAdvanced && !string.IsNullOrEmpty(request.AdvDrive) && request.AdvDrive != "All")
                        {
                            drives.Add(request.AdvDrive);
                        }
                        else
                        {
                            foreach (var drive in DriveInfo.GetDrives())
                            {
                                if (drive.IsReady)
                                {
                                    drives.Add(drive.Name.Substring(0, 1));
                                }
                            }
                        }

                        if (drives.Count == 0) continue;

                        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        CancellationToken scanToken = searchCts.Token;

                        IntPtr cancelFlag = Marshal.AllocHGlobal(1);
                        Marshal.WriteByte(cancelFlag, 0);

                        try
                        {
                            using var reg = scanToken.Register(() => Marshal.WriteByte(cancelFlag, 1));

                            var searchTask = Task.Run(() =>
                            {
                                Parallel.ForEach(drives, new ParallelOptions { MaxDegreeOfParallelism = drives.Count }, (d, driveState) =>
                                {
                                    if (scanToken.IsCancellationRequested || !pipeServer.IsConnected) { driveState.Stop(); return; }

                                    if (_globalMftCache.TryGetValue(d, out var mftTable))
                                    {
                                        var foundFiles = new System.Collections.Generic.List<ulong>();
                                        StringComparison comp = request.AdvCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                                        foreach (var entry in mftTable)
                                        {
                                            if (scanToken.IsCancellationRequested || !pipeServer.IsConnected) { driveState.Stop(); break; }

                                            if (!request.IsAdvanced)
                                            {
                                                if (!string.IsNullOrEmpty(request.BasicQuery) && entry.Value.fileName != null && entry.Value.fileName.Contains(request.BasicQuery, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    foundFiles.Add(entry.Key);
                                                    // Massive Optimization: Stop filling RAM with millions of basic matches since the UI only displays 100 anyway!
                                                    if (foundFiles.Count >= 200) break;
                                                }
                                            }
                                            else
                                            {
                                                string ext = Path.GetExtension(entry.Value.fileName) ?? "";
                                                bool matchesType = true;
                                                if (!string.IsNullOrEmpty(request.AdvFileType) && request.AdvFileType != "Everything")
                                                {
                                                    matchesType = request.AdvFileType switch
                                                    {
                                                        "Audio" => AudioExts.Contains(ext),
                                                        "Compressed" => CompressedExts.Contains(ext),
                                                        "Document" => DocumentExts.Contains(ext),
                                                        "Executable" => ExecutableExts.Contains(ext),
                                                        "Image" => ImageExts.Contains(ext),
                                                        "Video" => VideoExts.Contains(ext),
                                                        "Folder" => ext == "", // Quick prune
                                                        _ => true
                                                    };
                                                }
                                                if (!matchesType) continue;

                                                bool name1Match = string.IsNullOrEmpty(request.AdvName1) || (entry.Value.fileName != null && entry.Value.fileName.Contains(request.AdvName1, comp));
                                                bool name2Match = string.IsNullOrEmpty(request.AdvName2) || (entry.Value.fileName != null && entry.Value.fileName.Contains(request.AdvName2, comp));

                                                if (name1Match && name2Match)
                                                {
                                                    foundFiles.Add(entry.Key);
                                                }
                                            }
                                        }

                                        string GetFullPath(ulong fileId)
                                        {
                                            var pathParts = new System.Collections.Generic.List<string>();
                                            ulong currentId = fileId;
                                            int depth = 0;
                                            while (mftTable.TryGetValue(currentId, out var entry))
                                            {
                                                pathParts.Add(entry.fileName);
                                                // Add a depth limit to prevent infinite loops (OutOfMemory crashes) from cyclic MFT corruption
                                                if (entry.parentId == currentId || entry.parentId == 0 || depth++ > 128) break;
                                                currentId = entry.parentId;
                                            }
                                            pathParts.Reverse();
                                            if (pathParts.Count > 0 && (pathParts[0] == "." || pathParts[0] == ""))
                                            {
                                                pathParts.RemoveAt(0);
                                            }
                                            return d + ":\\" + string.Join("\\", pathParts);
                                        }

                                        if (!request.IsAdvanced)
                                        {
                                            int basicMatchCount = 0;
                                            foreach (var fileId in foundFiles)
                                            {
                                                if (scanToken.IsCancellationRequested || !pipeServer.IsConnected) { driveState.Stop(); break; }
                                                string fullPath = GetFullPath(fileId);
                                                lock (writer)
                                                {
                                                    try { writer.WriteLine(fullPath); } catch { searchCts.Cancel(); driveState.Stop(); break; }
                                                }

                                                // Prevent choking the Named Pipe / UI with massive result streams
                                                if (++basicMatchCount >= 200) break;
                                            }
                                        }
                                        else
                                        {
                                            Parallel.ForEach(foundFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (fileId, state) =>
                                            {
                                                if (scanToken.IsCancellationRequested || !pipeServer.IsConnected) { state.Stop(); return; }

                                                string fullPath = GetFullPath(fileId);

                                                if (!string.IsNullOrEmpty(request.AdvLocation) && !fullPath.StartsWith(request.AdvLocation, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    return;
                                                }

                                                if (request.AdvFileType == "Folder")
                                                {
                                                    if (!Directory.Exists(fullPath)) return;
                                                }

                                                bool hasContent1 = !string.IsNullOrEmpty(request.AdvContent1);
                                                bool hasContent2 = !string.IsNullOrEmpty(request.AdvContent2);

                                                if (hasContent1 || hasContent2)
                                                {
                                                    bool isPdf = fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                                                    bool isArchive = fullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                                                     fullPath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                                                                     fullPath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
                                                                     fullPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
                                                                     fullPath.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) ||
                                                                     fullPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

                                                    int caseSens = request.AdvCaseSensitive ? 1 : 0;

                                                    if (hasContent1)
                                                    {
                                                        if (isPdf) { if (!SearchPdf(fullPath, request.AdvContent1!, request.AdvCaseSensitive, scanToken)) return; }
                                                        else if (isArchive) { if (FastGrepArchive(fullPath, request.AdvContent1!, caseSens, cancelFlag) == 0) return; }
                                                        else { if (FastGrepFile(fullPath, request.AdvContent1!, caseSens, cancelFlag) == 0) return; }
                                                    }

                                                    if (hasContent2)
                                                    {
                                                        if (isPdf) { if (!SearchPdf(fullPath, request.AdvContent2!, request.AdvCaseSensitive, scanToken)) return; }
                                                        else if (isArchive) { if (FastGrepArchive(fullPath, request.AdvContent2!, caseSens, cancelFlag) == 0) return; }
                                                        else { if (FastGrepFile(fullPath, request.AdvContent2!, caseSens, cancelFlag) == 0) return; }
                                                    }
                                                }

                                                lock (writer)
                                                {
                                                    try { writer.WriteLine(fullPath); } catch { searchCts.Cancel(); state.Stop(); driveState.Stop(); return; }
                                                }
                                            });
                                        }
                                    }
                                });
                            });

                            var disconnectTask = reader.ReadLineAsync(scanToken).AsTask();
                            var completedTask = await Task.WhenAny(searchTask, disconnectTask);

                            if (completedTask == disconnectTask)
                            {
                                searchCts.Cancel();
                                try { await searchTask; } catch { }
                            }
                            else
                            {
                                if (pipeServer.IsConnected && !scanToken.IsCancellationRequested)
                                {
                                    try { writer.WriteLine("---EOF---"); } catch { }
                                }
                            }
                        }
                        catch (AggregateException ae)
                        {
                            foreach (var ex in ae.InnerExceptions)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[Scan Error] {ex.Message}");
                                Console.ResetColor();
                            }
                        }
                        finally
                        {
                            searchCts.Cancel();
                            Marshal.FreeHGlobal(cancelFlag);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on graceful task cancellation
            }
            catch (IOException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Search Engine] UI Client disconnected (Search cancelled gracefully).");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Pipe error: {ex.Message}");
                Console.ResetColor();
            }
        }
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
}
