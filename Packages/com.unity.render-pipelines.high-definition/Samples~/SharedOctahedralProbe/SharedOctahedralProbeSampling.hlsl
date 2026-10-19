#ifndef SHARED_OCTAHEDRAL_PROBE_SAMPLING_INCLUDED
#define SHARED_OCTAHEDRAL_PROBE_SAMPLING_INCLUDED

float2 SharedOctEncode(float3 n)
{
    n /= max(dot(abs(n), 1.0), 1e-6);
    float t = saturate(-n.z);
    return (n.xy + float2(n.x >= 0 ? t : -t, n.y >= 0 ? t : -t)) * 0.5 + 0.5;
}

float2 SharedOctAtlasUV(float2 octUV, float4 scaleOffset, float atlasSize, float mip)
{
    float texel = exp2(mip) / atlasSize;
    return octUV * (scaleOffset.xy - 2.0 * texel) + scaleOffset.zw + texel;
}

float3 SampleSharedOctProbe(Texture2D<float4> atlas, SamplerState linearClamp,
    float3 direction, float lod, float4 scaleOffset, float atlasSize, float maxMip)
{
    float2 uv = saturate(SharedOctEncode(direction));
    lod = clamp(lod, 0.0, maxMip);
    float lo = floor(lod), hi = min(lo + 1.0, maxMip);
    // Each mip has its own one-texel border. Use separate coordinates before blending mips.
    float3 a = atlas.SampleLevel(linearClamp, SharedOctAtlasUV(uv, scaleOffset, atlasSize, lo), lo).rgb;
    float3 b = atlas.SampleLevel(linearClamp, SharedOctAtlasUV(uv, scaleOffset, atlasSize, hi), hi).rgb;
    return lerp(a, b, lod - lo);
}

#endif
