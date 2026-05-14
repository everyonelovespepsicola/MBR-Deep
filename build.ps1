# Find the Visual Studio build tools batch file
$vcvars = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

if (-Not (Test-Path $vcvars)) {
    Write-Host "Error: Could not find vcvars64.bat at $vcvars" -ForegroundColor Red
    Write-Host "Please check your Visual Studio Build Tools installation." -ForegroundColor Yellow
    exit 1
}

$VCPKG_DIR = "vcpkg"
$VCPKG_EXE = "$VCPKG_DIR\vcpkg.exe"

if (-Not (Test-Path $VCPKG_DIR)) {
    Write-Host "Cloning vcpkg..." -ForegroundColor Cyan
    git clone https://github.com/microsoft/vcpkg.git $VCPKG_DIR
}

if (-Not (Test-Path $VCPKG_EXE)) {
    Write-Host "Bootstrapping vcpkg..." -ForegroundColor Cyan
    cmd.exe /c "cd `"$VCPKG_DIR`" && .\bootstrap-vcpkg.bat"
}

Write-Host "Installing libarchive via vcpkg (this may take a few minutes)..." -ForegroundColor Cyan
cmd.exe /c "cd `"$VCPKG_DIR`" && .\vcpkg.exe install libarchive:x64-windows-static"

$VCPKG_INSTALLED_DIR = "$VCPKG_DIR\installed\x64-windows-static"
$LIBARCHIVE_INCLUDE_PATH = "$VCPKG_INSTALLED_DIR\include"
$LIBARCHIVE_LIB_DIR = "$VCPKG_INSTALLED_DIR\lib"

if (-Not (Test-Path "$LIBARCHIVE_LIB_DIR\archive.lib")) {
    Write-Host "Error: Failed to build or find libarchive via vcpkg." -ForegroundColor Red
    exit 1
}

Write-Host "Found libarchive at: $LIBARCHIVE_LIB_DIR\archive.lib" -ForegroundColor Green

# Grab all the static libraries vcpkg built (zlib, lzma, bz2, etc.) so we don't miss any dependencies
$VCPKG_LIBS = (Get-ChildItem -Path $LIBARCHIVE_LIB_DIR -Filter "*.lib" | ForEach-Object { "`"$($_.FullName)`"" }) -join " "

# Add required Windows system libraries
$LINK_LIBS = "$VCPKG_LIBS Advapi32.lib Bcrypt.lib User32.lib Crypt32.lib Ws2_32.lib XmlLite.lib"

Write-Host "Compiling fast_search.c into fast_search.dll..." -ForegroundColor Cyan
# Run the batch script and the compiler in the same cmd.exe session
cmd.exe /c "`"$vcvars`" && cl.exe /LD fast_search.c /I`"$LIBARCHIVE_INCLUDE_PATH`" /link $LINK_LIBS"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build successful! fast_search.dll is ready." -ForegroundColor Green
}
else {
    Write-Host "Build failed." -ForegroundColor Red
}
