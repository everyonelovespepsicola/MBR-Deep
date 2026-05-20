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

# Safely ensure the Burn effect file exists
if (-not (Test-Path "$shaderDir\BurnEffect.fx")) {
    Write-Host "BurnEffect.fx not found! Creating default..." -ForegroundColor Yellow
    $burnFxContent = @"
sampler2D Input : register(s0);
float Progress : register(c0);
float TargetX : register(c1);

float hash(float2 p) {
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

float noise(float2 p) {
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(
        lerp(hash(i + float2(0.0, 0.0)), hash(i + float2(1.0, 0.0)), f.x),
        lerp(hash(i + float2(0.0, 1.0)), hash(i + float2(1.0, 1.0)), f.x),
        f.y);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    if (Progress <= 0.0) return tex2D(Input, uv);
    if (Progress >= 1.0) return float4(0,0,0,0);

    float4 color = tex2D(Input, uv);

    // Generate fiery procedural noise
    float n = noise(uv * 20.0) * 0.5 + noise(uv * 40.0) * 0.25;

    // Calculate distance from the starting edge (bottom of the screen at the cursor's X position)
    float dist = distance(uv, float2(TargetX, 1.0));
    float burnRadius = Progress * 2.0; // Grows enough to cover the entire diagonal bounds
    float burnState = (dist + n * 0.5) - burnRadius;

    // Burned away completely
    if (burnState < 0.0) return float4(0,0,0,0);

    // The fiery leading edge
    if (burnState < 0.15) {
        float intensity = 1.0 - (burnState / 0.15);
        // Red -> Orange -> Yellow -> White based on intensity
        float3 fire = float3(1.0, intensity, intensity * intensity * 0.5);
        return float4(lerp(color.rgb, fire, intensity * 0.8), color.a);
    }
    return color;
}
"@
    Set-Content -Path "$shaderDir\BurnEffect.fx" -Value $burnFxContent
}

# Safely ensure the Beam effect file exists
if (-not (Test-Path "$shaderDir\BeamEffect.fx")) {
    Write-Host "BeamEffect.fx not found! Creating default..." -ForegroundColor Yellow
    $beamFxContent = @"
sampler2D Input : register(s0);
float Progress : register(c0);
float TargetX : register(c1);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    if (Progress <= 0.0) return tex2D(Input, uv);
    if (Progress >= 1.0) return float4(0,0,0,0);

    // Break into vertical columns
    float noise = frac(sin(dot(floor(uv.x * 200.0), 12.9898)) * 43758.5453);

    float2 shiftedUV = uv;
    float beamOffset = Progress * 2.0 * (0.5 + noise * 0.5);
    shiftedUV.y += beamOffset; // Move image up

    if (shiftedUV.y > 1.0 || shiftedUV.y < 0.0) return float4(0,0,0,0);

    float4 color = tex2D(Input, shiftedUV);

    // Fade alpha and add blue sci-fi glow
    float alpha = 1.0 - Progress;
    color.rgb += float3(0.2, 0.5, 1.0) * Progress * noise * 2.0 * color.a;
    color.a *= alpha;

    return color;
}
"@
    Set-Content -Path "$shaderDir\BeamEffect.fx" -Value $beamFxContent
}

# Overwrite the Sandstorm effect file
Write-Host "Updating SandstormEffect.fx..." -ForegroundColor Yellow
$sandstormFxContent = @"
sampler2D Input : register(s0);
float Progress : register(c0);
float TargetX : register(c1);

float rand(float2 co){
    return frac(sin(dot(co.xy ,float2(12.9898,78.233))) * 43758.5453);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    if (Progress <= 0.0) return tex2D(Input, uv);
    if (Progress >= 1.0) return float4(0,0,0,0);

    // Break into particles
    float2 cell = floor(uv * 200.0) / 200.0;
    float noise = rand(cell);

    // Swirling movement
    float angle = noise * 6.28318 + Progress * 15.0; // Spin rapidly
    float swirlRadius = Progress * 0.15 * noise;
    float2 swirlOffset = float2(cos(angle), sin(angle)) * swirlRadius;

    // Blow to the background (scale UVs outward to simulate shrinking away into the distance)
    float2 center = float2(0.5, 0.5);
    float zoom = 1.0 + Progress * 2.0;

    float2 shiftedUV = center + (uv + swirlOffset - center) * zoom;

    if (shiftedUV.x < 0.0 || shiftedUV.x > 1.0 || shiftedUV.y < 0.0 || shiftedUV.y > 1.0)
        return float4(0,0,0,0);

    float4 color = tex2D(Input, shiftedUV);

    // Fade out without the sand color
    color.a *= (1.0 - Progress);

    return color;
}
"@
Set-Content -Path "$shaderDir\SandstormEffect.fx" -Value $sandstormFxContent

# Overwrite the Explode effect file with the updated shrinking logic
Write-Host "Updating ExplodeEffect.fx..." -ForegroundColor Yellow
$explodeFxContent = @"
sampler2D Input : register(s0);
float Progress : register(c0);
float TargetX : register(c1);

float rand(float2 co){
    return frac(sin(dot(co.xy ,float2(12.9898,78.233))) * 43758.5453);
}

