Write-Host "MBR-Deep: Native God Mode Shortcut Generator" -ForegroundColor Cyan
Write-Host "============================================"

$TargetDir = "$([Environment]::GetFolderPath('Desktop'))\GodMode_PerfectLinks"

# Ensure a clean slate for the test
if (Test-Path $TargetDir) { Remove-Item $TargetDir -Recurse -Force }
New-Item -ItemType Directory -Path $TargetDir | Out-Null

$Shell = New-Object -ComObject Shell.Application

# Bind to the Virtual God Mode folder and the Physical Target folder
$GodMode = $Shell.NameSpace("shell:::{ED7BA470-8E54-465E-825C-99712043E01C}")
$TargetFolder = $Shell.NameSpace($TargetDir)

if ($null -eq $GodMode -or $null -eq $TargetFolder) {
    Write-Host "Failed to bind to Windows Shell namespaces." -ForegroundColor Red
    exit
}

Write-Host "`nTriggering native Windows Shell Copy routine item-by-item..." -ForegroundColor Yellow
Write-Host "Target: $TargetDir"

$PreviousCount = -1
$StableSeconds = 0

foreach ($Item in $GodMode.Items()) {
    # Passing no flags forces Windows to show any error dialogs that might be blocking the process!
    $TargetFolder.CopyHere($Item)
}

while ($StableSeconds -lt 3) {
    Start-Sleep -Seconds 1
    $Count = (Get-ChildItem -Path $TargetDir -Filter "*.lnk" -ErrorAction SilentlyContinue).Count
    if ($Count -eq $PreviousCount -and $Count -gt 0) { $StableSeconds++ }
    else { $StableSeconds = 0 }
    $PreviousCount = $Count
}

Write-Host "`nSuccessfully generated $Count perfect native shortcuts!" -ForegroundColor Green
Write-Host "Test them out - they should have native icons and launch seamlessly!" -ForegroundColor Green
