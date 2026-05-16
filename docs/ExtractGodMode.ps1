$Shell = New-Object -ComObject Shell.Application
$GodModeFolder = $Shell.NameSpace("shell:::{ED7BA470-8E54-465E-825C-99712043E01C}")

if ($GodModeFolder) {
    $GodModeFolder.Items() | ForEach-Object {
        [PSCustomObject]@{
            Name = $_.Name
            Type = $_.Type
            Path = $_.Path
        }
    } | Export-Csv -Path "$PSScriptRoot\GodModePointers.csv" -NoTypeInformation -Encoding UTF8

    Write-Host "Saved to GodModePointers.csv!" -ForegroundColor Green
}
