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
