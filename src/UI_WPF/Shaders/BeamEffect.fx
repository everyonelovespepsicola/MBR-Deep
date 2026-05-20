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
