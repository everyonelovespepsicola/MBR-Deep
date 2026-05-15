Here is the 2-part development manifest for migrating MBR-Deep to a high-performance, Client-Server architecture. This roadmap will transition your app from a monolithic, Admin-only executable into an always-on, ultra-fast system service with a lightweight, instant-summon UI.

Part 1: The MBR-Deep Background Service (The Engine)
In this phase, we will extract the MFT scanning logic from your UI and place it into an isolated background process that runs as Administrator/SYSTEM.

Step 1.1: Scaffold the Service Project
Create a new .NET 10 "Worker Service" or a standard Console App.
Configure the project to copy fast_search.dll to its output directory.
Set the execution level in an app.manifest file to requireAdministrator so it inherently has MFT access when running standalone, or install it as a Windows Service (running as LocalSystem).
Step 1.2: Migrate the C-Engine Bridge
Move the [DllImport("fast_search.dll")] P/Invoke signatures out of the WPF app and into the new Service project.
Implement the C# FileFoundCallback inside the service to handle the raw data coming from the C-engine.
Step 1.3: Implement the IPC (Inter-Process Communication) Server
Set up a NamedPipeServerStream (e.g., \\.\pipe\MBRDeepSearchPipe).
Configure the pipe to run asynchronously, listening for incoming string queries from the user space.
When a query is received, trigger the MFT scan (or query the cache in the future) and stream the results back over the pipe as serialized binary data or lightweight JSON.
Step 1.4: Testing the Service
Build a tiny, temporary CLI client (or a quick PowerShell script) that connects to \\.\pipe\MBRDeepSearchPipe, sends a search string like "test", and prints whatever the server sends back.
Goal: Verify the Service can read the MFT and return results without any GUI attached.
Part 2: The User-Space App Drawer (The Frontend)
Once the backend is proven to work, we will strip the heavy logic out of your WPF App Drawer so it acts purely as a lightning-fast display layer.

Step 2.1: Strip Admin Requirements & Uncouple the DLL
Remove all fast_search.dll P/Invoke code from AppDrawerWindow.xaml.cs.
Ensure the WPF app runs as a standard user process (no UAC prompts!).
Step 2.2: Implement the IPC Client
Set up a NamedPipeClientStream in the WPF app that connects to the Service's pipe.
Update the SearchBox_TextChanged event: instead of running a local Task, it simply sends the currentSearchTerm over the Named Pipe.
Step 2.3: Stream Handling & UI Updates
Create a background listener in the WPF app that continuously reads incoming matches from the Named Pipe.
Use Application.Current.Dispatcher to safely push these incoming results into the ObservableCollection<SearchResult>, updating the GNOME-style grid live.
Step 2.4: Polish & System Integration
Implement Global Hotkey registration (e.g., Alt+Space or Win+Shift+S) using RegisterHotKey so the app drawer can be summoned from anywhere in Windows.
Configure the window to hide gracefully when it loses focus (acting like a true native overlay).
Implement C# icon extraction (SHGetFileInfo) to finally replace the empty placeholders with real system icons.
