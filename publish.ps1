Write-Host "Publishing MBR-Deep Suite..." -ForegroundColor Cyan

$distDir = "$PSScriptRoot\dist"
$payloadDir = "$distDir\payload"
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir | Out-Null

# 1. Compile C-Engine
Write-Host "`n[1/5] Compiling C-Engine..." -ForegroundColor Yellow
Set-Location $PSScriptRoot
& ".\build.ps1"
if ($LASTEXITCODE -ne 0) { Write-Host "C-Engine build failed!" -ForegroundColor Red; exit 1 }

# 2. Publish Backend
Write-Host "`n[2/5] Publishing Backend Service..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\src\BackendService\MBR-DeepService.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$payloadDir\Backend"

# 3. Publish Frontend
Write-Host "`n[3/5] Publishing App Drawer..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\src\UI_WPF\MBR-DeepDrawer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$payloadDir\Frontend"

# Ensure the C-Engine DLL is explicitly copied to the UI folder so the global keyboard/mouse hooks function properly
Copy-Item "$PSScriptRoot\src\Engine\fast_search.dll" "$payloadDir\Frontend\" -Force -ErrorAction SilentlyContinue

# Bundle the perfect God Mode .lnk shortcuts
Write-Host "`n[*] Bundling God Mode Shortcuts..." -ForegroundColor Yellow
Copy-Item "$PSScriptRoot\src\GodModeLinks" "$payloadDir\GodModeLinks" -Recurse -Force

# 4. Compress Payload
Write-Host "`n[4/5] Compressing Payload..." -ForegroundColor Yellow
$zipPath = "$distDir\payload.zip"
Compress-Archive -Path "$payloadDir\*" -DestinationPath $zipPath -Force

# 5. Build Native C# Installer EXE
Write-Host "`n[5/5] Building standalone Installer EXE..." -ForegroundColor Yellow
$installerDir = "$distDir\InstallerSource"
New-Item -ItemType Directory -Path $installerDir | Out-Null

$iconXml = ""
if (Test-Path "$PSScriptRoot\icon3.ico") {
  Copy-Item "$PSScriptRoot\icon3.ico" "$installerDir\icon3.ico" -Force
  $iconXml = "<ApplicationIcon>icon3.ico</ApplicationIcon>"
}

# Generate the C# Project File for the Installer
$csprojContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    $iconXml
    <AssemblyTitle>MBR-Deep Setup</AssemblyTitle>
    <Description>MBR-Deep Installer</Description>
    <Product>MBR-Deep</Product>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="..\payload.zip" LogicalName="payload.zip" />
  </ItemGroup>
</Project>
"@
Set-Content -Path "$installerDir\Installer.csproj" -Value $csprojContent

# Ensure the Installer forces the UAC Admin Prompt
$manifestContent = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
'@
Set-Content -Path "$installerDir\app.manifest" -Value $manifestContent

