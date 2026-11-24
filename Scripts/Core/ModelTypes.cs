using System;
using System.Collections.Generic;

namespace FireEscape.Core
{
    /// <summary>Land cover; values are ESA WorldCover class codes, the same as in the Python maps.</summary>
    public enum TerrainKind { Forest = 10, Shrub = 20, Grass = 30, Cropland = 40, BuiltUp = 50, Bare = 60, Snow = 70, Water = 80, Wetland = 90, Mangrove = 95, Moss = 100 }
    public enum RunStatus { Moving, Evacuated, Incapacitated, TimedOut }

    [Serializable]
    public struct Point
    {
        public double X, Z;
        public Point(double x, double z) { X = x; Z = z; }
        public static double Distance(Point a, Point b) { double x = a.X - b.X, z = a.Z - b.Z; return Math.Sqrt(x * x + z * z); }
        public static Point Lerp(Point a, Point b, double f) { return new Point(a.X + (b.X - a.X) * f, a.Z + (b.Z - a.Z) * f); }
    }

    [Serializable]
    public sealed class Cell
    {
        public bool Blocked, Exit;
        public double Height;
        public TerrainKind Terrain = TerrainKind.Grass;
    }

    [Serializable]
    public sealed class GridMap
    {
        public const int MaxSize = 513;
        public const double MinCellSize = .25, MaxCellSize = 50, MaxAbsHeight = 9000;
        public string Name = "";
        public int Width, Depth;
        public double CellSize;
        public Cell[] Cells;
        public Point Start;
        public List<Point> FireSources;
        public GridMap(int width, int depth, double cellSize = 1)
        {
            if (width < 2 || depth < 2 || width > MaxSize || depth > MaxSize || !ModelMath.Finite(cellSize) || cellSize < MinCellSize || cellSize > MaxCellSize)
                throw new ArgumentException("Grid dimensions must be 2.." + MaxSize + " and cell size " + MinCellSize + ".." + MaxCellSize + " metres.");
            Width = width; Depth = depth; CellSize = cellSize;
            Cells = new Cell[width * depth];
            for (int i = 0; i < Cells.Length; i++) Cells[i] = new Cell();
            FireSources = new List<Point>(); Start = Center(0);
        }
        public int Index(int x, int z) { return x < 0 || z < 0 || x >= Width || z >= Depth ? -1 : z * Width + x; }
        public Cell At(int x, int z) { int i = Index(x, z); return i < 0 ? null : Cells[i]; }
        public int CellIndex(Point p)
        {
            if (!ModelMath.Finite(p.X) || !ModelMath.Finite(p.Z) || p.X < 0 || p.Z < 0 || p.X >= Width * CellSize || p.Z >= Depth * CellSize) return -1;
            return Index((int)(p.X / CellSize), (int)(p.Z / CellSize));
        }
        public Point Center(int index) { return new Point((index % Width + .5) * CellSize, (index / Width + .5) * CellSize); }
        public bool IsWalkable(Point p) { int i = CellIndex(p); return i >= 0 && !Cells[i].Blocked; }
        public double HeightAt(Point p)
        {
            double gx = Math.Max(0, Math.Min(Width - 1, p.X / CellSize - .5));
            double gz = Math.Max(0, Math.Min(Depth - 1, p.Z / CellSize - .5));
            int x = (int)gx, z = (int)gz, xx = Math.Min(x + 1, Width - 1), zz = Math.Min(z + 1, Depth - 1);
            double fx = gx - x, fz = gz - z;
            return At(x, z).Height * (1 - fx) * (1 - fz) + At(xx, z).Height * fx * (1 - fz) + At(x, zz).Height * (1 - fx) * fz + At(xx, zz).Height * fx * fz;
        }
        public GridMap Clone()
        {
            var copy = new GridMap(Width, Depth, CellSize) { Start = Start, Name = Name };
            for (int i = 0; i < Cells.Length; i++) copy.Cells[i] = new Cell { Blocked = Cells[i].Blocked, Exit = Cells[i].Exit, Height = Cells[i].Height, Terrain = Cells[i].Terrain };
            copy.FireSources.AddRange(FireSources); return copy;
        }
        public void Validate()
        {
            if (Width < 2 || Depth < 2 || Width > MaxSize || Depth > MaxSize || Cells == null || Cells.Length != Width * Depth || !ModelMath.Finite(CellSize) || CellSize < MinCellSize || CellSize > MaxCellSize)
                throw new ArgumentException("Invalid grid dimensions or cell array.");
            foreach (Cell cell in Cells)
                if (cell == null || !ModelMath.Finite(cell.Height) || Math.Abs(cell.Height) > MaxAbsHeight || !Enum.IsDefined(typeof(TerrainKind), cell.Terrain)) throw new ArgumentException("Invalid grid cell.");
            if (!IsWalkable(Start)) throw new ArgumentException("Initial position must be on a walkable cell.");
            if (FireSources == null || FireSources.Count > 100) throw new ArgumentException("Invalid fire source list.");
            foreach (Point p in FireSources) if (CellIndex(p) < 0) throw new ArgumentException("Fire source outside map.");
        }
    }

