Write-Host "Setting up MBR-Deep Stress Test Environment..." -ForegroundColor Cyan

$distDir = "$PSScriptRoot\dist\StressTest"
$payloadDir = "$distDir\payload"
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir | Out-Null

# 1. Compile C-Engine
Write-Host "`n[1/5] Compiling C-Engine..." -ForegroundColor Yellow
Set-Location $PSScriptRoot
& ".\build.ps1"
if ($LASTEXITCODE -ne 0) { Write-Host "C-Engine build failed!" -ForegroundColor Red; exit 1 }

# Compile HLSL Shaders for WPF
Write-Host "`n[1.5/5] Compiling HLSL Shaders..." -ForegroundColor Yellow
& ".\compile_shaders.ps1"
if ($LASTEXITCODE -ne 0) { Write-Host "Shader compilation failed!" -ForegroundColor Red; exit 1 }

# 2. Publish Backend
Write-Host "`n[2/5] Publishing Backend Service..." -ForegroundColor Yellow
dotnet publish "$PSScriptRoot\src\BackendService\MBR-DeepService.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$payloadDir\Backend"

# 3. Create Stress Test Client
Write-Host "`n[3/5] Building Stress Test Client..." -ForegroundColor Yellow
$clientDir = "$distDir\ClientSource"
New-Item -ItemType Directory -Path $clientDir | Out-Null

$csprojContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- Masquerade as MBR-DeepDrawer to pass the Backend Service security check -->
    <AssemblyName>MBR-DeepDrawer</AssemblyName>
  </PropertyGroup>
</Project>
"@
Set-Content -Path "$clientDir\StressTestClient.csproj" -Value $csprojContent

$programContent = @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StressTestClient
{
    class MultiTextWriter : TextWriter
    {
        TextWriter _a, _b;
        public MultiTextWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override Encoding Encoding => _a.Encoding;
        public override void Write(char value) { _a.Write(value); _b.Write(value); }
        public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
        public override void Flush() { _a.Flush(); _b.Flush(); }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "test.log");
            using var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs) { AutoFlush = true };

            var origOut = Console.Out;
            var origErr = Console.Error;
            Console.SetOut(TextWriter.Synchronized(new MultiTextWriter(origOut, sw)));
            Console.SetError(TextWriter.Synchronized(new MultiTextWriter(origErr, sw)));

            Console.WriteLine("Starting MBR-Deep Stress Test Client...");
            Console.WriteLine("Simulating aggressive connect/disconnect behavior...");

            // Run multiple concurrent clients to stress the pipe server's connection handling
            var tasks = new Task[10];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // Run for 60 seconds

            for(int i = 0; i < 10; i++)
            {
                int clientId = i;
                tasks[i] = Task.Run(() => RunClientLoop(clientId, cts.Token));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }

            Console.WriteLine("Stress test completed after 60 seconds.");
        }

        static async Task RunClientLoop(int clientId, CancellationToken token)
        {
            int attempts = 0;
            while (!token.IsCancellationRequested)
            {
                attempts++;
                try
                {
                    using var pipeClient = new NamedPipeClientStream(".", "MBRDeepSearchPipe", PipeDirection.InOut, PipeOptions.Asynchronous);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(2));
                    await pipeClient.ConnectAsync(cts.Token);

                    Console.WriteLine($"[Client {clientId}] Connected (Attempt {attempts}).");

                    using var writer = new StreamWriter(pipeClient, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(pipeClient, Encoding.UTF8, leaveOpen: true);

                    // Randomize between basic and fully-populated advanced queries to stress test all backend code paths
                    object request;
                    if (Random.Shared.Next(0, 2) == 0)
                    {
                        request = new { IsAdvanced = false, BasicQuery = "e" };
                    }
                    else
                    {
                        var fileTypes = new[] { "Everything", "Document", "Folder", "Audio", "Video", "Image", "Compressed", "Executable" };
                        request = new
                        {
                            IsAdvanced = true,
                            AdvName1 = "e",
                            AdvName2 = "a",
                            AdvContent1 = Random.Shared.Next(0, 2) == 0 ? "test" : "data",
                            AdvContent2 = Random.Shared.Next(0, 2) == 0 ? "info" : "log",
                            AdvLocation = @"C:\",
                            AdvCaseSensitive = Random.Shared.Next(0, 2) == 0,
                            AdvDrive = "C",
                            AdvFileType = fileTypes[Random.Shared.Next(fileTypes.Length)]
                        };
                    }
                    string json = JsonSerializer.Serialize(request);
                    await writer.WriteLineAsync(json.AsMemory(), token);

                    // Read some lines but randomly close the connection midway
                    for (int i = 0; i < Random.Shared.Next(1, 200); i++)
                    {
                        var line = await reader.ReadLineAsync(token);
                        if (line == null || line == "---EOF---") break;

                        if (Random.Shared.Next(0, 100) < 5) // 5% chance per line to crash
                        {
                            Console.WriteLine($"[Client {clientId}] Simulating crash! Disconnecting mid-stream.");
                            break; // Exiting the loop disposes the pipe instantly
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) break;
                    Console.WriteLine($"[Client {clientId}] Connection timeout.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Client {clientId}] Connection error: {ex.Message}");
                }

                // Short wait before slamming the server again
                if (!token.IsCancellationRequested)
                {
                    await Task.Delay(Random.Shared.Next(10, 200), token).ContinueWith(_ => {});
                }
            }
        }
    }
}
'@
Set-Content -Path "$clientDir\Program.cs" -Value $programContent

