# publish.ps1

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $ScriptDir

$EnvDir = ".env"

if (Test-Path "$EnvDir\Scripts\Activate.ps1") {
    Write-Host "Found virtual environment." -ForegroundColor Cyan
} else {
    Write-Host "Error: Virtual environment not found! Please run run.ps1 first to set up the environment." -ForegroundColor Red
    exit 1
}

Write-Host "Setting up requirements..."
& ".\$EnvDir\Scripts\python.exe" -m pip install --upgrade pip
& ".\$EnvDir\Scripts\python.exe" -m pip install pyinstaller sv_ttk pypdfium2 pywin32 pillow

# Check if the DLL has been built
if (-Not (Test-Path "..\Engine\fast_search.dll")) {
    Write-Host "Error: ..\Engine\fast_search.dll not found! Please run build.ps1 from the project root before publishing." -ForegroundColor Red
    exit 1
}

# Update the output tree
Write-Host "Updating output_tree.txt..." -ForegroundColor Cyan
cmd /c "cd ..\.. && update_tree.bat"

Write-Host "Compiling main.py into an executable..."

# --noconfirm: Overwrite the output directory without asking
# --windowed: Run as a GUI app without a background console window
# --onefile: Create a single executable file instead of a directory
# --icon: Set the Windows executable icon
# --add-data: Include the fast_search.dll in the output
# --collect-all: Ensure sv_ttk tcl theme assets are completely included
& ".\$EnvDir\Scripts\python.exe" -m PyInstaller --noconfirm --windowed --onefile `
    --icon "..\..\icon.ico" `
    --add-data "..\Engine\fast_search.dll;." `
    --add-data "..\..\icon.ico;." `
    --collect-all "sv_ttk" `
    --name "MBR-Deep" `
    main.py

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: PyInstaller build failed." -ForegroundColor Red
    exit 1
}

Write-Host "Build complete! You can find your compiled application at 'dist\MBR-Deep.exe'."
