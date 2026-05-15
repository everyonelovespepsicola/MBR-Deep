param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action = 'Install'
)

$serviceName = "MBRDeepService"
$binPath = "$PSScriptRoot\src\BackendService\bin\Debug\net10.0-windows\BackendService.exe"

if (-not [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent().IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "Please run this script as an Administrator."
    exit
}

if ($Action -eq 'Install') {
    Write-Host "Installing MBR-Deep Engine as a Windows Service..." -ForegroundColor Cyan
    sc.exe create $serviceName binpath= "$binPath" start= auto
    sc.exe start $serviceName
    Write-Host "Service installed and running in the background! (Session 0)" -ForegroundColor Green
}
elseif ($Action -eq 'Uninstall') {
    Write-Host "Stopping and uninstalling MBR-Deep Engine Service..." -ForegroundColor Yellow
    sc.exe stop $serviceName
    sc.exe delete $serviceName
    Write-Host "Service completely removed." -ForegroundColor Green
}
