Shader "Hidden/HDRP/BlitPrefilteredOctahedralProbe"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            ZWrite Off ZTest Always Blend Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma editor_sync_compilation
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
            #pragma vertex VertQuadPadding
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Reflection/OctahedralProbeSource.hlsl"

            Texture2D<float4> _OctProbeSource;
            float4 _OctProbeSourceRegion;
            float4 _OctProbeSourceParameters; // atlas size, maximum source mip, maximum destination mip

            float4 Frag(Varyings input) : SV_Target
            {
                return float4(SampleOctahedralProbeSource(_OctProbeSource, sampler_LinearClamp,
                    input.texcoord, _OctProbeSourceRegion, _OctProbeSourceParameters.x,
                    _OctProbeSourceParameters.y, _BlitMipLevel, _OctProbeSourceParameters.z), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
