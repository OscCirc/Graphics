// Compile the actual Core vertex stage and the production upload helper without an Editor.
// Unity normally defines the XR texture aliases before including Blit.hlsl.
#define SHADER_API_D3D11 1
#define SHADER_TARGET 50
#define UNITY_COMPILER_HLSL 1
#define TEXTURE2D_X(name) TEXTURE2D(name)
#define TEXTURE2D_X_FLOAT(name) TEXTURE2D_FLOAT(name)
#define SAMPLE_TEXTURE2D_X_LOD(tex, sampl, uv, lod) SAMPLE_TEXTURE2D_LOD(tex, sampl, uv, lod)
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Reflection/OctahedralProbeSource.hlsl"

Texture2D<float4> _OctProbeSource;
float4 _OctProbeSourceRegion;
float4 _OctProbeSourceParameters;

float4 Frag(Varyings input) : SV_Target
{
    return float4(SampleOctahedralProbeSource(_OctProbeSource, sampler_LinearClamp,
        input.texcoord, _OctProbeSourceRegion, _OctProbeSourceParameters.x,
        _OctProbeSourceParameters.y, _BlitMipLevel, _OctProbeSourceParameters.z), 1);
}