    [Serializable]
    public sealed class ModelSettings
    {
        public double BaseSpeed = 1.35, SpeedSigma = .15, Stress = .3, Familiarity = .75, RiskAversion = 4;
        public double SmokeStrength = 1, ReactionDelay = 30, ReactionJitter = 15, DecisionNoise = .15;
        // Landscape-scale synthetic fire: radii in metres, growth in metres per second.
        public double FireStartRadius = 25, FireGrowth = .15, SmokeRadius = 100, SmokeGrowth = .2;
        public double TimeStep = .5, Horizon = 3600, SnapshotInterval = 5;
        public int Seed = 12345;
        public ModelSettings Clone() { return (ModelSettings)MemberwiseClone(); }
        public void Validate()
        {
            Check(BaseSpeed, .35, 2.5, "BaseSpeed"); Check(SpeedSigma, 0, 1, "SpeedSigma"); Check(Stress, 0, 1, "Stress");
            Check(Familiarity, 0, 1, "Familiarity"); Check(RiskAversion, 0, 30, "RiskAversion"); Check(SmokeStrength, 0, 10, "SmokeStrength");
            Check(FireGrowth, 0, 5, "FireGrowth"); Check(ReactionDelay, 0, 900, "ReactionDelay"); Check(ReactionJitter, 0, 900, "ReactionJitter");
            Check(FireStartRadius, .5, 2000, "FireStartRadius"); Check(SmokeRadius, 1, 5000, "SmokeRadius"); Check(SmokeGrowth, 0, 10, "SmokeGrowth");
            Check(DecisionNoise, 0, 2, "DecisionNoise"); Check(TimeStep, .01, 2, "TimeStep"); Check(Horizon, 1, 7200, "Horizon"); Check(SnapshotInterval, .1, 60, "SnapshotInterval");
        }
        private static void Check(double x, double low, double high, string name)
        { if (!ModelMath.Finite(x) || x < low || x > high) throw new ArgumentException(name + " must be in [" + low + ", " + high + "]."); }
    }

    [Serializable]
    public sealed class Sample
    {
        public double Time;
        public Point Position;
        public RunStatus Status;
        public double Speed, Smoke, Heat;
    }

    [Serializable]
    public sealed class RunResult
    {
        public int Seed;
        public double SampledSpeed, SampledReactionDelay;
        public RunStatus Status;
        public double EndTime, Distance;
        public List<Sample> Samples = new List<Sample>();
        public Point FinalPosition;
        public Point PositionAt(double time)
        {
            if (Samples.Count == 0) return FinalPosition;
            if (time <= Samples[0].Time) return Samples[0].Position;
            if (time >= Samples[Samples.Count - 1].Time) return Samples[Samples.Count - 1].Position;
            int lo = 0, hi = Samples.Count - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Samples[mid].Time <= time) lo = mid; else hi = mid; }
            Sample a = Samples[lo], b = Samples[hi];
            return Point.Lerp(a.Position, b.Position, (time - a.Time) / (b.Time - a.Time));
        }
    }

    public static class ModelMath
    {
        public static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        public static double Clamp(double x, double min, double max) { return Math.Max(min, Math.Min(max, x)); }
        /// <summary>Walking speed factor per land cover; same estimates as KLASE in python/sim/stvarno.py.</summary>
        public static double TerrainFactor(TerrainKind terrain)
        {
            switch (terrain)
            {
                case TerrainKind.Forest: return .7;
                case TerrainKind.Shrub: return .5;
                case TerrainKind.Grass: return .9;
                case TerrainKind.Cropland: return .8;
                case TerrainKind.BuiltUp: return 1;
                case TerrainKind.Bare: return .9;
                case TerrainKind.Snow: return .5;
                case TerrainKind.Wetland: return .3;
                case TerrainKind.Moss: return .9;
                default: return .05; // water, mangrove: such cells are blocked anyway
            }
        }
        public static double SlopeFactor(double slope) { return Clamp(Math.Exp(-3.5 * (Math.Abs(slope + .05) - .05)), .15, 1.2); }
        public static double DesiredSpeed(double sampledSpeed, double stress, TerrainKind terrain, double slope, double smoke)
        { return Clamp(sampledSpeed * TerrainFactor(terrain) * SlopeFactor(slope) * (1 + .3 * stress) * Math.Max(.15, Math.Exp(-.45 * smoke)), .12, 3.2); }
    }

    internal sealed class StableRandom
    {
        private uint state;
        public StableRandom(int seed)
        {
            // Avalanche consecutive run seeds before taking the first variate. A bare XOR
            // followed by xorshift leaves adjacent seeds strongly correlated initially.
            unchecked
            {
                state = (uint)seed + 0x9e3779b9u;
                state = (state ^ (state >> 16)) * 0x7feb352du;
                state = (state ^ (state >> 15)) * 0x846ca68bu;
                state ^= state >> 16;
            }
            if (state == 0) state = 1;
        }
        public double NextDouble()
        { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state + .5) / 4294967296.0; }
        public double Normal() { return Math.Sqrt(-2 * Math.Log(NextDouble())) * Math.Cos(2 * Math.PI * NextDouble()); }
    }
}
