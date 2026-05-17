sampler2D Input : register(s0);
float Progress : register(c0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // Progress goes from 0.0 (fully open) to 1.0 (sucked into taskbar)
    if (Progress <= 0.0) return tex2D(Input, uv);
    if (Progress >= 1.0) return float4(0,0,0,0);

    // The top boundary of the window drops as it sucks in
    float topY = Progress;

    // If the pixel is above the new dropping boundary, it's invisible
    if (uv.y < topY) return float4(0, 0, 0, 0);

    // Normalize Y within the new boundary (0.0 at the top, 1.0 at the bottom)
    float normalizedY = (uv.y - topY) / (1.0 - topY);

    // Calculate the width of the funnel at this specific Y coordinate
    // A cubic curve causes the width to pinch inward sharply near the bottom
    float pinchStrength = Progress * pow(normalizedY, 3.0);
    float width = 1.0 - pinchStrength;

    // Calculate the physical bounds of the funnel
    float leftBound = 0.5 - (width / 2.0);
    float rightBound = 0.5 + (width / 2.0);

    if (uv.x < leftBound || uv.x > rightBound) return float4(0, 0, 0, 0);

    // Map the distorted screen pixel back to the original UI texture coordinate
    float sourceX = (uv.x - leftBound) / width;
    float sourceY = normalizedY;

    // Add a slight alpha fade as it sucks in
    float alpha = 1.0 - (Progress * 0.5);

    return tex2D(Input, float2(sourceX, sourceY)) * alpha;
}
