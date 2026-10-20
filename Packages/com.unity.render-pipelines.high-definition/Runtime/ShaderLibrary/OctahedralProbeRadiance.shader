Shader "Hidden/HDRP/OctahedralProbeRadiance"
{
    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Reflection/OctahedralProbeSource.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Reflection/OctahedralProbeRadiance.hlsl"
    Texture2D<float4> _OctRadiance2D;
    TextureCube<float4> _OctRadianceCube;
    float _OctRadianceSize;
    uint _OctRadianceFace;
    float4 CopyFace(Varyings input) : SV_Target
    {
        float3 d = OctProbeRawDirection(input.texcoord, _OctRadianceSize);
        clip(OctProbeDirectionFace(d) == _OctRadianceFace ? 1 : -1);
        return float4(_OctRadiance2D.SampleLevel(sampler_LinearClamp,
            OctProbeDirectionFaceUV(d, _OctRadianceFace), 0).rgb, 1);
    }
    float4 OctToCube(Varyings input) : SV_Target
    {
        return float4(OctProbeRawSample(_OctRadiance2D, sampler_LinearClamp,
            OctProbeFaceDirection(input.texcoord, _OctRadianceFace), _OctRadianceSize), 1);
    }
    float4 CubeToOct(Varyings input) : SV_Target
    {
        return float4(_OctRadianceCube.SampleLevel(sampler_LinearClamp,
            OctProbeRawDirection(input.texcoord, _OctRadianceSize), 0).rgb, 1);
    }
    ENDHLSL
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        ZWrite Off ZTest Always Blend Off Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CopyFace
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment OctToCube
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CubeToOct
            ENDHLSL
        }
    }
    Fallback Off
}
