Remove-Item -Recurse -Force build, dist, __pycache__ -ErrorAction SilentlyContinue
Remove-Item *.spec -ErrorAction SilentlyContinue

Write-Host "Cleanup complete! Build artifacts have been removed." -ForegroundColor Green
