@echo off
echo Updating output_tree.txt with the latest project structure (excluding vcpkg, env, and build folders)...

:: Temporarily hide directories we want to exclude from the tree output
if exist "vcpkg" attrib +h "vcpkg"
if exist ".git" attrib +h ".git"
if exist ".vs" attrib +h ".vs"
if exist "src\UI_Python\.env" attrib +h "src\UI_Python\.env"
if exist "src\UI_Python\build" attrib +h "src\UI_Python\build"
if exist "src\UI_Python\dist" attrib +h "src\UI_Python\dist"
if exist "src\UI_Python\__pycache__" attrib +h "src\UI_Python\__pycache__"

tree /F /A > output_tree.txt

:: Restore visibility of the directories
if exist "vcpkg" attrib -h "vcpkg"
if exist ".git" attrib -h ".git"
if exist ".vs" attrib -h ".vs"
if exist "src\UI_Python\.env" attrib -h "src\UI_Python\.env"
if exist "src\UI_Python\build" attrib -h "src\UI_Python\build"
if exist "src\UI_Python\dist" attrib -h "src\UI_Python\dist"
if exist "src\UI_Python\__pycache__" attrib -h "src\UI_Python\__pycache__"

echo Update complete!