float noise(float2 p) {
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(
        lerp(rand(i + float2(0.0, 0.0)), rand(i + float2(1.0, 0.0)), f.x),
        lerp(rand(i + float2(0.0, 1.0)), rand(i + float2(1.0, 1.0)), f.x),
        f.y);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    if (Progress <= 0.0) return tex2D(Input, uv);
    if (Progress >= 1.0) return float4(0,0,0,0);

    // Warp the UV space to create organic, jagged shards instead of perfect squares
    float warpX = noise(uv * 20.0) * 0.08;
    float warpY = noise(uv * 20.0 + float2(5.2, 1.3)) * 0.08;
    float2 warpedUV = uv + float2(warpX, warpY);

    // Break into a shattered grid
    float gridSize = 50.0;
    float2 cell = floor(warpedUV * gridSize) / gridSize;

    // Make the pieces shrink and disappear before they get too far!
    float2 localUV = frac(warpedUV * gridSize) - 0.5;
    float pieceSize = 0.5 * (1.0 - pow(Progress, 0.5));
    if (abs(localUV.x) > pieceSize || abs(localUV.y) > pieceSize)
        return float4(0,0,0,0);

    // Calculate random velocity and scatter
    float angle = rand(cell) * 6.2831853;
    float speed = (1.0 + rand(cell + 1.0)) * 2.0;
    float2 velocity = float2(cos(angle), sin(angle)) * speed;

    // Distort the pixel
    float2 sourceUV = uv - velocity * Progress * 0.5;

    if (sourceUV.x < 0.0 || sourceUV.x > 1.0 || sourceUV.y < 0.0 || sourceUV.y > 1.0)
        return float4(0,0,0,0);

    float4 color = tex2D(Input, sourceUV);

    // Add explosive flash and fade out
    float flash = sin(Progress * 3.14159);
    color.rgb += float3(1.0, 0.6, 0.1) * flash * rand(cell) * 1.5 * color.a;
    color.a *= (1.0 - Progress);

    return color;
}
"@
Set-Content -Path "$shaderDir\ExplodeEffect.fx" -Value $explodeFxContent

# Overwrite the Plinko effect file to act like Connect Four blocks
Write-Host "Updating PlinkoEffect.fx..." -ForegroundColor Yellow
$plinkoFxContent = @"
sampler2D Input : register(s0);
float Progress : register(c0);
float TargetX : register(c1);

float rand(float2 co){
    return frac(sin(dot(co.xy ,float2(12.9898,78.233))) * 43758.5453);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    if (Progress <= 0.0) return tex2D(Input, uv);
    if (Progress >= 1.0) return float4(0,0,0,0);

    // Break the UI into large blocks (roughly the size of the App Icons)
    float columns = 16.0;
    float rows = 10.0;
    float2 cell = float2(floor(uv.x * columns) / columns, floor(uv.y * rows) / rows);

    // Calculate a random delay per block so they fall individually
    float noise = rand(cell);

    // Connect Four cascade: Random delay per cell with slight bottom-to-top weighting
    float delay = (1.0 - cell.y) * 0.3 + noise * 0.6;

    // Calculate local progress for this individual block
    float localProgress = max(0.0, (Progress - delay) / (1.0 - delay));

    if (localProgress <= 0.0) return tex2D(Input, uv);

    // Simulating gravity (straight down drop, accelerating)
    float drop = pow(localProgress, 3.0) * 1.5;

    float2 shiftedUV = uv;
    shiftedUV.y -= drop;     // Move pixel straight downwards

    if (shiftedUV.y < 0.0 || shiftedUV.y > 1.0 || shiftedUV.x < 0.0 || shiftedUV.x > 1.0) return float4(0,0,0,0);

    float4 color = tex2D(Input, shiftedUV);
    color.a *= (1.0 - localProgress); // Fade out slightly as it falls

    return color;
}
"@
Set-Content -Path "$shaderDir\PlinkoEffect.fx" -Value $plinkoFxContent

Push-Location $shaderDir
& $fxcPath /nologo /T ps_3_0 /E main /Fo GenieEffect.ps GenieEffect.fx
$res1 = $LASTEXITCODE
& $fxcPath /nologo /T ps_3_0 /E main /Fo BurnEffect.ps BurnEffect.fx
$res2 = $LASTEXITCODE
& $fxcPath /nologo /T ps_3_0 /E main /Fo BeamEffect.ps BeamEffect.fx
$res3 = $LASTEXITCODE
& $fxcPath /nologo /T ps_3_0 /E main /Fo ExplodeEffect.ps ExplodeEffect.fx
$res4 = $LASTEXITCODE
& $fxcPath /nologo /T ps_3_0 /E main /Fo SandstormEffect.ps SandstormEffect.fx
$res5 = $LASTEXITCODE
& $fxcPath /nologo /T ps_3_0 /E main /Fo PlinkoEffect.ps PlinkoEffect.fx
$res6 = $LASTEXITCODE
Pop-Location

if ($res1 -eq 0 -and $res2 -eq 0 -and $res3 -eq 0 -and $res4 -eq 0 -and $res5 -eq 0 -and $res6 -eq 0) {
    Write-Host "Shaders compiled successfully!" -ForegroundColor Green
}
else {
    Write-Host "Failed to compile shaders!" -ForegroundColor Red
    exit 1
}
