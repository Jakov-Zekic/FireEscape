using System;
using System.Collections.Generic;
using System.IO;

namespace FireEscape.Core
{
    public enum WaterMode { Sea, Lakes }

    /// <summary>
    /// Settings for a random map. Same meaning as Postavke in python/sim/generator.py:
    /// every share is a fraction of the map area (land cover: a fraction of the land area).
    /// </summary>
    [Serializable]
    public sealed class TerrainSettings
    {
        public int Size = 257;              // nodes per side; 2^k + 1
        public double CellSize = 10;        // metres
        public int Seed = -1;               // < 0: a new random map every time
        public double ReliefM = 400;        // height span of ridged areas, metres
        public double RidgeShare = .3;      // area with ridges and hills
        public double ValleyShare = .1;     // area cut by valleys
        public double WaterShare = .1;      // area under water
        public WaterMode Water = WaterMode.Sea;
        public double ForestShare = .3, ShrubShare = .3, BareShare = .1;  // rest of the land is grass
        public double MaxSlopeDeg = 45;     // steeper is rock (blocked)

        public TerrainSettings Clone() { return (TerrainSettings)MemberwiseClone(); }

        public void Validate()
        {
            if (Size < 3 || Size > GridMap.MaxSize) throw new ArgumentException("Size must be in [3, " + GridMap.MaxSize + "].");
            if (!ModelMath.Finite(CellSize) || CellSize < GridMap.MinCellSize || CellSize > GridMap.MaxCellSize) throw new ArgumentException("CellSize out of range.");
            if (!ModelMath.Finite(ReliefM) || ReliefM < 0 || ReliefM > 3000) throw new ArgumentException("ReliefM must be in [0, 3000].");
            Share(RidgeShare, "RidgeShare"); Share(ValleyShare, "ValleyShare"); Share(WaterShare, "WaterShare");
            Share(ForestShare, "ForestShare"); Share(ShrubShare, "ShrubShare"); Share(BareShare, "BareShare");
            if (WaterShare >= 1) throw new ArgumentException("WaterShare must be below 1 (some land must remain).");
            if (ForestShare + ShrubShare + BareShare > 1 + 1e-9) throw new ArgumentException("ForestShare + ShrubShare + BareShare must be at most 1.");
            if (!ModelMath.Finite(MaxSlopeDeg) || MaxSlopeDeg < 5 || MaxSlopeDeg > 89) throw new ArgumentException("MaxSlopeDeg must be in [5, 89].");
            if (!Enum.IsDefined(typeof(WaterMode), Water)) throw new ArgumentException("Unknown water mode.");
        }
        static void Share(double x, string name)
        { if (!ModelMath.Finite(x) || x < 0 || x > 1) throw new ArgumentException(name + " must be in [0, 1]."); }
    }

    /// <summary>Port of python/sim/generator.py. Same steps and meaning; random numbers differ from numpy.</summary>
    public static class TerrainGenerator
    {
        // Feature sizes in metres (not cells), so slopes do not depend on map resolution. Same as in Python.
        const double HillScaleM = 855, RegionEdgeM = 128, RidgeScaleM = 395, ValleyScaleM = 640, ValleySmoothM = 51, CoverScaleM = 160;

