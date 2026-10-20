using System.Reflection;
using NUnit.Framework;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition.Tests
{
    class OctahedralProbeModeTests
    {
        GameObject m_Object;
        HDAdditionalReflectionData m_Probe;

        [SetUp]
        public void SetUp()
        {
            m_Object = new GameObject("Native Oct probe test");
            m_Probe = m_Object.AddComponent<HDAdditionalReflectionData>();
            m_Probe.mode = ProbeSettings.Mode.Realtime;
            m_Probe.realtimeMode = ProbeSettings.RealtimeMode.EveryFrame;
            m_Probe.useOctahedralRadiance = true;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(m_Object);

        void Begin() => m_Probe.AllocateOctahedralCapture(GraphicsFormat.R16G16B16A16_SFloat);
        void Faces(Vector3 position)
        {
            for (int i = 0; i < 6; ++i) m_Probe.RecordOctahedralFace((CubemapFace)i, position);
        }

        [TestCase(ProbeSettings.RealtimeMode.EveryFrame)]
        [TestCase(ProbeSettings.RealtimeMode.OnEnable)]
        [TestCase(ProbeSettings.RealtimeMode.OnDemand)]
        public void NativeModesStillUseTheExistingScheduler(ProbeSettings.RealtimeMode mode)
        {
            m_Probe.realtimeMode = mode;
            m_Probe.RequestRenderNextUpdate();
            Assert.IsTrue(m_Probe.requiresRealtimeUpdate);
            m_Probe.SetIsRendered();
            Assert.AreEqual(ProbeRenderSteps.ReflectionProbeMask, m_Probe.NextRenderSteps());
            Begin(); Faces(Vector3.zero); m_Probe.RequestOctahedralPublication();
            Assert.IsTrue(m_Probe.HasValidRenderedData());
            Assert.AreEqual(mode == ProbeSettings.RealtimeMode.EveryFrame, m_Probe.requiresRealtimeUpdate);
        }

        [Test]
        public void OnDemandStorageOptInDoesNotInventAnUpdateRequest()
        {
            m_Probe.realtimeMode = ProbeSettings.RealtimeMode.OnDemand;
            Assert.IsFalse(m_Probe.requiresRealtimeUpdate);
            m_Probe.RequestRenderNextUpdate();
            Assert.IsTrue(m_Probe.requiresRealtimeUpdate);
        }

        [Test]
        public void OnDemandReenablePreservesThePublishedResultWithoutRequestingACapture()
        {
            m_Probe.realtimeMode = ProbeSettings.RealtimeMode.OnDemand;
            m_Probe.RequestRenderNextUpdate(); m_Probe.SetIsRendered(); m_Probe.NextRenderSteps();
            Begin(); Faces(Vector3.one); m_Probe.RequestOctahedralPublication();
            Texture published = m_Probe.texture;
            m_Probe.enabled = false;
            m_Probe.enabled = true;
            Assert.AreSame(published, m_Probe.texture);
            Assert.IsTrue(m_Probe.HasValidRenderedData());
            Assert.IsFalse(m_Probe.requiresRealtimeUpdate);
        }

        [Test]
        public void DestroyAdditionalDataReleasesPublishedAndPendingTextures()
        {
            Begin(); Faces(Vector3.zero); m_Probe.RequestOctahedralPublication();
            var published = m_Probe.realtimeTexture;
            Begin(); var pending = m_Probe.pendingOctahedralRadiance;
            Object.DestroyImmediate(m_Object);
            m_Object = null;
            Assert.IsTrue(published == null);
            Assert.IsTrue(pending == null);
        }

        [Test]
        public void TimeSlicingLimitsWorkToOneStepPerFrame()
        {
            m_Probe.timeSlicing = true;
            m_Probe.SetIsRendered();
            Assert.AreEqual(ProbeRenderSteps.CubeFace0, m_Probe.NextRenderSteps());
            Assert.AreEqual(ProbeRenderSteps.None, m_Probe.NextRenderSteps());
        }

        [Test]
        public void CompletionRequiresExecutedFacesAndSchedulerPublication()
        {
            Begin(); Faces(Vector3.one);
            Assert.IsNull(m_Probe.texture);
            Assert.IsFalse(m_Probe.HasValidRenderedData());
            m_Probe.RequestOctahedralPublication();
            Assert.IsTrue(m_Probe.HasValidRenderedData());
            Assert.AreEqual(TextureDimension.Tex2D, m_Probe.texture.dimension);
            Assert.IsNull(m_Probe.pendingOctahedralRadiance);
        }

        [Test]
        public void PartialCapturePreservesPublishedTexturePositionAndVersion()
        {
            Begin(); Faces(Vector3.one); m_Probe.RequestOctahedralPublication();
            Texture old = m_Probe.texture;
            uint version = m_Probe.GetTextureHash();
            Begin(); m_Probe.RecordOctahedralFace(CubemapFace.PositiveX, Vector3.right * 10);
            m_Probe.RequestOctahedralPublication();
            Assert.AreSame(old, m_Probe.texture);
            Assert.AreEqual(Vector3.one, m_Probe.octahedralCapturePosition);
            Assert.AreEqual(version, m_Probe.GetTextureHash());
        }

        [Test]
        public void CancelledCaptureDoesNotPublishOrDestroyPreviousResult()
        {
            Begin(); Faces(Vector3.zero); m_Probe.RequestOctahedralPublication();
            Texture old = m_Probe.texture;
            Begin(); m_Probe.RecordOctahedralFace(CubemapFace.PositiveX, Vector3.zero);
            m_Probe.ReleaseOctahedralCapture();
            Assert.AreSame(old, m_Probe.texture);
            Assert.IsNull(m_Probe.pendingOctahedralRadiance);
        }

        [Test]
        public void SourceSwapPreservesTheRealtimeAtlasKey()
        {
            var key = typeof(ReflectionProbeTextureCache).GetMethod("GetTextureID", BindingFlags.Static | BindingFlags.NonPublic);
            Begin(); Faces(Vector3.zero); m_Probe.RequestOctahedralPublication();
            object before = key.Invoke(null, new object[] { m_Probe });
            Begin(); Faces(Vector3.one); m_Probe.RequestOctahedralPublication();
            Assert.AreEqual(before, key.Invoke(null, new object[] { m_Probe }));
        }

        [TestCase(ProbeSettings.Mode.Baked)]
        [TestCase(ProbeSettings.Mode.Custom)]
        public void BothAssetModesAcceptRawOctWithoutAnExternalProvider(ProbeSettings.Mode mode)
        {
            var source = new Texture2D(64, 64, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None);
            try
            {
                m_Probe.mode = mode;
                m_Probe.SetTexture(mode, source);
                Assert.IsTrue(m_Probe.hasOctahedralRadiance);
                Assert.IsTrue(m_Probe.HasValidRenderedData());
                Assert.IsFalse(m_Probe.usesExternalOctahedralCapture);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void NonSquareOrNonPowerOfTwoSourcesAreRejected()
        {
            var source = new Texture2D(63, 64, TextureFormat.RGBAHalf, false, true);
            try
            {
                m_Probe.mode = ProbeSettings.Mode.Custom;
                m_Probe.customTexture = source;
                Assert.IsFalse(m_Probe.HasValidRenderedData());
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void DisablingStorageReleasesCaptureAndRestoresCubeScheduling()
        {
            Begin(); Faces(Vector3.zero); m_Probe.RequestOctahedralPublication(); Begin();
            m_Probe.useOctahedralRadiance = false;
            Assert.IsFalse(m_Probe.usesOctahedralRealtime);
            Assert.IsNull(m_Probe.texture);
            Assert.IsNull(m_Probe.pendingOctahedralRadiance);
            Assert.IsTrue(m_Probe.requiresRealtimeUpdate);
        }

        [Test]
        public void FormatChangeRestartsAllFacesInsteadOfPublishingIncompleteData()
        {
            Begin(); m_Probe.RecordOctahedralFace(CubemapFace.PositiveX, Vector3.zero);
            m_Probe.AllocateOctahedralCapture(GraphicsFormat.R32G32B32A32_SFloat);
            m_Probe.RequestOctahedralPublication();
            Assert.IsNull(m_Probe.texture);
            Faces(Vector3.zero);
            Assert.IsNotNull(m_Probe.texture);
        }

        [Test]
        public void LegacyCubeInputsRemainValidInCustomMode()
        {
            var source = new Cubemap(32, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None);
            try
            {
                m_Probe.mode = ProbeSettings.Mode.Custom;
                m_Probe.customTexture = source;
                Assert.IsFalse(m_Probe.hasOctahedralRadiance);
                Assert.IsTrue(m_Probe.HasValidRenderedData());
            }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void NonlinearOctSourcesAreRejected()
        {
            var source = new Texture2D(64, 64, GraphicsFormat.R8G8B8A8_SRGB, TextureCreationFlags.None);
            try { Assert.IsFalse(HDProbe.IsValidRawOctTexture(source)); }
            finally { Object.DestroyImmediate(source); }
        }

        [Test]
        public void PlanarProbeRetainsItsExistingProjectionAndCapturePath()
        {
            var go = new GameObject("Planar Oct exclusion test");
            try
            {
                var planar = go.AddComponent<PlanarReflectionProbe>();
                planar.useOctahedralRadiance = true;
                Assert.IsFalse(planar.usesOctahedralRealtime);
                Assert.IsFalse(planar.hasOctahedralRadiance);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void BakedOctImporterPreservesItsLinearMiplessTwoDimensionalContract()
        {
            string folder = UnityEditor.AssetDatabase.GenerateUniqueAssetPath("Assets/OctProbeImporterTest");
            UnityEditor.AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
            var source = new Texture2D(64, 64, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None);
            try
            {
                string file = folder + "/raw.exr";
                System.IO.File.WriteAllBytes(file, source.EncodeToEXR());
                UnityEditor.AssetDatabase.ImportAsset(file);
                var importer = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath(file);
                UnityEditor.Rendering.HighDefinition.HDBakedReflectionSystem.ConfigureOctahedralImporter(importer, m_Probe);
                importer.SaveAndReimport();
                Assert.AreEqual(UnityEditor.TextureImporterShape.Texture2D, importer.textureShape);
                Assert.IsFalse(importer.sRGBTexture);
                Assert.IsFalse(importer.mipmapEnabled);
                Assert.AreEqual(TextureWrapMode.Clamp, importer.wrapMode);
                Assert.AreEqual(1, UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(file).mipmapCount);
            }
            finally
            {
                Object.DestroyImmediate(source);
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