dotnet publish "$clientDir\StressTestClient.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$payloadDir\Client"

# 4. Compress Payload
Write-Host "`n[4/5] Compressing Payload..." -ForegroundColor Yellow
$zipPath = "$distDir\payload.zip"
Compress-Archive -Path "$payloadDir\*" -DestinationPath $zipPath -Force

# 5. Build Native C# Installer EXE
Write-Host "`n[5/5] Building standalone Test_Setup.exe..." -ForegroundColor Yellow
$installerDir = "$distDir\InstallerSource"
New-Item -ItemType Directory -Path $installerDir | Out-Null

$csprojContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="..\payload.zip" LogicalName="payload.zip" />
  </ItemGroup>
</Project>
"@
Set-Content -Path "$installerDir\Installer.csproj" -Value $csprojContent

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

$installerProgramContent = @'
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;

namespace TestInstaller
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("MBR-Deep Stress Test Installer");
            Console.WriteLine("==============================");

            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MBR-Deep-Test");

            try
            {
                Console.WriteLine("Stopping existing service (if running)...");
                Process.Start(new ProcessStartInfo("cmd.exe", "/c sc stop MBRDeepService & sc delete MBRDeepService") { CreateNoWindow = true }).WaitForExit();
                Thread.Sleep(2000);

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
                Process.Start(new ProcessStartInfo("sc.exe", "start MBRDeepService") { CreateNoWindow = true }).WaitForExit();

                Console.WriteLine("Waiting 10 seconds before launching the test...");
                Thread.Sleep(10000);

                Console.WriteLine("Launching Stress Test Client (Terminal will be visible for 60 seconds)...");
                string clientExe = Path.Combine(installDir, "Client", "MBR-DeepDrawer.exe");

                Process.Start(new ProcessStartInfo(clientExe) { UseShellExecute = true });

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nInstallation Complete!");
                Console.WriteLine("The stress test is currently running. The log will appear on your desktop as 'test.log'.");
                Console.WriteLine("This installer will now exit.");
                Console.ResetColor();
                Thread.Sleep(3000);
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
Set-Content -Path "$installerDir\Program.cs" -Value $installerProgramContent

dotnet publish "$installerDir\Installer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$distDir\Output"

Move-Item "$distDir\Output\Installer.exe" "$distDir\Test_Setup.exe" -Force
Remove-Item "$installerDir", "$payloadDir", "$distDir\Output", $zipPath -Recurse -Force

Write-Host "`nDone! Your all-in-one test installer is ready at: $distDir\Test_Setup.exe" -ForegroundColor Green