        /// <summary>New random map. usedSeed is the seed actually used (also in map.Name), to repeat a map.</summary>
        public static GridMap Generate(TerrainSettings p, out int usedSeed)
        {
            if (p == null) throw new ArgumentNullException("p");
            p.Validate();
            usedSeed = p.Seed >= 0 ? p.Seed : new Random().Next(0, 1000000);
            var rng = new Random(usedSeed);
            int n = p.Size; double h = p.CellSize;
            Func<double, double, double> cells = (metres, least) => Math.Max(metres / h, least);
            double scale = cells(HillScaleM, 2);

            // Base: gentle hills.
            double[] z = Noise.Fractal(n, .1 * p.ReliefM, rng, 5, scale);

            // Ridges on RidgeShare of the area, with a soft edge of the region.
            double[] region = Noise.Fractal(n, 1, rng, 3, scale);
            double[] soft = Mask(region, p.RidgeShare);
            Noise.Blur(soft, n, cells(RegionEdgeM, 1));
            double[] ridged = Noise.Ridged(n, 1, rng, 6, cells(RidgeScaleM, 2));
            for (int i = 0; i < z.Length; i++) z[i] += soft[i] * ridged[i] * p.ReliefM;

            // Valleys: cells near zero crossings of a noise field form winding lines; cut terrain there.
            if (p.ValleyShare > 0)
            {
                double[] v = Noise.Fractal(n, 1, rng, 3, cells(ValleyScaleM, 2));
                for (int i = 0; i < v.Length; i++) v[i] = Math.Abs(v[i]);
                double q = Quantile(v, p.ValleyShare);
                var profile = new double[v.Length];
                if (q > 0) for (int i = 0; i < v.Length; i++) profile[i] = Math.Sqrt(Math.Max(0, 1 - v[i] / q));
                Noise.Blur(profile, n, cells(ValleySmoothM, 1));
                for (int i = 0; i < z.Length; i++) z[i] -= profile[i] * .25 * p.ReliefM;
            }

            // Water: the lowest WaterShare of cells. For sea, terrain first rises away from a random edge.
            if (p.Water == WaterMode.Sea && p.WaterShare > 0)
            {
                double angle = rng.Next(4) * Math.PI / 2, slope = .4 * (p.ReliefM + 50) / ((n - 1) * h), mid = (n - 1) * h / 2;
                for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                    z[y * n + x] += slope * ((x * h - mid) * Math.Cos(angle) + (y * h - mid) * Math.Sin(angle));
            }
            var water = new bool[z.Length];
            double level = Min(z);
            if (p.WaterShare > 0)
            {
                level = Quantile(z, p.WaterShare);
                for (int i = 0; i < z.Length; i++) if (z[i] <= level) { water[i] = true; z[i] = level; }
            }
            for (int i = 0; i < z.Length; i++) z[i] -= level;   // water surface at 0 m, as on real maps

            byte[] cover = Cover(z, water, p, rng, n, cells(CoverScaleM, 1));
            GridMap map = TerrainBuilder.Build(n, n, h, z, cover, p.MaxSlopeDeg);
            map.Name = "nasumicna_" + usedSeed;
            return map;
        }

