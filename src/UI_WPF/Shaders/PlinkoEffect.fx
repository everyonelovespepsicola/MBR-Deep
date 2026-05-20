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
