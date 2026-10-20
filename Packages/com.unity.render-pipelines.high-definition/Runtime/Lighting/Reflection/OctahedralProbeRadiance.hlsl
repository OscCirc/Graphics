#ifndef OCTAHEDRAL_PROBE_RADIANCE_INCLUDED
#define OCTAHEDRAL_PROBE_RADIANCE_INCLUDED

// Raw, linear radiance: one border texel on each side, no roughness mips.
static const float3 kOctProbeFaceU[6] = {
    float3(0,0,-1), float3(0,0,1), float3(1,0,0), float3(1,0,0), float3(1,0,0), float3(-1,0,0)
};
static const float3 kOctProbeFaceV[6] = {
    float3(0,-1,0), float3(0,-1,0), float3(0,0,1), float3(0,0,-1), float3(0,-1,0), float3(0,-1,0)
};
float3 OctProbeFaceDirection(float2 uv, uint face)
{
    return normalize(kOctProbeFaceU[face] * (2 * uv.x - 1) + kOctProbeFaceV[face] * (2 * uv.y - 1)
        + cross(kOctProbeFaceV[face], kOctProbeFaceU[face]));
}
uint OctProbeDirectionFace(float3 d)
{
    float3 a = abs(d);
    if (a.x >= a.y && a.x >= a.z) return d.x >= 0 ? 0 : 1;
    if (a.y >= a.z) return d.y >= 0 ? 2 : 3;
    return d.z >= 0 ? 4 : 5;
}
float2 OctProbeDirectionFaceUV(float3 d, uint face)
{
    float3 n = cross(kOctProbeFaceV[face], kOctProbeFaceU[face]);
    return float2(dot(d, kOctProbeFaceU[face]), dot(d, kOctProbeFaceV[face])) / dot(d, n) * 0.5 + 0.5;
}
float3 OctProbeRawSample(Texture2D<float4> source, SamplerState linearClamp, float3 direction, float size)
{
    float2 uv = PackNormalOctQuadEncode(direction) * 0.5 + 0.5;
    return source.SampleLevel(linearClamp, (uv * (size - 2) + 1) / size, 0).rgb;
}
float3 OctProbeRawDirection(float2 uv, float size)
{
    uv = OctProbeRepeatUV((uv * size - 1) / (size - 2));
    return UnpackNormalOctQuadEncode(uv * 2 - 1);
}
#endif
