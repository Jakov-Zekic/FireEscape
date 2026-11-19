using System;
using System.Collections.Generic;

namespace FireEscape.Core
{
    /// <summary>One pedestrian on a terrain graph, continuous in space and time between grid vertices.</summary>
    public sealed class Simulation
    {
        public GridMap Map { get; private set; }
        public ModelSettings Settings { get; private set; }
        public RunResult Result { get; private set; }
        public Point Position { get; private set; }
        public double Time { get; private set; }
        public double CurrentSpeed { get; private set; }
        public double Dose { get; private set; }
        public RunStatus Status { get { return Result.Status; } }
        public bool IsComplete { get { return Status != RunStatus.Moving; } }
        public List<Point> PlannedPath { get; private set; }
        private readonly double[] cellBias;
        private Point lastVertex;
        private double nextPlan;
        private int nextSnapshot = 1;
        private const double Tau = .45;
        private const double Epsilon = 1e-9;

        public Simulation(GridMap map, ModelSettings settings, int seed)
        {
            if (map == null || settings == null) throw new ArgumentNullException("map/settings");
            map.Validate(); settings.Validate();
            Map = map.Clone(); Settings = settings.Clone();
            var rng = new StableRandom(seed);
            Result = new RunResult { Seed = seed, Status = RunStatus.Moving,
                SampledSpeed = ModelMath.Clamp(settings.BaseSpeed + settings.SpeedSigma * rng.Normal(), .35, 2.5),
                SampledReactionDelay = Math.Max(0, settings.ReactionDelay * (1 + .7 * (1 - settings.Familiarity)) + settings.ReactionJitter * (2 * rng.NextDouble() - 1)) };
            cellBias = new double[Map.Cells.Length];
            double amplitude = settings.DecisionNoise * (1 + .8 * settings.Stress) * (1.5 - settings.Familiarity);
            for (int i = 0; i < cellBias.Length; i++) cellBias[i] = 1 + amplitude * rng.NextDouble();
            // Start is deliberately snapped to the selected cell centre, guaranteeing clearance.
            Position = Map.Center(Map.CellIndex(map.Start)); lastVertex = Position;
            PlannedPath = new List<Point>();
            CheckTerminal(); Record();
        }

        public RunResult RunToEnd() { while (!IsComplete) Step(); return Result; }

