$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $ScriptDir

$EnvDir = ".env"

# 1. Create the virtual environment if it doesn't exist
if (-Not (Test-Path "$EnvDir\Scripts\python.exe")) {
    Write-Host "Creating Python virtual environment in .\$EnvDir..." -ForegroundColor Cyan

    # Clean up broken/empty environment folder if it exists
    if (Test-Path $EnvDir) {
        Remove-Item -Recurse -Force $EnvDir
    }

    # The 'py' launcher correctly bypasses the fake Windows Store alias
    if (Get-Command "py" -ErrorAction SilentlyContinue) {
        py -m venv $EnvDir
    }
    else {
        python -m venv $EnvDir
    }

    if (-Not (Test-Path "$EnvDir\Scripts\pip.exe")) {
        Write-Host "ERROR: Virtual environment creation failed! (The Windows Store might be blocking Python)." -ForegroundColor Red
        exit 1
    }
}

# Ensure required packages are actually installed
& ".\$EnvDir\Scripts\python.exe" -c "import sv_ttk; import pypdfium2; import win32gui; from PIL import Image" 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installing missing dependencies (sv_ttk, pypdfium2, pywin32, pillow)..." -ForegroundColor Cyan
    & ".\$EnvDir\Scripts\python.exe" -m pip install sv_ttk pypdfium2 pywin32 pillow
}

# 2. Check if the DLL has been built
if (-Not (Test-Path "..\Engine\fast_search.dll")) {
    Write-Host "Warning: ..\Engine\fast_search.dll not found! Please run build.ps1 from the project root first." -ForegroundColor Red
    exit 1
}

# 3. Activate the virtual environment in the current terminal session
Write-Host "Activating virtual environment..." -ForegroundColor Cyan
. ".\$EnvDir\Scripts\Activate.ps1"

# 4. Update the output tree
Write-Host "Updating output_tree.txt..." -ForegroundColor Cyan
cmd /c "cd ..\.. && update_tree.bat"

# 5. Run the application
& ".\$EnvDir\Scripts\python.exe" main.py
