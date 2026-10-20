#ifndef OCTAHEDRAL_PROBE_SOURCE_INCLUDED
#define OCTAHEDRAL_PROBE_SOURCE_INCLUDED

// Source tiles use a one-texel border at every mip. Destination UV may include HDRP's wider border.
float2 OctProbeRepeatUV(float2 uv)
{
    float u = uv.x, v = uv.y;
    if (u < 0)
        return v < 0 ? float2(1 + u, 1 + v) : (v < 1 ? float2(-u, 1 - v) : float2(1 + u, v - 1));
    if (u < 1)
        return v < 0 ? float2(1 - u, -v) : (v < 1 ? uv : float2(1 - u, 2 - v));
    return v < 0 ? float2(u - 1, 1 + v) : (v < 1 ? float2(2 - u, 1 - v) : float2(u - 1, v - 1));
}

float2 OctProbeSourceUV(float2 uv, float4 region, float atlasSize, float mip)
{
    float texel = exp2(mip) / atlasSize;
    return uv * (region.xy - 2.0 * texel) + region.zw + texel;
}

float3 SampleOctahedralProbeSource(Texture2D<float4> source, SamplerState sourceSampler,
    float2 destinationUV, float4 sourceRegion, float sourceAtlasSize, float sourceMaxMip,
    float destinationMip, float destinationMaxMip)
{
    float2 uv = OctProbeRepeatUV(destinationUV);
    // Both mip chains use the Core roughness relation normalized by their respective maximum mip.
    float sourceLod = saturate(destinationMip / destinationMaxMip) * sourceMaxMip;
    float lo = floor(sourceLod), hi = min(lo + 1.0, sourceMaxMip);
    float3 a = source.SampleLevel(sourceSampler, OctProbeSourceUV(uv, sourceRegion, sourceAtlasSize, lo), lo).rgb;
    float3 b = source.SampleLevel(sourceSampler, OctProbeSourceUV(uv, sourceRegion, sourceAtlasSize, hi), hi).rgb;
    return lerp(a, b, sourceLod - lo);
}

#endif
