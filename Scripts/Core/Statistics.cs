using System;
using System.Collections.Generic;

namespace FireEscape.Core
{
    public static class Statistics
    {
        /// <summary>Probability mass at a specified instant; not integrated visits or density per square metre.</summary>
        public static double[] Heatmap(GridMap map, IList<RunResult> runs, double time, bool final)
        {
            if (map == null || runs == null) throw new ArgumentNullException("map/runs");
            if (!ModelMath.Finite(time) || time < 0) throw new ArgumentException("Heatmap time must be finite and nonnegative.");
            var mass = new double[map.Cells.Length]; if (runs.Count == 0) return mass;
            foreach (RunResult run in runs)
            {
                Point p = final ? run.FinalPosition : run.PositionAt(time);
                int i = map.CellIndex(p);
                if (i < 0 || map.Cells[i].Blocked) throw new InvalidOperationException("A run position is outside the walkable map. Use the original scenario map.");
                mass[i] += 1.0 / runs.Count;
            }
            return mass;
        }
        public static double EvacuationFraction(IList<RunResult> runs)
        { if (runs.Count == 0) return 0; int n = 0; foreach (RunResult r in runs) if (r.Status == RunStatus.Evacuated) n++; return (double)n / runs.Count; }
        public static double MeanEvacuationTime(IList<RunResult> runs)
        { double sum = 0; int n = 0; foreach (RunResult r in runs) if (r.Status == RunStatus.Evacuated) { sum += r.EndTime; n++; } return n == 0 ? double.NaN : sum / n; }
        public static double[] WilsonInterval(int successes, int n)
        {
            if (n < 0 || successes < 0 || successes > n) throw new ArgumentException("Invalid binomial counts.");
            if (n == 0) return new double[] { 0, 1 };
            const double z = 1.95996398454005; double p = (double)successes / n, denominator = 1 + z * z / n;
            double center = (p + z * z / (2 * n)) / denominator;
            double radius = z * Math.Sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denominator;
            return new double[] { Math.Max(0, center - radius), Math.Min(1, center + radius) };
        }
    }
}