# Generate the Installer Logic
$programContent = @'
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace MBRDeepInstaller
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("MBR-Deep Installer");
            Console.WriteLine("==================");

            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MBR-Deep");

            try
            {
                Console.WriteLine("Stopping existing service (if running)...");
                Process.Start(new ProcessStartInfo("cmd.exe", "/c sc stop MBRDeepService & sc delete MBRDeepService") { CreateNoWindow = true }).WaitForExit();
                Thread.Sleep(2000); // Give the OS time to release file locks

                Console.WriteLine("Extracting files to " + installDir + "...");
                if (Directory.Exists(installDir)) { Directory.Delete(installDir, true); }
                Directory.CreateDirectory(installDir);

                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                using (ZipArchive archive = new ZipArchive(stream))
                {
                    archive.ExtractToDirectory(installDir, true);
                }

                Console.WriteLine("Installing Background Service...");
                string backendExe = Path.Combine(installDir, "Backend", "MBR-DeepService.exe");
                Process.Start(new ProcessStartInfo("sc.exe", $"create MBRDeepService binPath= \"{backendExe}\" start= auto") { CreateNoWindow = true }).WaitForExit();
                Process.Start(new ProcessStartInfo("sc.exe", $"create MBRDeepService binPath= \"{backendExe}\" start= auto DisplayName= \"MBR-Deep Engine Service\"") { CreateNoWindow = true }).WaitForExit();
                Process.Start(new ProcessStartInfo("sc.exe", $"description MBRDeepService \"MBR-Deep Background Search Engine\"") { CreateNoWindow = true }).WaitForExit();
                Process.Start(new ProcessStartInfo("sc.exe", "start MBRDeepService") { CreateNoWindow = true }).WaitForExit();

                Console.WriteLine("Creating Start Menu and Startup Shortcuts...");
                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
                string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                string target = Path.Combine(installDir, "Frontend", "MBR-DeepDrawer.exe");
                string startMenuLnk = Path.Combine(startMenu, "MBR-Deep Search.lnk");
                string startupLnk = Path.Combine(startupFolder, "MBR-Deep Search.lnk");
                string psCommand = $"-NoProfile -Command \"$wshell = New-Object -ComObject WScript.Shell; $s = $wshell.CreateShortcut('{startMenuLnk}'); $s.TargetPath = '{target}'; $s.Save(); $s2 = $wshell.CreateShortcut('{startupLnk}'); $s2.TargetPath = '{target}'; $s2.Arguments = '-hidden'; $s2.Save()\"";
                Process.Start(new ProcessStartInfo("powershell", psCommand) { CreateNoWindow = true }).WaitForExit();

                Console.WriteLine("Registering Uninstaller...");
                string uninstallBatPath = Path.Combine(installDir, "Uninstall.bat");
                string batContent = $@"@echo off
echo MBR-Deep Uninstaller
echo ====================
echo Closing App Drawer...
taskkill /F /IM MBR-DeepDrawer.exe >nul 2>&1

echo Stopping and removing Background Service...
sc stop MBRDeepService >nul 2>&1
sc delete MBRDeepService >nul 2>&1
ping 127.0.0.1 -n 3 >nul

echo Removing Shortcuts...
del ""{startMenuLnk}"" >nul 2>&1
del ""{startupLnk}"" >nul 2>&1

echo Removing Registry Keys...
reg delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MBR-Deep"" /f >nul 2>&1

echo Removing Installation Directory...
cd /d ""%TEMP%""
start /b cmd.exe /c ""ping 127.0.0.1 -n 2 >nul & rmdir /s /q ""{installDir}""""
";
                File.WriteAllText(uninstallBatPath, batContent);

                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MBR-Deep"))
                {
                    key.SetValue("DisplayName", "MBR-Deep Search");
                    key.SetValue("DisplayIcon", target);
                    key.SetValue("UninstallString", $"\"{uninstallBatPath}\"");
                    key.SetValue("Publisher", "MBR-Deep");
                    key.SetValue("NoModify", 1);
                    key.SetValue("NoRepair", 1);
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nInstallation Complete!");
                Console.ResetColor();
                Console.WriteLine("Launching MBR-Deep...");
                Thread.Sleep(1000);

                // Launch the UI through explorer.exe so it strips the Installer's Administrator privileges.
                // This ensures the AppDrawer safely runs as a standard user process as intended!
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Installation failed: " + ex.Message);
                Console.ResetColor();
                Thread.Sleep(5000);
            }
        }
    }
}
'@
Set-Content -Path "$installerDir\Program.cs" -Value $programContent

# Build the standalone Installer EXE
dotnet publish "$installerDir\Installer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$distDir\Output"
Move-Item "$distDir\Output\Installer.exe" "$distDir\MBRDeep_Installer.exe" -Force
Remove-Item "$installerDir", "$payloadDir", "$distDir\Output", $zipPath -Recurse -Force

Write-Host "`nDone! Your standalone installer is ready at: $distDir\MBRDeep_Installer.exe" -ForegroundColor Green