        public void Step()
        {
            if (IsComplete) return;
            double stepEnd = Math.Min(Settings.Horizon, Time + Settings.TimeStep);
            int guard = 0;
            while (Time < stepEnd - Epsilon && !IsComplete)
            {
                if (++guard > 1000) throw new InvalidOperationException("Integration failed to advance.");
                double snapshotTime = nextSnapshot * Settings.SnapshotInterval;
                if (snapshotTime <= Time + Epsilon) { nextSnapshot++; continue; }
                double dt = Math.Min(stepEnd - Time, snapshotTime - Time);
                bool reacting = Time < Result.SampledReactionDelay - Epsilon;
                if (reacting) dt = Math.Min(dt, Result.SampledReactionDelay - Time);
                if (!reacting)
                {
                    bool atVertex = Point.Distance(Position, lastVertex) < 1e-7;
                    if (atVertex && Time >= nextPlan - Epsilon) Replan();
                    // If the growing heat field blocks the next vertex, return along the same safe edge.
                    if (PlannedPath.Count > 0 && HazardField.Heat(Map, Settings, PlannedPath[0], Time) >= .72)
                    {
                        if (!atVertex && HazardField.Heat(Map, Settings, lastVertex, Time) < HazardField.Heat(Map, Settings, PlannedPath[0], Time))
                        { PlannedPath.Clear(); PlannedPath.Add(lastVertex); nextPlan = Time; }
                        else if (atVertex) { PlannedPath.Clear(); if (Time >= nextPlan - Epsilon) Replan(); }
                    }
                }
                bool moving = !reacting && PlannedPath.Count > 0;
                Point target = moving ? PlannedPath[0] : Position;
                double horizontal = Point.Distance(Position, target);
                if (moving && horizontal < 1e-8)
                { Position = target; lastVertex = target; PlannedPath.RemoveAt(0); CheckTerminal(); Record(); continue; }
                double slope = moving ? (Map.HeightAt(target) - Map.HeightAt(Position)) / horizontal : 0;
                double surfaceFactor = Math.Sqrt(1 + slope * slope);
                int index = Map.CellIndex(Position);
                double smoke = HazardField.Smoke(Map, Settings, Position, Time);
                double heat = HazardField.Heat(Map, Settings, Position, Time);
                double stress = ModelMath.Clamp(Settings.Stress + .35 * heat + .15 * smoke, 0, 1);
                double desired = moving ? ModelMath.DesiredSpeed(Result.SampledSpeed, stress, Map.Cells[index].Terrain, slope, smoke) : 0;
                double segmentLength = horizontal * surfaceFactor;
                if (moving && Travel(CurrentSpeed, desired, dt) >= segmentLength)
                {
                    double lo = 0, hi = dt;
                    for (int j = 0; j < 36; j++) { double mid = (lo + hi) * .5; if (Travel(CurrentSpeed, desired, mid) < segmentLength) lo = mid; else hi = mid; }
                    dt = hi;
                }
                double doseRate = .015 * smoke + .10 * Math.Max(0, heat - .35);
                if (doseRate > 0 && Dose + doseRate * dt >= 1) dt = Math.Max(0, (1 - Dose) / doseRate);
                double distance = moving ? Math.Min(segmentLength, Travel(CurrentSpeed, desired, dt)) : 0;
                CurrentSpeed = desired + (CurrentSpeed - desired) * Math.Exp(-dt / Tau);
                if (moving) Position = Point.Lerp(Position, target, Math.Min(1, distance / segmentLength));
                Result.Distance += distance; Dose += doseRate * dt; Time += dt;
                if (Math.Abs(Time - snapshotTime) < 1e-7) { Time = snapshotTime; nextSnapshot++; }
                if (moving && Point.Distance(Position, target) < 1e-7)
                { Position = target; lastVertex = target; PlannedPath.RemoveAt(0); }
                CheckTerminal(); Record();
                if (dt < Epsilon && !IsComplete) throw new InvalidOperationException("Zero integration step.");
            }
            if (!IsComplete && Time >= Settings.Horizon - Epsilon)
            { Time = Settings.Horizon; Finish(RunStatus.TimedOut); Record(); }
        }

        private static double Travel(double speed, double target, double dt)
        { return Math.Max(0, target * dt + (speed - target) * Tau * (1 - Math.Exp(-dt / Tau))); }

        private void CheckTerminal()
        {
            int i = Map.CellIndex(Position);
            // An exit represents an absorbing safe refuge; a stationary final marker remains there.
            if (i >= 0 && Map.Cells[i].Exit && Point.Distance(Position, Map.Center(i)) < 1e-7) { Finish(RunStatus.Evacuated); return; }
            if (Dose >= 1 - 1e-10 || HazardField.Heat(Map, Settings, Position, Time) >= .95) { Finish(RunStatus.Incapacitated); return; }
            if (Time >= Settings.Horizon - Epsilon) Finish(RunStatus.TimedOut);
        }
        private void Finish(RunStatus status)
        { Result.Status = status; Result.EndTime = Time; Result.FinalPosition = Position; CurrentSpeed = 0; PlannedPath.Clear(); }
        private void Record()
        {
            Result.EndTime = Time; Result.FinalPosition = Position;
            var sample = new Sample { Time = Time, Position = Position, Status = Status, Speed = CurrentSpeed,
                Smoke = HazardField.Smoke(Map, Settings, Position, Time), Heat = HazardField.Heat(Map, Settings, Position, Time) };
            int n = Result.Samples.Count;
            if (n > 0 && Math.Abs(Result.Samples[n - 1].Time - Time) < Epsilon) Result.Samples[n - 1] = sample;
            else Result.Samples.Add(sample);
        }

