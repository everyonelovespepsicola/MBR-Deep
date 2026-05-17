Write-Host "Locating fxc.exe to compile Pixel Shaders..." -ForegroundColor Cyan

$fxcPath = (Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1).FullName

if (-not $fxcPath) {
    Write-Host "Windows SDK not found! Cannot compile shaders. (fxc.exe is missing)" -ForegroundColor Red
    exit 1
}

$shaderDir = Join-Path $PSScriptRoot "src\UI_WPF\Shaders"

# Safely ensure the directory exists
if (-not (Test-Path $shaderDir)) {
    Write-Host "Creating Shaders directory..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $shaderDir | Out-Null
}

# Safely ensure the effect file exists
if (-not (Test-Path "$shaderDir\GenieEffect.fx")) {
    Write-Host "GenieEffect.fx not found! Creating default..." -ForegroundColor Yellow
    $fxContent = @"
sampler2D Input : register(s0);
float Progress : register(c0);
float TargetX : register(c1);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    if (Progress <= 0.0) return tex2D(Input, uv);
    if (Progress >= 1.0) return float4(0,0,0,0);
    float topY = Progress;
    if (uv.y < topY) return float4(0, 0, 0, 0);
    float normalizedY = (uv.y - topY) / (1.0 - topY);
    float pinchStrength = Progress * pow(normalizedY, 3.0);
    float width = 1.0 - pinchStrength;
    float currentCenter = lerp(0.5, TargetX, pinchStrength);
    float leftBound = currentCenter - (width / 2.0);
    float rightBound = currentCenter + (width / 2.0);
    if (uv.x < leftBound || uv.x > rightBound) return float4(0, 0, 0, 0);
    return tex2D(Input, float2((uv.x - leftBound) / width, normalizedY)) * (1.0 - (Progress * 0.5));
}
"@
    Set-Content -Path "$shaderDir\GenieEffect.fx" -Value $fxContent
}

Push-Location $shaderDir
& $fxcPath /nologo /T ps_3_0 /E main /Fo GenieEffect.ps GenieEffect.fx
$res = $LASTEXITCODE
Pop-Location

if ($res -eq 0) {
    Write-Host "GenieEffect.ps compiled successfully!" -ForegroundColor Green
}
else {
    Write-Host "Failed to compile shaders!" -ForegroundColor Red
    exit 1
}
