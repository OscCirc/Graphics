using System;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition.Experimental
{
    // Experimental, explicit scheduling API. This does not replace HDRP's built-in probe scheduler.
    // Call from the main thread outside a render callback. All work uses the graphics queue.
    public sealed class SharedOctahedralProbeAtlas : IDisposable
    {
        struct Entry
        {
            public int slot;
            public uint version;
            public bool ready;
        }

        readonly ComputeShader m_Shader;
        readonly Dictionary<int, Entry> m_Entries = new Dictionary<int, Entry>();
        readonly Stack<int> m_FreeSlots = new Stack<int>();
        readonly int m_CubeKernel, m_FaceKernel, m_OctKernel;
        RenderTexture m_Cube, m_Face, m_RawOct;
        bool m_Busy, m_FaceCapture, m_Disposed;
        int m_FaceOwner, m_FaceMask;

        public RenderTexture texture { get; private set; }
        public int faceSize { get; }
        public int tileSize { get; }
        public int tilesPerRow { get; }
        public int maxMip { get; }
        public int sampleCount { get; }
        public int probeCount => m_Entries.Count;
        public bool hasSharedCubemap => m_Cube != null;

        public SharedOctahedralProbeAtlas(ComputeShader shader, int faceSize = 128,
            int tileSize = 256, int tilesPerRow = 2, int sampleCount = 256)
        {
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            if (!SystemInfo.supportsComputeShaders) throw new NotSupportedException("Compute shaders are required.");
            if (!Mathf.IsPowerOfTwo(faceSize) || faceSize < 4) throw new ArgumentOutOfRangeException(nameof(faceSize));
            if (!Mathf.IsPowerOfTwo(tileSize) || tileSize < 8) throw new ArgumentOutOfRangeException(nameof(tileSize));
            if (!Mathf.IsPowerOfTwo(tilesPerRow) || tilesPerRow < 1 || tilesPerRow > SystemInfo.maxTextureSize / tileSize)
                throw new ArgumentOutOfRangeException(nameof(tilesPerRow));
            if (sampleCount < 1 || sampleCount > 4096) throw new ArgumentOutOfRangeException(nameof(sampleCount));
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.LoadStore))
                throw new NotSupportedException("RGBAHalf random-write textures are required.");

            this.faceSize = faceSize;
            this.tileSize = tileSize;
            this.tilesPerRow = tilesPerRow;
            this.sampleCount = sampleCount;
            // Every published mip has at least a 2x2 interior and a one-texel border on each side.
            maxMip = (int)Mathf.Log(tileSize, 2) - 2;
            m_Shader = UnityEngine.Object.Instantiate(shader); // Parameters must not leak between atlas instances.
            m_CubeKernel = m_Shader.FindKernel("FilterCube");
            m_FaceKernel = m_Shader.FindKernel("CopyFace");
            m_OctKernel = m_Shader.FindKernel("FilterOct");
            texture = Allocate(tileSize * tilesPerRow, TextureDimension.Tex2D, true, true, "Shared Oct probe atlas");
            for (int i = tilesPerRow * tilesPerRow - 1; i >= 0; --i) m_FreeSlots.Push(i);
        }

        static RenderTexture Allocate(int size, TextureDimension dimension, bool mips, bool randomWrite, string name, int depthBits = 0)
        {
            var descriptor = new RenderTextureDescriptor(size, size)
            {
                graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                depthBufferBits = depthBits,
                dimension = dimension,
                msaaSamples = 1,
                useMipMap = mips,
                autoGenerateMips = false,
                enableRandomWrite = randomWrite
            };
            var target = new RenderTexture(descriptor) { name = name, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            if (!target.Create())
            {
                Destroy(target);
                throw new InvalidOperationException("Could not allocate " + name);
            }
            return target;
        }

        void CheckIdle()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(SharedOctahedralProbeAtlas));
            if (m_Busy) throw new InvalidOperationException("A capture owns the shared workspace. Complete or cancel it first.");
        }

        Entry Reserve(int probeId)
        {
            if (m_Entries.TryGetValue(probeId, out var entry)) return entry;
            if (m_FreeSlots.Count == 0) throw new InvalidOperationException("The probe atlas is full. Remove a probe first.");
            entry.slot = m_FreeSlots.Pop();
            m_Entries.Add(probeId, entry);
            return entry;
        }

        void Publish(int probeId, uint version)
        {
            var entry = m_Entries[probeId];
            entry.version = version;
            entry.ready = true;
            m_Entries[probeId] = entry;
        }

        // The delegate must finish submitting all six faces before returning. No per-probe Cubemap is retained.
        public bool CaptureCubemap(int probeId, uint version, Func<RenderTexture, bool> capture)
        {
            CheckIdle();
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            var entry = Reserve(probeId);
            if (m_Cube == null) m_Cube = Allocate(faceSize, TextureDimension.Cube, false, false, "Shared probe capture cube");
            m_Busy = true;
            m_FaceCapture = false;
            try
            {
                if (!capture(m_Cube)) return false;
                Filter(m_CubeKernel, m_Cube, entry.slot);
                Publish(probeId, version);
                return true;
            }
            finally { m_Busy = false; }
        }

        public void BeginFaces(int probeId)
        {
            CheckIdle();
            Reserve(probeId);
            if (m_Face == null) m_Face = Allocate(faceSize, TextureDimension.Tex2D, false, false, "Shared probe capture face", 24);
            if (m_RawOct == null) m_RawOct = Allocate(tileSize, TextureDimension.Tex2D, false, true, "Shared unfiltered Oct workspace");
            m_FaceOwner = probeId;
            m_FaceMask = 0;
            m_Busy = true;
            m_FaceCapture = true;
        }

        // Face indices and UV orientation follow BlitCubeTextureFace.shader's faceU / faceV convention.
        public void CaptureFace(int probeId, CubemapFace face, Action<RenderTexture, CubemapFace> capture)
        {
            CheckFaceOwner(probeId);
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            if ((int)face < 0 || (int)face > 5) throw new ArgumentOutOfRangeException(nameof(face));
            capture(m_Face, face);
            m_Shader.SetInt("_Face", (int)face);
            m_Shader.SetInt("_TileSize", tileSize);
            m_Shader.SetTexture(m_FaceKernel, "_FaceTexture", m_Face);
            m_Shader.SetTexture(m_FaceKernel, "_Output", m_RawOct);
            m_Shader.Dispatch(m_FaceKernel, (tileSize + 7) / 8, (tileSize + 7) / 8, 1);
            m_FaceMask |= 1 << (int)face;
        }

        public void CompleteFaces(int probeId, uint version)
        {
            CheckFaceOwner(probeId);
            if (m_FaceMask != 63) throw new InvalidOperationException("All six faces must be captured before prefiltering and publication.");
            try
            {
                Filter(m_OctKernel, m_RawOct, m_Entries[probeId].slot);
                Publish(probeId, version);
            }
            finally { m_Busy = false; }
        }

        public void CancelFaces(int probeId)
        {
            CheckFaceOwner(probeId);
            m_Busy = false;
            m_FaceMask = 0;
        }

        void CheckFaceOwner(int probeId)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(SharedOctahedralProbeAtlas));
            if (!m_Busy || !m_FaceCapture || m_FaceOwner != probeId) throw new InvalidOperationException("This probe does not own a face capture.");
        }

        void Filter(int kernel, Texture source, int slot)
        {
            m_Shader.SetInt("_SampleCount", sampleCount);
            m_Shader.SetInt("_RawSize", tileSize);
            m_Shader.SetTexture(kernel, kernel == m_CubeKernel ? "_CubeTexture" : "_OctTexture", source);
            for (int mip = 0; mip <= maxMip; ++mip)
            {
                int size = tileSize >> mip;
                m_Shader.SetInt("_TileSize", size);
                m_Shader.SetInts("_Origin", slot % tilesPerRow * size, slot / tilesPerRow * size);
                // Same perceptual-roughness/mip relation as Core's default IBL mapping.
                float x = (float)mip / maxMip;
                float perceptualRoughness = (1.7f - Mathf.Sqrt(2.89f - 2.8f * x)) / 1.4f;
                m_Shader.SetFloat("_Roughness", perceptualRoughness * perceptualRoughness);
                m_Shader.SetTexture(kernel, "_Output", texture, mip);
                m_Shader.Dispatch(kernel, (size + 7) / 8, (size + 7) / 8, 1);
            }
        }

        public bool TryGetProbe(int probeId, out Vector4 scaleOffset, out uint version)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(SharedOctahedralProbeAtlas));
            scaleOffset = Vector4.zero;
            version = 0;
            if (!m_Entries.TryGetValue(probeId, out var entry) || !entry.ready) return false;
            float scale = 1.0f / tilesPerRow;
            scaleOffset = new Vector4(scale, scale, entry.slot % tilesPerRow * scale, entry.slot / tilesPerRow * scale);
            version = entry.version;
            return true;
        }

        public void RemoveProbe(int probeId)
        {
            CheckIdle();
            if (m_Entries.TryGetValue(probeId, out var entry))
            {
                m_Entries.Remove(probeId);
                m_FreeSlots.Push(entry.slot);
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            Release(texture); Release(m_Cube); Release(m_Face); Release(m_RawOct);
            Destroy(m_Shader);
            texture = null;
            m_Entries.Clear();
        }

        static void Release(RenderTexture target)
        {
            if (target == null) return;
            target.Release();
            Destroy(target);
        }

        static void Destroy(UnityEngine.Object target)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
