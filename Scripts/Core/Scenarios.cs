using System;

namespace FireEscape.Core
{
    /// <summary>Default start, fire and exits for any terrain map, so a fresh map can run at once.</summary>
    public static class ScenarioSetup
    {
        /// <summary>
        /// Start: walkable cell nearest the map centre. Fire: a walkable cell about a quarter of the
        /// map away in a random direction. Exits: walkable cells on the map edge facing away from the fire.
        /// </summary>
        public static void PlaceDefaults(GridMap map, int seed)
        {
            if (map == null) throw new ArgumentNullException("map");
            double width = map.Width * map.CellSize, depth = map.Depth * map.CellSize;
            int start = NearestWalkable(map, new Point(width / 2, depth / 2));
            if (start < 0) throw new ArgumentException("Map has no walkable cell.");
            map.Start = map.Center(start);

            var rng = new Random(seed);
            double angle = rng.NextDouble() * 2 * Math.PI, reach = .25 * Math.Min(width, depth);
            map.FireSources.Clear();
            int fire = NearestWalkable(map, new Point(map.Start.X + reach * Math.Cos(angle), map.Start.Z + reach * Math.Sin(angle)), start);
            if (fire >= 0) map.FireSources.Add(map.Center(fire));

            ClearExits(map);
            // Edge whose outward direction points most away from the fire: 0 = west, 1 = east, 2 = south, 3 = north.
            double ax = -Math.Cos(angle), az = -Math.Sin(angle);
            int edge = Math.Abs(ax) >= Math.Abs(az) ? (ax < 0 ? 0 : 1) : (az < 0 ? 2 : 3);
            if (SetEdgeExits(map, edge) == 0) SetAllEdgeExits(map);
        }

        public static void ClearExits(GridMap map) { foreach (Cell c in map.Cells) c.Exit = false; }

        /// <summary>All walkable cells on the map border become exits. Returns how many.</summary>
        public static int SetAllEdgeExits(GridMap map)
        {
            int n = 0;
            for (int edge = 0; edge < 4; edge++) n += SetEdgeExits(map, edge);
            return n;
        }

        /// <summary>Walkable cells on one border (0 = west, 1 = east, 2 = south, 3 = north) become exits.</summary>
        public static int SetEdgeExits(GridMap map, int edge)
        {
            int n = 0, count = edge < 2 ? map.Depth : map.Width;
            for (int k = 0; k < count; k++)
            {
                Cell c = edge == 0 ? map.At(0, k) : edge == 1 ? map.At(map.Width - 1, k) : edge == 2 ? map.At(k, 0) : map.At(k, map.Depth - 1);
                if (c.Blocked || c.Exit) continue;
                c.Exit = true; n++;
            }
            return n;
        }

        static int NearestWalkable(GridMap map, Point target, int avoid = -1)
        {
            int best = -1; double bestDistance = double.PositiveInfinity;
            for (int i = 0; i < map.Cells.Length; i++)
            {
                if (map.Cells[i].Blocked || i == avoid) continue;
                double d = Point.Distance(map.Center(i), target);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }
    }

    public static class HazardField
    {
        // Synthetic surrogate fields, deliberately NOT a CFD, fire-spread or toxicological model.
        // Fire is a growing disc (FireStartRadius + FireGrowth * t), smoke a wider growing plume.
        public static double Heat(GridMap map, ModelSettings s, Point p, double t)
        {
            double h = 0, radius = s.FireStartRadius + s.FireGrowth * Math.Max(0, t);
            foreach (Point source in map.FireSources)
            { double d = Point.Distance(source, p); h = Math.Max(h, Math.Exp(-d * d / (2 * radius * radius))); }
            return h;
        }
        public static double Smoke(GridMap map, ModelSettings s, Point p, double t)
        {
            double k = 0, radius = s.SmokeRadius + s.SmokeGrowth * Math.Max(0, t), amplitude = s.SmokeStrength * (.5 + .5 * (1 - Math.Exp(-Math.Max(0, t) / 120)));
            foreach (Point source in map.FireSources)
            { double d = Point.Distance(source, p); k += amplitude * Math.Exp(-d * d / (2 * radius * radius)); }
            return Math.Min(4, k);
        }
        public static double Visibility(GridMap map, ModelSettings s, Point p, double t)
        { return Math.Min(60, 3 / (.05 + Smoke(map, s, p, t))); }
    }
}
