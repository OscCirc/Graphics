using System;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Layout of a prefiltered Oct probe tile with one border texel per side at every mip.</summary>
    /// <remarks>This source layout is resampled into HDRP's own atlas border and roughness-mip layout on upload.</remarks>
    public readonly struct OctahedralProbeSourceLayout
    {
        /// <summary>Width and height of the square source texture.</summary>
        public readonly int atlasSize;
        /// <summary>Width and height of the source tile, including its border.</summary>
        public readonly int tileSize;
        /// <summary>Horizontal offset of the source tile at mip zero.</summary>
        public readonly int offsetX;
        /// <summary>Vertical offset of the source tile at mip zero.</summary>
        public readonly int offsetY;
        /// <summary>Highest initialized roughness mip in the source tile.</summary>
        public readonly int maxMip;

        /// <summary>Creates and validates an aligned source layout.</summary>
        public OctahedralProbeSourceLayout(int atlasSize, int tileSize, int offsetX, int offsetY, int maxMip)
        {
            if (!IsPowerOfTwo(atlasSize) || !IsPowerOfTwo(tileSize) || tileSize < 4 || tileSize > atlasSize)
                throw new ArgumentOutOfRangeException(nameof(tileSize), "Source atlas and tile sizes must be powers of two, with tile size at least four.");
            if (offsetX < 0 || offsetY < 0 || offsetX > atlasSize - tileSize || offsetY > atlasSize - tileSize ||
                offsetX % tileSize != 0 || offsetY % tileSize != 0)
                throw new ArgumentOutOfRangeException(nameof(offsetX), "The tile must fit the source atlas and be aligned to its size.");
            if (maxMip < 1 || maxMip > 28 || (tileSize >> maxMip) < 4)
                throw new ArgumentOutOfRangeException(nameof(maxMip), "At least two roughness levels are required, with a 2x2 interior at the coarsest level.");
            this.atlasSize = atlasSize;
            this.tileSize = tileSize;
            this.offsetX = offsetX;
            this.offsetY = offsetY;
            this.maxMip = maxMip;
        }

        /// <summary>Checks the layout against the supplied texture's actual dimensions and mip count.</summary>
        public void ValidateTexture(int width, int height, int mipCount)
        {
            // Validate default(struct) as well as layouts built through the constructor.
            _ = new OctahedralProbeSourceLayout(atlasSize, tileSize, offsetX, offsetY, maxMip);
            if (width != atlasSize || height != atlasSize || mipCount <= maxMip)
                throw new ArgumentException("The source texture dimensions or initialized mip range do not match the layout.");
        }

        static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    }
}
