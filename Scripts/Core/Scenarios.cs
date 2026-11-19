using System;

namespace FireEscape.Core
{
    public static class ScenarioFactory
    {
        public static string Name(ScenarioKind kind)
        {
            switch (kind) { case ScenarioKind.Baseline: return "01 / Osnovna evakuacija"; case ScenarioKind.FireDetour: return "02 / Požar i obilazak"; case ScenarioKind.TerrainSlope: return "03 / Teren i nagib"; default: return "04 / Zatvoreni izlazi"; }
        }
        public static GridMap Create(ScenarioKind kind)
        {
            var m = new GridMap(36, 26);
            m.Start = new Point(3.5, 13.5);
            for (int z = 0; z < m.Depth; z++) for (int x = 0; x < m.Width; x++)
                m.At(x, z).Blocked = x == 0 || x == m.Width - 1 || z == 0 || z == m.Depth - 1;
            // A broad central obstacle creates two equally plausible escape corridors.
            for (int z = 9; z <= 17; z++) for (int x = 14; x <= 21; x++) m.At(x, z).Blocked = true;
            for (int z = 5; z <= 7; z++) { m.At(35, z).Blocked = false; m.At(35, z).Exit = true; }
            for (int z = 19; z <= 21; z++) { m.At(35, z).Blocked = false; m.At(35, z).Exit = true; }
            // Scattered scenery is actual collision geometry, not decorative obstructions.
            for (int z = 3; z <= 5; z++) for (int x = 6; x <= 9; x++) m.At(x, z).Blocked = true;
            for (int z = 21; z <= 23; z++) for (int x = 8; x <= 11; x++) m.At(x, z).Blocked = true;
            if (kind == ScenarioKind.FireDetour || kind == ScenarioKind.TerrainSlope)
                m.FireSources.Add(new Point(18.5, 6.5));
            if (kind == ScenarioKind.TerrainSlope)
            {
                for (int z = 1; z < 25; z++) for (int x = 1; x < 35; x++)
                {
                    Cell c = m.At(x, z);
                    if (z >= 18 && x >= 7 && x <= 29) c.Terrain = TerrainKind.Rubble;
                    else if (z <= 8 && x >= 11 && x <= 29) c.Terrain = TerrainKind.Grass;
                    // Continuous directional ramp, rising 2.2m then remaining on a plateau.
                    c.Height = z >= 18 ? Math.Min(2.2, Math.Max(0, (x - 7) * .16)) : Math.Max(0, (z - 13) / 5.0) * Math.Min(2.2, Math.Max(0, (x - 7) * .16));
                }
                // Exit elevation follows its connected plateau.
                for (int z = 19; z <= 21; z++) m.At(35, z).Height = 2.2;
            }
            if (kind == ScenarioKind.BlockedExit)
            {
                foreach (Cell c in m.Cells) if (c.Exit) { c.Exit = false; c.Blocked = true; }
                // No fire: isolates geometric failure from exposure and allows a clean timeout test.
            }
            return m;
        }
    }

    public static class HazardField
    {
        // Synthetic surrogate fields, deliberately NOT a CFD or toxicological model.
        public static double Heat(GridMap map, ModelSettings s, Point p, double t)
        {
            double h = 0, radius = .85 + s.FireGrowth * Math.Max(0, t);
            foreach (Point source in map.FireSources)
            { double d = Point.Distance(source, p); h = Math.Max(h, Math.Exp(-d * d / (2 * radius * radius))); }
            return h;
        }
        public static double Smoke(GridMap map, ModelSettings s, Point p, double t)
        {
            double k = 0, radius = 2.5 + .045 * Math.Max(0, t), amplitude = s.SmokeStrength * (.5 + .5 * (1 - Math.Exp(-Math.Max(0, t) / 20)));
            foreach (Point source in map.FireSources)
            { double d = Point.Distance(source, p); k += amplitude * Math.Exp(-d * d / (2 * radius * radius)); }
            return Math.Min(4, k);
        }
        public static double Visibility(GridMap map, ModelSettings s, Point p, double t)
        { return Math.Min(60, 3 / (.05 + Smoke(map, s, p, t))); }
    }
}
