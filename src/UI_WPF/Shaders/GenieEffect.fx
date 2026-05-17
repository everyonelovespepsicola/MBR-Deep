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
