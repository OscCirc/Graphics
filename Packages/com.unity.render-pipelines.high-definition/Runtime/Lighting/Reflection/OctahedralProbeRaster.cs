using System;

namespace UnityEngine.Rendering.HighDefinition
{
    // Graphics-queue conversion only. Filtering continues through the stock IBL filters.
    internal sealed class OctahedralProbeRaster : IDisposable
    {
        readonly Material m_Material;
        readonly MaterialPropertyBlock m_Properties = new MaterialPropertyBlock();

        internal OctahedralProbeRaster(Shader shader)
        {
            if (shader == null) throw new InvalidOperationException("Oct radiance shader resource is missing.");
            m_Material = CoreUtils.CreateEngineMaterial(shader);
        }

        void Draw(CommandBuffer cmd, Texture source, RenderTexture target, int pass, int face = 0)
        {
            m_Properties.Clear();
            m_Properties.SetTexture(pass == 2 ? "_OctRadianceCube" : "_OctRadiance2D", source);
            m_Properties.SetFloat("_OctRadianceSize", pass == 1 ? source.width : target.width);
            m_Properties.SetInt("_OctRadianceFace", face);
            m_Properties.SetVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
            cmd.SetRenderTarget(target, 0, pass == 1 ? (CubemapFace)face : CubemapFace.Unknown);
            cmd.SetViewport(new Rect(0, 0, target.width, target.height));
            cmd.DrawProcedural(Matrix4x4.identity, m_Material, pass, MeshTopology.Triangles, 3, 1, m_Properties);
            // DrawProcedural snapshots the block; the converter must not pin an old source asset.
            m_Properties.Clear();
        }

        internal void CopyFace(CommandBuffer cmd, Texture face, RenderTexture rawOct, CubemapFace index)
            => Draw(cmd, face, rawOct, 0, (int)index);

        internal void ToCube(CommandBuffer cmd, Texture rawOct, RenderTexture cube)
        {
            for (int face = 0; face < 6; ++face) Draw(cmd, rawOct, cube, 1, face);
            cmd.GenerateMips(cube);
        }

        internal void FromCube(CommandBuffer cmd, Texture cube, RenderTexture rawOct) => Draw(cmd, cube, rawOct, 2);
        public void Dispose() => CoreUtils.Destroy(m_Material);
    }
}
