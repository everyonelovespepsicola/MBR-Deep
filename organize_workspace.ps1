Write-Host "Reorganizing MBR-Deep Workspace..." -ForegroundColor Cyan

# Create Directories
New-Item -ItemType Directory -Force -Path "src\Engine" | Out-Null
New-Item -ItemType Directory -Force -Path "src\UI_Python" | Out-Null
New-Item -ItemType Directory -Force -Path "src\UI_WPF" | Out-Null
New-Item -ItemType Directory -Force -Path "src\Service" | Out-Null
New-Item -ItemType Directory -Force -Path "docs" | Out-Null

# Move C Engine files
if (Test-Path "fast_search.c") { Move-Item -Path "fast_search.c" -Destination "src\Engine\" -Force }
if (Test-Path "fast_search.dll") { Move-Item -Path "fast_search.dll" -Destination "src\Engine\" -Force }

# Move Python legacy files
if (Test-Path "main.py") { Move-Item -Path "main.py" -Destination "src\UI_Python\" -Force }
if (Test-Path "run.ps1") { Move-Item -Path "run.ps1" -Destination "src\UI_Python\" -Force }
if (Test-Path "publish.ps1") { Move-Item -Path "publish.ps1" -Destination "src\UI_Python\" -Force }

# Move C# WPF files
if (Test-Path "App*.xaml") { Move-Item -Path "App*.xaml" -Destination "src\UI_WPF\" -Force }
if (Test-Path "App*.cs") { Move-Item -Path "App*.cs" -Destination "src\UI_WPF\" -Force }
if (Test-Path "AppDrawerXAML.csproj") { Move-Item -Path "AppDrawerXAML.csproj" -Destination "src\UI_WPF\" -Force }

# Move Docs
if (Test-Path "hook.md") { Move-Item -Path "hook.md" -Destination "docs\" -Force }
if (Test-Path "manifest01.md") { Move-Item -Path "manifest01.md" -Destination "docs\" -Force }

Write-Host "Cleanup complete! The project is now beautifully structured for the Client-Server split." -ForegroundColor Green
