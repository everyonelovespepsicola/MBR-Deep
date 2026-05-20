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
