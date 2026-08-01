using Godot;
using System.Collections.Generic;
using World.HexPlanet;

namespace World.Surface
{

    /// <summary>
    /// Generates surface elevation data for hex/pentagon tiles using
    /// multi-layer 3D noise with continent masking.
    ///
    /// Technique: continent shape noise → domain warping → 
    /// ridged mountain noise → hill detail → combined output normalized to [-1, 1].
    /// Produces Earth-like land/ocean distribution (~70% ocean, 30% land).
    /// </summary>
    public class SurfaceGenerator
    {
        private readonly FastNoiseLite _continentNoise;  // broad continent shapes
        private readonly FastNoiseLite _mountainNoise;   // ridged terrain (mountains)
        private readonly FastNoiseLite _hillNoise;       // rolling hills detail
        private readonly FastNoiseLite _warpNoise;       // domain warping

        private readonly int _seed;
        private readonly float _continentScale;
        private readonly float _detailScale;

        public SurfaceGenerator(
            int seed = 42,
            float continentScale = 0.0002f,
            float detailScale = 0.0033f)
        {
            // Frequencies are per km (coordinates are km, radius 6330 = 6330km):
            //   continent 0.0002 → 5000km wavelength (Earth-like continental blocks)
            //   mountain  0.0030 → ~333km wavelength (mountain belts)
            //   hill      0.0050 → 200km wavelength (rolling terrain)
            // Earlier 0.12/0.40 produced 8km/2.8km wavelengths — far below the
            // 69km grid cell (n=96), so tile sampling aliased into random noise
            // and all structure was lost.
            _seed = seed;
            _continentScale = continentScale;
            _detailScale = detailScale;

            // ── Continent shape: very broad, single octave ──
            _continentNoise = new FastNoiseLite();
            _continentNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            _continentNoise.Frequency = continentScale;
            _continentNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;
            _continentNoise.Seed = seed;

            // ── Domain warping: distorts continent input for organic shapes ──
            _warpNoise = new FastNoiseLite();
            _warpNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            _warpNoise.Frequency = continentScale * 1.5f;
            _warpNoise.FractalType = FastNoiseLite.FractalTypeEnum.None;
            _warpNoise.Seed = seed + 100;

            // ── Mountain noise: ridged (abs of noise creates ridge profiles) ──
            _mountainNoise = new FastNoiseLite();
            _mountainNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            _mountainNoise.Frequency = detailScale * 0.9f;
            _mountainNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
            _mountainNoise.FractalOctaves = 5;
            _mountainNoise.FractalLacunarity = 2.1f;
            _mountainNoise.FractalGain = 0.45f;
            _mountainNoise.Seed = seed + 200;

            // ── Hill noise: standard FBM for rolling terrain ──
            _hillNoise = new FastNoiseLite();
            _hillNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            _hillNoise.Frequency = detailScale * 1.5f;
            _hillNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
            _hillNoise.FractalOctaves = 4;
            _hillNoise.FractalLacunarity = 2.0f;
            _hillNoise.FractalGain = 0.5f;
            _hillNoise.Seed = seed + 300;
        }

        /// <summary>
        /// Apply multi-layer elevation to a list of tiles.
        /// Output is normalized to [-1, 1].
        /// Remembers the raw min/max so arbitrary positions can be sampled consistently.
        /// </summary>
        public void ApplyElevation(List<HexTile> tiles)
        {
            // Pass 1: compute raw elevation
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            foreach (var tile in tiles)
            {
                float elevation = ComputeElevation(tile.Center);
                tile.Elevation = elevation;

                if (elevation < minVal) minVal = elevation;
                if (elevation > maxVal) maxVal = elevation;
            }

            // Remember range for consistent normalized sampling elsewhere
            MinElev = minVal;
            MaxElev = maxVal;
            _range = maxVal - minVal;

            // Pass 2: normalize to [-1, 1]
            if (_range > 0.0001f)
            {
                foreach (var tile in tiles)
                {
                    tile.Elevation = ((tile.Elevation - minVal) / _range) * 2f - 1f;
                }
            }

            // 统计：陆地比例 + 成片性（相邻 tile 同符号比例）
            int land = 0, sameSignPairs = 0, totalPairs = 0;
            foreach (var tile in tiles)
            {
                if (tile.Elevation > 0f) land++;
                foreach (int nb in tile.Neighbors)
                {
                    if (nb > tile.Id)
                    {
                        totalPairs++;
                        if ((tile.Elevation > 0f) == (tiles[nb].Elevation > 0f)) sameSignPairs++;
                    }
                }
            }
            GD.Print($"[SurfaceGenerator] land={100f * land / tiles.Count:F1}%  same-sign adjacent={100f * sameSignPairs / totalPairs:F1}%");

            GD.Print($"[SurfaceGenerator] multi-layer noise  min={minVal:F4}  max={maxVal:F4}  range={_range:F4}");
        }

