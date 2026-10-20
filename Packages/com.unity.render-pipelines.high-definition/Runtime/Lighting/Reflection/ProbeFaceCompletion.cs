using System;

namespace UnityEngine.Rendering.HighDefinition
{
    // Kept independent of Unity APIs so scheduling/publication rules can be tested outside an Editor.
    internal struct ProbeFaceCompletion
    {
        int m_Mask;
        bool m_PublishRequested;
        internal bool complete => m_Mask == 63;
        internal bool canPublish => complete && m_PublishRequested;
        internal void Record(int face)
        {
            if ((uint)face >= 6) throw new ArgumentOutOfRangeException(nameof(face));
            m_Mask |= 1 << face;
        }
        internal void RequestPublication() => m_PublishRequested = true;
        internal void InvalidateFaces(int faceMask)
        {
            if ((uint)faceMask > 63) throw new ArgumentOutOfRangeException(nameof(faceMask));
            if (faceMask == 0) return;
            m_Mask &= ~faceMask;
            m_PublishRequested = false;
        }
    }
}