        private void Replan()
        {
            nextPlan = Time + .75; PlannedPath.Clear();
            int start = Map.CellIndex(Position), size = Map.Cells.Length;
            if (start < 0) return;
            bool hasExit = false;
            foreach (Cell c in Map.Cells) if (c.Exit && !c.Blocked) { hasExit = true; break; }
            if (!hasExit) return;
            var cost = new double[size]; var parent = new int[size]; var closed = new bool[size];
            var heat = new double[size]; var smoke = new double[size];
            for (int i = 0; i < size; i++)
            {
                cost[i] = double.PositiveInfinity; parent[i] = -1;
                if (!Map.Cells[i].Blocked) { Point p = Map.Center(i); heat[i] = HazardField.Heat(Map, Settings, p, Time); smoke[i] = HazardField.Smoke(Map, Settings, p, Time); }
            }
            var heap = new MinHeap(); cost[start] = 0; heap.Push(start, 0);
            int goal = -1;
            while (heap.Count > 0)
            {
                int i = heap.Pop(); if (closed[i]) continue; closed[i] = true;
                if (Map.Cells[i].Exit) { goal = i; break; }
                int x = i % Map.Width, z = i / Map.Width;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int j = Map.Index(x + dx, z + dz);
                    if (j < 0 || closed[j] || Map.Cells[j].Blocked || heat[j] >= .72) continue;
                    if (dx != 0 && dz != 0)
                    {
                        int a = Map.Index(x + dx, z), b = Map.Index(x, z + dz);
                        if (a < 0 || b < 0 || Map.Cells[a].Blocked || Map.Cells[b].Blocked || heat[a] >= .72 || heat[b] >= .72) continue;
                    }
                    Point aPoint = Map.Center(i), bPoint = Map.Center(j), midpoint = Point.Lerp(aPoint, bPoint, .5);
                    double midHeat = HazardField.Heat(Map, Settings, midpoint, Time);
                    if (midHeat >= .72) continue;
                    double horizontal = Map.CellSize * (dx != 0 && dz != 0 ? Math.Sqrt(2) : 1);
                    double grade = (Map.Cells[j].Height - Map.Cells[i].Height) / horizontal;
                    double h = Math.Max(midHeat, Math.Max(heat[i], heat[j]));
                    double k = (smoke[i] + smoke[j]) * .5;
                    double stress = ModelMath.Clamp(Settings.Stress + .35 * h + .15 * k, 0, 1);
                    // Average directional travel cost, not pure geometric shortest distance.
                    double speedA = ModelMath.DesiredSpeed(Result.SampledSpeed, stress, Map.Cells[i].Terrain, grade, k);
                    double speedB = ModelMath.DesiredSpeed(Result.SampledSpeed, stress, Map.Cells[j].Terrain, grade, k);
                    double travelTime = horizontal * Math.Sqrt(1 + grade * grade) / ((speedA + speedB) * .5);
                    double edge = travelTime * (1 + Settings.RiskAversion * (1 - .4 * stress) * (k + 3 * h)) * cellBias[j];
                    double candidate = cost[i] + edge;
                    if (candidate < cost[j]) { cost[j] = candidate; parent[j] = i; heap.Push(j, candidate); }
                }
            }
            if (goal < 0) return; // No path: remain in place and retry as the scenario evolves.
            var reverse = new List<Point>();
            while (goal != start && goal >= 0) { reverse.Add(Map.Center(goal)); goal = parent[goal]; }
            reverse.Reverse(); PlannedPath.AddRange(reverse);
        }

        private sealed class MinHeap
        {
            private struct Entry { public int Node; public double Cost; public Entry(int node, double cost) { Node = node; Cost = cost; } }
            private readonly List<Entry> data = new List<Entry>();
            public int Count { get { return data.Count; } }
            public void Push(int node, double cost)
            {
                int i = data.Count; data.Add(new Entry(node, cost));
                while (i > 0) { int p = (i - 1) / 2; if (data[p].Cost <= cost) break; data[i] = data[p]; i = p; }
                data[i] = new Entry(node, cost);
            }
            public int Pop()
            {
                int answer = data[0].Node, end = data.Count - 1; Entry last = data[end]; data.RemoveAt(end);
                if (end == 0) return answer;
                int i = 0;
                while (i * 2 + 1 < end)
                {
                    int c = i * 2 + 1; if (c + 1 < end && data[c + 1].Cost < data[c].Cost) c++;
                    if (last.Cost <= data[c].Cost) break; data[i] = data[c]; i = c;
                }
                data[i] = last; return answer;
            }
        }
    }
}
