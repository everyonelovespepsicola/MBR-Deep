Write-Host "Setting up God Mode Test Environment..." -ForegroundColor Cyan

$distDir = "$PSScriptRoot\dist\GodModeTest"
$payloadDir = "$distDir\payload"
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir | Out-Null

# 1. Create God Mode Test Client
Write-Host "`n[1/3] Building God Mode Test Client..." -ForegroundColor Yellow
$clientDir = "$distDir\ClientSource"
New-Item -ItemType Directory -Path $clientDir | Out-Null

$csprojContent = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@
Set-Content -Path "$clientDir\GodModeTestClient.csproj" -Value $csprojContent

$programContent = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace GodModeTester
{
    class Program
    {
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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(IntPtr ppidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_PIDL = 0x00000008;

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        const uint WM_CLOSE = 0x0010;

        [STAThread]
        static void Main(string[] args)
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "godmode_test.log");
            using var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs) { AutoFlush = true };

            Console.SetOut(sw);
            Console.SetError(sw);

            Console.WriteLine("Starting God Mode Launch Test...");
            Console.WriteLine("=================================");

            int successCount = 0;
            int errorCount = 0;

            try
            {
                Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
                if (shellAppType == null)
                {
                    Console.WriteLine("[ERROR] Could not load Shell.Application COM object.");
                    return;
                }

                dynamic shell = Activator.CreateInstance(shellAppType)!;
                dynamic folder = shell.NameSpace("shell:::{ED7BA470-8E54-465E-825C-99712043E01C}");

                if (folder != null)
                {
                    int index = 1;
                    foreach (dynamic item in folder.Items())
                    {
                        string name = "";
                        string path = "";
                        try
                        {
                            name = item.Name;
                            string rawPath = item.Path;

                            path = rawPath;
                            int lastBrace = rawPath.LastIndexOf('{');
                            if (lastBrace >= 0)
                            {
                                path = "shell:::" + rawPath.Substring(lastBrace);
                            }

                            Console.WriteLine($"\n[{index}] Processing: {name}");
                            Console.WriteLine($"    Path: {path}");

                            // Test icon extraction
                            IntPtr pidl = IntPtr.Zero;
                            uint sfgaoOut = 0;
                            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out sfgaoOut) == 0 && pidl != IntPtr.Zero)
                            {
                                SHFILEINFO shinfo = new SHFILEINFO();
                                SHGetFileInfo(pidl, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_PIDL);
                                if (shinfo.hIcon != IntPtr.Zero)
                                {
                                    Console.WriteLine("    [OK] Icon Extracted.");
                                    DestroyIcon(shinfo.hIcon);
                                }
                                else
                                {
                                    Console.WriteLine("    [WARN] No Icon found via PIDL.");
                                }
                                ILFree(pidl);
                            }
                            else
                            {
                                Console.WriteLine("    [WARN] SHParseDisplayName failed.");
                            }

                            // Test launching
                            Console.WriteLine("    Launching...");
                            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });

                            // Wait for the applet to open and steal focus
                            Thread.Sleep(2500);

                            // Try to close the newly opened window gracefully
                            IntPtr fgWindow = GetForegroundWindow();
                            IntPtr myConsole = GetConsoleWindow();
                            if (fgWindow != IntPtr.Zero && fgWindow != myConsole)
                            {
                                Console.WriteLine("    [OK] Closing active window...");
                                PostMessage(fgWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            }

                            // Spaced out so the OS has time to clean up
                            Thread.Sleep(1000);

                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    [ERROR] Failed on item {name} ({path}): {ex.Message}");
                            errorCount++;
                        }
                        index++;
                    }
                }
                else
                {
                    Console.WriteLine("[ERROR] Could not load God Mode folder.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL ERROR] {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("\n=================================");
            Console.WriteLine($"Test Complete. Success: {successCount}, Errors: {errorCount}");
        }
    }
}
'@
Set-Content -Path "$clientDir\Program.cs" -Value $programContent

dotnet publish "$clientDir\GodModeTestClient.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$payloadDir\Client"

# 2. Compress Payload
Write-Host "`n[2/3] Compressing Payload..." -ForegroundColor Yellow
$zipPath = "$distDir\payload.zip"
Compress-Archive -Path "$payloadDir\*" -DestinationPath $zipPath -Force

# 3. Create the executable launcher (Test_GM_Setup.exe)
Write-Host "`n[3/3] Finalizing standalone executable Test_GM_Setup.exe..." -ForegroundColor Yellow

# We simply pull out the compiled single-file EXE instead of wrapping it in another C# installer
# since we aren't installing a Windows service for this specific test

Move-Item "$payloadDir\Client\GodModeTestClient.exe" "$distDir\Test_GM_Setup.exe" -Force

# Clean up build artifacts
Remove-Item "$clientDir", "$payloadDir", $zipPath -Recurse -Force

Write-Host "`nDone! Your God Mode test executable is ready at: $distDir\Test_GM_Setup.exe" -ForegroundColor Green
Write-Host "You can update Sandbox.wsb to launch this file to safely test launching all ~250 God Mode items." -ForegroundColor Cyan
