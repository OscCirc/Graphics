using System;
using System.Reflection;
using NUnit.Framework;

namespace UnityEngine.Rendering.HighDefinition.Tests
{
    class ExternalOctahedralProbeTests
    {
        GameObject m_Object;
        HDAdditionalReflectionData m_Probe;

        [SetUp]
        public void SetUp()
        {
            m_Object = new GameObject("External Oct probe test");
            m_Probe = m_Object.AddComponent<HDAdditionalReflectionData>();
            m_Probe.mode = ProbeSettings.Mode.Custom;
            m_Probe.customTexture = null;
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(m_Object);

        [Test]
        public void AtlasValidityDoesNotRequirePersistentSourceTexture()
        {
            m_Probe.EnableExternalOctahedralCapture();
            Assert.IsNull(m_Probe.texture);
            Assert.IsFalse(m_Probe.HasValidRenderedData());
            Assert.IsTrue(m_Probe.externalOctahedralUpdateRequested);
            m_Probe.PublishExternalOctahedralCapture(7, new Vector3(1, 2, 3));
            Assert.AreEqual(new Vector3(1, 2, 3), m_Probe.externalOctahedralCapturePosition);
            Assert.IsTrue(m_Probe.HasValidRenderedData());
            Assert.AreEqual(7u, m_Probe.GetTextureHash());
            Assert.IsFalse(m_Probe.externalOctahedralUpdateRequested);
        }

        [Test]
        public void InvalidationRequestsCaptureWithoutReadingAnySharedSource()
        {
            m_Probe.EnableExternalOctahedralCapture();
            m_Probe.PublishExternalOctahedralCapture(9, Vector3.zero);
            m_Probe.RequestExternalOctahedralCapture();
            Assert.IsTrue(m_Probe.externalOctahedralUpdateRequested);
            Assert.IsTrue(m_Probe.HasValidRenderedData());
            Assert.AreEqual(9u, m_Probe.GetTextureHash());
        }

        [Test]
        public void ChangingModeOrDisablingProviderRestoresOrdinaryTextureValidity()
        {
            m_Probe.EnableExternalOctahedralCapture();
            m_Probe.PublishExternalOctahedralCapture(1, Vector3.zero);
            m_Probe.DisableExternalOctahedralCapture();
            Assert.IsFalse(m_Probe.HasValidRenderedData());
            m_Probe.EnableExternalOctahedralCapture();
            m_Probe.PublishExternalOctahedralCapture(2, Vector3.zero);
            m_Probe.mode = ProbeSettings.Mode.Realtime;
            Assert.IsFalse(m_Probe.usesExternalOctahedralCapture);
        }

        [Test]
        public void RegistrationRequiresCustomModeAndOneProvider()
        {
            m_Probe.mode = ProbeSettings.Mode.Realtime;
            Assert.Throws<InvalidOperationException>(() => m_Probe.EnableExternalOctahedralCapture());
            m_Probe.mode = ProbeSettings.Mode.Custom;
            m_Probe.EnableExternalOctahedralCapture();
            Assert.Throws<InvalidOperationException>(() => m_Probe.EnableExternalOctahedralCapture());
        }

        [Test]
        public void CacheKeysDistinguishProbesWithoutSourceTextures()
        {
            m_Probe.EnableExternalOctahedralCapture();
            var otherObject = new GameObject("Other external Oct probe");
            try
            {
                var other = otherObject.AddComponent<HDAdditionalReflectionData>();
                other.mode = ProbeSettings.Mode.Custom;
                other.customTexture = null;
                other.EnableExternalOctahedralCapture();
                var getKey = typeof(ReflectionProbeTextureCache).GetMethod("GetTextureID", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.AreNotEqual(getKey.Invoke(null, new object[] { m_Probe }), getKey.Invoke(null, new object[] { other }));
                Assert.IsNull(m_Probe.texture);
                Assert.IsNull(other.texture);
            }
            finally { UnityEngine.Object.DestroyImmediate(otherObject); }
        }

        [Test]
        public void ExternalCaptureDoesNotReuseBakedNormalizationSH()
        {
            m_Probe.EnableExternalOctahedralCapture();
            typeof(HDProbe).GetField("m_HasValidSHForNormalization", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(m_Probe, true);
            Assert.IsFalse(m_Probe.GetSHForNormalization(out var l0l1, out var l2, out float l22));
            Assert.AreEqual(Vector4.zero, l0l1);
            Assert.AreEqual(Vector4.zero, l2);
            Assert.AreEqual(0f, l22);
        }
    }
}
