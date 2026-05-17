Write-Host "Setting up MBR-Deep Backend Service Build..." -ForegroundColor Cyan

$backendDir = "src\BackendService"
$csprojPath = Join-Path $backendDir "MBR-DeepService.csproj"

# Kill any existing backend processes so we can overwrite the executable
Write-Host "Stopping any running BackendService processes..." -ForegroundColor Yellow
Stop-Process -Name "MBR-DeepService" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1 # Give the OS a second to release the file locks

# Compile and Launch
Write-Host "`nBuilding and launching the Backend Service..." -ForegroundColor Cyan
Write-Host "----------------------------------------"

dotnet build $csprojPath
Start-Process -FilePath "dotnet" -ArgumentList "run --project $csprojPath" -Verb RunAs
