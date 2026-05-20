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
