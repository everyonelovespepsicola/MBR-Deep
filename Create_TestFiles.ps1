$word = "Dr. Paul"
$extensions = @(".txt", ".xml", ".log", ".csv", ".ini", ".md")
$locations = @(
    "C:\",
    "C:\Users\WDAGUtilityAccount\Desktop",
    "C:\Users\WDAGUtilityAccount\Documents",
    "C:\Users\WDAGUtilityAccount\Downloads",
    "C:\Users\WDAGUtilityAccount\Pictures",
    "C:\Users\WDAGUtilityAccount\Music",
    "C:\Users\WDAGUtilityAccount\Videos",
    "C:\ProgramData",
    "C:\Windows\Temp",
    "C:\Temp",
    "C:\Users\WDAGUtilityAccount\AppData\Local\Temp",
    "C:\Users\Public\Documents",
    "C:\Users\Public\Downloads",
    "C:\Users\Public\Pictures",
    "C:\Users\Public\Music",
    "C:\Users\Public\Videos",
    "C:\Users\WDAGUtilityAccount\Favorites",
    "C:\Users\WDAGUtilityAccount\Contacts",
    "C:\Users\WDAGUtilityAccount\Searches",
    "C:\Users\WDAGUtilityAccount\Links"
)

New-Item -Path "C:\Temp" -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

$i = 1
foreach ($loc in $locations) {
    if (-not (Test-Path $loc)) { New-Item -Path $loc -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
    $ext = $extensions[$i % $extensions.Length]

    # 1. File with name containing the word
    Set-Content -Path (Join-Path $loc "Dr. Paul_Test_File_$i$ext") -Value "Nothing to see here." -Encoding UTF8 -ErrorAction SilentlyContinue
    # 2. File with content containing the word
    Set-Content -Path (Join-Path $loc "Hidden_Patient_Record_$i$ext") -Value "Patient notes for $word : All clear." -Encoding UTF8 -ErrorAction SilentlyContinue
    $i++
}

# Download and silently install Notepad++
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/notepad-plus-plus/notepad-plus-plus/releases/latest" -UseBasicParsing
    $asset = $release.assets | Where-Object { $_.name -match "Installer\.x64\.exe$" }
    if ($asset) {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile "C:\Temp\npp_installer.exe" -UseBasicParsing
        Start-Process -FilePath "C:\Temp\npp_installer.exe" -ArgumentList "/S" -Wait
    }
}
catch {
}
