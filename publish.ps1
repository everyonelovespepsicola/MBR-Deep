# publish.ps1

Write-Host "Setting up requirements..."
python -m pip install --upgrade pip
pip install pyinstaller sv_ttk pypdfium2 pywin32 pillow

# Update the output tree
Write-Host "Updating output_tree.txt..." -ForegroundColor Cyan
cmd /c "tree /F /A > output_tree.txt"

Write-Host "Compiling main.py into an executable..."

# --noconfirm: Overwrite the output directory without asking
# --windowed: Run as a GUI app without a background console window
# --onefile: Create a single executable file instead of a directory
# --icon: Set the Windows executable icon
# --add-data: Include the fast_search.dll in the output
# --collect-all: Ensure sv_ttk tcl theme assets are completely included
pyinstaller --noconfirm --windowed --onefile `
    --icon "icon.ico" `
    --add-data "fast_search.dll;." `
    --add-data "icon.ico;." `
    --collect-all "sv_ttk" `
    --name "MBR-Deep" `
    main.py

Write-Host "Build complete! You can find your compiled application at 'dist\MBR-Deep.exe'."