        /// <summary>
        /// Raw elevation range used for normalization (valid after ApplyElevation).
        /// </summary>
        public float MinElev { get; private set; } = float.MaxValue;
        public float MaxElev { get; private set; } = float.MinValue;
        private float _range;

        /// <summary>
        /// Sample elevation at an arbitrary position, normalized to [-1, 1]
        /// using the same min/max range as the tile elevations.
        /// </summary>
        public float SampleNormalized(Vector3 pos)
        {
            float raw = ComputeElevation(pos);
            if (_range <= 0.0001f) return raw;
            return ((raw - MinElev) / _range) * 2f - 1f;
        }

        /// <summary>
        /// Computes elevation at a 3D position using multi-layer noise.
        /// Returns raw (non-normalized) elevation value.
        /// </summary>
        public float ComputeElevation(Vector3 pos)
        {
            // ── 1. Domain warp ──
            // Amplitude must be in km and comparable to the warp wavelength (3333km):
            // 400km offset organically distorts continent edges. (0.6 was 600m = no-op.)
            float warpX = _warpNoise.GetNoise3D(pos.X, pos.Y, pos.Z);
            float warpY = _warpNoise.GetNoise3D(pos.X + 50f, pos.Y + 50f, pos.Z + 50f);
            float warpZ = _warpNoise.GetNoise3D(pos.X - 50f, pos.Y - 50f, pos.Z - 50f);

            Vector3 warped = pos + new Vector3(warpX * 400f, warpY * 400f, warpZ * 400f);

            // ── 2. Continent shape ──
            float continent = _continentNoise.GetNoise3D(warped.X, warped.Y, warped.Z);

            // ── 3. Ridged mountain noise ──
            float mountainRaw = _mountainNoise.GetNoise3D(pos.X, pos.Y, pos.Z);
            // Ridged noise: 1 - |noise| creates sharp ridges, then remap to [-1, 1]
            float ridged = 1f - Mathf.Abs(mountainRaw);
            ridged = ridged * 2f - 1f; // remap [0,1] → [-1,1]
                                       // Negate so ridges are high points: -|noise| gives peaks at noise zero-crossings
            float mountain = -Mathf.Abs(mountainRaw);

            // ── 4. Hill detail ──
            float hill = _hillNoise.GetNoise3D(pos.X, pos.Y, pos.Z);

            // ── 5. Combine ──
            float elevation;

            if (continent > -0.05f)
            {
                // ── Land ──
                // Continent provides the base shape, mountains add relief, hills add texture
                float landBase = (continent + 0.05f) * 0.55f;   // 0 ~ ~1.05
                float mountainInfluence = mountain * 0.35f * Mathf.Clamp(continent + 0.5f, 0f, 1f);
                float hillInfluence = hill * 0.15f;
                elevation = landBase + mountainInfluence + hillInfluence;
            }
            else
            {
                // ── Ocean ──
                // Smoothly sloping ocean floor, deeper away from continent edges
                float oceanFloor = (continent + 0.05f) * 0.7f;  // negative, gentle slope
                float hillInfluence = hill * 0.05f;
                elevation = oceanFloor + hillInfluence;
            }

            return elevation;
        }
    }
}