        /// <summary>Land cover: lowest land forest, then shrub (makija), then grass; the highest is bare.</summary>
        static byte[] Cover(double[] z, bool[] water, TerrainSettings p, Random rng, int n, double noiseScale)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int i = 0; i < z.Length; i++) if (!water[i]) { lo = Math.Min(lo, z[i]); hi = Math.Max(hi, z[i]); }
            double span = hi - lo > 0 ? hi - lo : 1;
            double[] noise = Noise.Fractal(n, 1, rng, 5, noiseScale);
            var land = new List<int>();
            var r = new double[z.Length];
            for (int i = 0; i < z.Length; i++) if (!water[i]) { land.Add(i); r[i] = (z[i] - lo) / span + .1 * noise[i]; }
            int[] order = land.ToArray();
            double[] keys = new double[order.Length];
            for (int k = 0; k < order.Length; k++) keys[k] = r[order[k]];
            Array.Sort(keys, order);
            var cover = new byte[z.Length];
            for (int i = 0; i < z.Length; i++) cover[i] = (byte)TerrainKind.Water;
            for (int k = 0; k < order.Length; k++)
            {
                double rank = (k + .5) / order.Length;
                TerrainKind t = TerrainKind.Grass;
                if (rank < p.ForestShare) t = TerrainKind.Forest;
                else if (rank < p.ForestShare + p.ShrubShare) t = TerrainKind.Shrub;
                if (rank >= 1 - p.BareShare) t = TerrainKind.Bare;
                cover[order[k]] = (byte)t;
            }
            return cover;
        }

        /// <summary>Measured shares on a map: water of all cells; land cover of land cells.</summary>
        public static Dictionary<string, double> Measure(GridMap map)
        {
            int water = 0, land = 0, forest = 0, shrub = 0, grass = 0, bare = 0, blockedLand = 0;
            foreach (Cell c in map.Cells)
            {
                if (c.Terrain == TerrainKind.Water) { water++; continue; }
                land++;
                if (c.Blocked) blockedLand++;
                if (c.Terrain == TerrainKind.Forest) forest++;
                else if (c.Terrain == TerrainKind.Shrub) shrub++;
                else if (c.Terrain == TerrainKind.Grass) grass++;
                else if (c.Terrain == TerrainKind.Bare) bare++;
            }
            double l = Math.Max(1, land);
            return new Dictionary<string, double> {
                { "water", (double)water / map.Cells.Length }, { "forest", forest / l }, { "shrub", shrub / l },
                { "grass", grass / l }, { "bare", bare / l }, { "blockedLand", blockedLand / l } };
        }

        static double[] Mask(double[] field, double share)
        {
            var m = new double[field.Length];
            if (share <= 0) return m;
            if (share >= 1) { for (int i = 0; i < m.Length; i++) m[i] = 1; return m; }
            double threshold = Quantile(field, 1 - share);
            for (int i = 0; i < m.Length; i++) m[i] = field[i] > threshold ? 1 : 0;
            return m;
        }

        /// <summary>Linear-interpolated quantile, as numpy.quantile.</summary>
        public static double Quantile(double[] values, double q)
        {
            var sorted = (double[])values.Clone(); Array.Sort(sorted);
            double pos = q * (sorted.Length - 1);
            int i = (int)Math.Floor(pos); int j = Math.Min(i + 1, sorted.Length - 1);
            return sorted[i] + (sorted[j] - sorted[i]) * (pos - i);
        }
        static double Min(double[] a) { double m = double.PositiveInfinity; foreach (double x in a) m = Math.Min(m, x); return m; }
    }

    /// <summary>Turns heights and land cover into a GridMap; shared by the generator and real-map import.</summary>
    public static class TerrainBuilder
    {
        /// <summary>heights and cover are row-major [z * width + x], row 0 = south (as in Python [y, x]).</summary>
        public static GridMap Build(int width, int depth, double cellSize, double[] heights, byte[] cover, double maxSlopeDeg)
        {
            if (heights.Length != width * depth || cover.Length != width * depth) throw new ArgumentException("Array size does not match the grid.");
            var map = new GridMap(width, depth, cellSize);
            double maxGrade = Math.Tan(maxSlopeDeg * Math.PI / 180);
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                {
                    int i = z * width + x;
                    // Central differences inside, one-sided at the border (as numpy.gradient).
                    double gx = (heights[z * width + Math.Min(x + 1, width - 1)] - heights[z * width + Math.Max(x - 1, 0)]) / ((Math.Min(x + 1, width - 1) - Math.Max(x - 1, 0)) * cellSize);
                    double gz = (heights[Math.Min(z + 1, depth - 1) * width + x] - heights[Math.Max(z - 1, 0) * width + x]) / ((Math.Min(z + 1, depth - 1) - Math.Max(z - 1, 0)) * cellSize);
                    TerrainKind kind = Enum.IsDefined(typeof(TerrainKind), (int)cover[i]) ? (TerrainKind)cover[i] : TerrainKind.Grass;
                    Cell c = map.Cells[i];
                    c.Height = heights[i];
                    c.Terrain = kind;
                    c.Blocked = kind == TerrainKind.Water || kind == TerrainKind.Mangrove || Math.Sqrt(gx * gx + gz * gz) > maxGrade;
                }
            return map;
        }
    }

    /// <summary>
    /// Binary map exported by python/izvoz_unity.py (little-endian):
    /// "FEMAP1" (6 bytes), int32 width, int32 depth, float32 cell size, float32 heights[width*depth], uint8 cover[width*depth].
    /// </summary>
    public static class MapFile
    {
        const string Magic = "FEMAP1";
        public static GridMap Read(byte[] data, string name, double maxSlopeDeg)
        {
            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                if (new string(reader.ReadChars(Magic.Length)) != Magic) throw new InvalidDataException("Not a FireEscape map file.");
                int width = reader.ReadInt32(), depth = reader.ReadInt32();
                double cellSize = reader.ReadSingle();
                if (width < 2 || depth < 2 || width > GridMap.MaxSize || depth > GridMap.MaxSize) throw new InvalidDataException("Map size out of range.");
                var heights = new double[width * depth];
                for (int i = 0; i < heights.Length; i++) heights[i] = reader.ReadSingle();
                byte[] cover = reader.ReadBytes(width * depth);
                if (cover.Length != width * depth) throw new InvalidDataException("Map file is truncated.");
                GridMap map = TerrainBuilder.Build(width, depth, cellSize, heights, cover, maxSlopeDeg);
                map.Name = name;
                return map;
            }
        }
    }

    /// <summary>Smooth random fields: port of fraktalni_sum / grebenasti_sum from python/sim/mapa.py.</summary>
    static class Noise
    {
        static double Normal(Random rng)
        { return Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble()); }

        static double[] SmoothNormal(int n, Random rng, double sigma)
        {
            var f = new double[n * n];
            for (int i = 0; i < f.Length; i++) f[i] = Normal(rng);
            Blur(f, n, sigma);
            double mean = 0, variance = 0;
            foreach (double x in f) mean += x;
            mean /= f.Length;
            foreach (double x in f) variance += (x - mean) * (x - mean);
            double std = Math.Sqrt(variance / f.Length);
            if (std > 0) for (int i = 0; i < f.Length; i++) f[i] /= std;
            return f;
        }

        /// <summary>Sum of smoothed random fields, each octave half the scale. Range about +-amplitude.</summary>
        public static double[] Fractal(int n, double amplitude, Random rng, int octaves, double largest)
        {
            var z = new double[n * n];
            double scale = largest, weight = 1;
            for (int o = 0; o < octaves; o++)
            {
                double[] f = SmoothNormal(n, rng, scale);
                for (int i = 0; i < z.Length; i++) z[i] += weight * f[i];
                scale /= 2; weight /= 2;
            }
            double max = 0; foreach (double x in z) max = Math.Max(max, Math.Abs(x));
            if (max > 0) for (int i = 0; i < z.Length; i++) z[i] *= amplitude / max;
            return z;
        }

        /// <summary>Sharp ridges and rounded valleys (1 - |noise|)^2, in [0, amplitude].</summary>
        public static double[] Ridged(int n, double amplitude, Random rng, int octaves, double largest)
        {
            var z = new double[n * n];
            double scale = largest, weight = 1;
            for (int o = 0; o < octaves; o++)
            {
                double[] f = SmoothNormal(n, rng, scale);
                for (int i = 0; i < z.Length; i++) { double a = 1 - Math.Min(Math.Abs(f[i]) / 2, 1); z[i] += weight * a * a; }
                scale /= 2; weight /= 2;
            }
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (double x in z) { min = Math.Min(min, x); max = Math.Max(max, x); }
            double span = max - min > 0 ? max - min : 1;
            for (int i = 0; i < z.Length; i++) z[i] = amplitude * (z[i] - min) / span;
            return z;
        }

        /// <summary>Gaussian blur with wrap-around edges, approximated by three box blurs (in place).</summary>
        public static void Blur(double[] f, int n, double sigma)
        {
            if (sigma < .5) return;
            // Box widths whose three passes match a Gaussian of this sigma (Kovesi, 2010).
            double ideal = Math.Sqrt(12 * sigma * sigma / 3 + 1);
            int wl = (int)Math.Floor(ideal); if (wl % 2 == 0) wl--;
            int wu = wl + 2;
            int m = (int)Math.Round((12 * sigma * sigma - 3 * wl * wl - 12 * wl - 9) / (-4.0 * wl - 4));
            var tmp = new double[f.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                int radius = ((pass < m ? wl : wu) - 1) / 2;
                radius = Math.Min(radius, (n - 1) / 2);
                if (radius < 1) continue;
                Box(f, tmp, n, radius, true);
                Box(tmp, f, n, radius, false);
            }
        }

        static void Box(double[] src, double[] dst, int n, int r, bool horizontal)
        {
            double norm = 1.0 / (2 * r + 1);
            for (int line = 0; line < n; line++)
            {
                double sum = 0;
                for (int k = -r; k <= r; k++) sum += src[Idx(line, Wrap(k, n), n, horizontal)];
                for (int k = 0; k < n; k++)
                {
                    dst[Idx(line, k, n, horizontal)] = sum * norm;
                    sum += src[Idx(line, Wrap(k + r + 1, n), n, horizontal)] - src[Idx(line, Wrap(k - r, n), n, horizontal)];
                }
            }
        }
        static int Wrap(int k, int n) { k %= n; return k < 0 ? k + n : k; }
        static int Idx(int line, int k, int n, bool horizontal) { return horizontal ? line * n + k : k * n + line; }
    }
}
