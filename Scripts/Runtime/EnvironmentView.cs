using System;
using System.Collections.Generic;
using FireEscape.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace FireEscape
{
    /// <summary>
    /// Procedural visualisation only. Core positions and hazard values remain the authority;
    /// animation, billowing smoke and camera movement do not affect model results.
    /// </summary>
    public sealed class EnvironmentView : MonoBehaviour
    {
        public static bool CameraInputBlocked;
        public Camera SceneCamera { get { return sceneCamera; } }
        private const int VerticesPerCell = 4;
        private GridMap map;
        // Size of markers (person, fire, lines) relative to human scale; grows with the map so they stay visible.
        private float unit = 1;
        private float minHeight, maxHeight;
        private Transform world;
        private Transform person, leftLeg, rightLeg, leftArm, rightArm;
        private Transform fireRoot, ensembleRoot;
        private GameObject heatObject, hazardObject;
        private Mesh heatMesh, hazardMesh;
        private Color[] heatColours, hazardColours;
        private readonly List<Material> ownedMaterials = new List<Material>();
        private readonly List<Mesh> ownedMeshes = new List<Mesh>();
        private readonly List<Transform> flames = new List<Transform>();
        private readonly List<Transform> smokePuffs = new List<Transform>();
        private readonly List<Vector3> smokeOrigins = new List<Vector3>();
        private Material surfaceMaterial, overlayMaterial, lineMaterial;
        private Material tealMaterial, navyMaterial, skinMaterial, shoeMaterial;
        private Material fireMaterial, flameMaterial, smokeMaterial, greenMaterial, charcoalMaterial;
        private LineRenderer plannedLine, actualLine;
        private Camera sceneCamera;
        private OrbitCamera orbit;
        private Vector3 previousPosition;
        private bool havePreviousPosition;
        private float facingYaw;
        private ModelSettings lastHazardSettings;
        private double lastHazardTime = -100;
        private bool hazardsVisible = true;

        public void Build(GridMap newMap)
        {
            if (newMap == null) throw new ArgumentNullException("newMap");
            ClearWorld();
            map = newMap;
            float extent = (float)(Math.Max(map.Width, map.Depth) * map.CellSize);
            unit = Mathf.Max(1f, extent / 150f);
            minHeight = float.PositiveInfinity; maxHeight = float.NegativeInfinity;
            foreach (Cell c in map.Cells) { minHeight = Mathf.Min(minHeight, (float)c.Height); maxHeight = Mathf.Max(maxHeight, (float)c.Height); }
            CreateMaterials();
            world = new GameObject("FireEscape_ProceduralWorld").transform;
            world.SetParent(transform, false);
            BuildTerrain();
            BuildMarkers();
            BuildPerson();
            BuildFires();
            BuildOverlays();
            plannedLine = NewLine("PlannedRoute", new Color(.1f, .92f, 1f, .55f), .055f, world);
            actualLine = NewLine("RecordedTrajectory", new Color(.95f, .98f, .65f, 1), .09f, world);
            ensembleRoot = new GameObject("MonteCarloTrajectories").transform;
            ensembleRoot.SetParent(world, false);
            EnsureCameraAndLight();
            SetAgent(map.Start, 0, 0, RunStatus.Moving);
            ResetCamera(false);
            lastHazardTime = -100;
        }

        private void CreateMaterials()
        {
            Shader surface = Resources.Load<Shader>("FireEscape/VertexSurface");
            Shader overlay = Resources.Load<Shader>("FireEscape/VertexOverlay");
            if (surface == null) surface = Shader.Find("FireEscape/VertexSurface");
            if (overlay == null) overlay = Shader.Find("FireEscape/VertexOverlay");
            surfaceMaterial = Track(new Material(surface));
            overlayMaterial = Track(new Material(overlay));
            lineMaterial = Track(new Material(overlay));
            lineMaterial.renderQueue = 3040;
            tealMaterial = Solid(new Color(.12f, .76f, .81f), .18f);
            navyMaterial = Solid(new Color(.08f, .14f, .23f));
            skinMaterial = Solid(new Color(.83f, .61f, .43f));
            shoeMaterial = Solid(new Color(.055f, .075f, .1f));
            charcoalMaterial = Solid(new Color(.055f, .075f, .1f));
            greenMaterial = Solid(new Color(.2f, .95f, .58f), .35f);
            fireMaterial = Solid(new Color(1f, .28f, .035f), 1.4f);
            flameMaterial = Solid(new Color(1f, .72f, .12f), 1.8f);
            // Dedicated Resources shader is retained in standalone builds; no dependency
            // on Unity's runtime-discovered/possibly stripped Standard shader variants.
            smokeMaterial = Track(new Material(Resources.Load<Shader>("FireEscape/TransparentTint")));
            smokeMaterial.color = new Color(.52f, .57f, .65f, .14f);
            smokeMaterial.renderQueue = 3000;
        }

        private Material Track(Material material) { ownedMaterials.Add(material); return material; }
        private Material Solid(Color colour, float emission = 0)
        {
            Material material = Track(new Material(Resources.Load<Shader>("FireEscape/LitTint")));
            material.color = colour;
            if (emission > 0)
            {
                material.SetColor("_EmissionColor", colour * emission);
            }
            return material;
        }

        /// <summary>Muted ESA WorldCover legend colours, so the 3D map matches the Python plots.</summary>
        public static Color TerrainColor(TerrainKind kind)
        {
            switch (kind)
            {
                case TerrainKind.Forest: return new Color(.16f, .38f, .20f);
                case TerrainKind.Shrub: return new Color(.62f, .55f, .27f);
                case TerrainKind.Grass: return new Color(.66f, .69f, .38f);
                case TerrainKind.Cropland: return new Color(.70f, .55f, .66f);
                case TerrainKind.BuiltUp: return new Color(.62f, .30f, .28f);
                case TerrainKind.Bare: return new Color(.60f, .60f, .58f);
                case TerrainKind.Snow: return new Color(.92f, .93f, .95f);
                case TerrainKind.Water: return new Color(.16f, .40f, .66f);
                case TerrainKind.Wetland: return new Color(.20f, .52f, .52f);
                default: return new Color(.50f, .55f, .45f);
            }
        }

        private void BuildTerrain()
        {
            float width = (float)(map.Width * map.CellSize);
            float depth = (float)(map.Depth * map.CellSize);
            float thickness = .65f * unit;
            MakePrimitive("Foundation", PrimitiveType.Cube, world,
                new Vector3(width / 2, minHeight - thickness / 2 - .05f * unit, depth / 2), new Vector3(width + .4f * unit, thickness, depth + .4f * unit), charcoalMaterial, false);
            List<Vector3> vertices = new List<Vector3>();
            List<int> indices = new List<int>();
            List<Color> colours = new List<Color>();
            for (int z = 0; z < map.Depth; z++)
                for (int x = 0; x < map.Width; x++)
                {
                    Cell cell = map.At(x, z);
                    Color c = TerrainColor(cell.Terrain);
                    // Blocked land is a cliff or a user obstacle; water keeps its own colour.
                    if (cell.Blocked && cell.Terrain != TerrainKind.Water) c = new Color(.24f, .22f, .22f);
                    if (cell.Exit) c = new Color(.12f, .85f, .45f);
                    c.a = 1;
                    AddTile(vertices, indices, colours, x, z, .002f, c);
                }
            Mesh mesh = NewMesh("TerrainMesh", vertices, indices, colours);
            GameObject floor = MeshObject("TerrainSurface", mesh, surfaceMaterial);
            floor.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private void BuildMarkers()
        {
            // Exits are green terrain cells (often a whole map edge), so they need no objects of their own.
            Ring("StartPosition", map.Start, 2.5f, new Color(.25f, .95f, 1f), .3f);
        }

        private void BuildPerson()
        {
            person = new GameObject("SinglePerson").transform;
            person.SetParent(world, false);
            person.localScale = Vector3.one * unit;
            MakePrimitive("Torso", PrimitiveType.Capsule, person, new Vector3(0, 1.11f, 0), new Vector3(.43f, .33f, .28f), tealMaterial, false);
            MakePrimitive("Head", PrimitiveType.Sphere, person, new Vector3(0, 1.67f, 0), new Vector3(.29f, .33f, .29f), skinMaterial, false);
            MakePrimitive("Hair", PrimitiveType.Sphere, person, new Vector3(0, 1.79f, -.025f), new Vector3(.30f, .14f, .28f), navyMaterial, false);
            MakePrimitive("FaceDirection", PrimitiveType.Sphere, person, new Vector3(0, 1.67f, .145f), new Vector3(.08f, .07f, .07f), skinMaterial, false);
            MakePrimitive("Hips", PrimitiveType.Cube, person, new Vector3(0, .82f, 0), new Vector3(.31f, .2f, .23f), navyMaterial, false);
            leftLeg = Limb("LeftLeg", new Vector3(-.1f, .77f, 0), .68f, .13f, navyMaterial);
            rightLeg = Limb("RightLeg", new Vector3(.1f, .77f, 0), .68f, .13f, navyMaterial);
            leftArm = Limb("LeftArm", new Vector3(-.26f, 1.34f, 0), .57f, .11f, tealMaterial);
            rightArm = Limb("RightArm", new Vector3(.26f, 1.34f, 0), .57f, .11f, tealMaterial);
            MakePrimitive("Shoe", PrimitiveType.Cube, leftLeg, new Vector3(0, -.68f, .065f), new Vector3(.16f, .10f, .28f), shoeMaterial, false);
            MakePrimitive("Shoe", PrimitiveType.Cube, rightLeg, new Vector3(0, -.68f, .065f), new Vector3(.16f, .10f, .28f), shoeMaterial, false);
            MakePrimitive("Hand", PrimitiveType.Sphere, leftArm, new Vector3(0, -.58f, 0), Vector3.one * .12f, skinMaterial, false);
            MakePrimitive("Hand", PrimitiveType.Sphere, rightArm, new Vector3(0, -.58f, 0), Vector3.one * .12f, skinMaterial, false);
        }

        private Transform Limb(string name, Vector3 pivot, float length, float width, Material material)
        {
            Transform t = new GameObject(name).transform;
            t.SetParent(person, false); t.localPosition = pivot;
            MakePrimitive("Segment", PrimitiveType.Capsule, t, new Vector3(0, -length / 2f, 0), new Vector3(width, length / 2f, width), material, false);
            return t;
        }

        private void BuildFires()
        {
            fireRoot = new GameObject("HazardSources").transform;
            fireRoot.SetParent(world, false);
            for (int k = 0; k < map.FireSources.Count; k++)
            {
                Point p = map.FireSources[k];
                Vector3 ground = Ground(p, 0);
                // Flames live in a per-fire transform scaled like the person, so local sizes stay in "human" units.
                Transform source = new GameObject("FireSource").transform;
                source.SetParent(fireRoot, false); source.position = ground; source.localScale = Vector3.one * unit;
                for (int j = 0; j < 4; j++)
                {
                    float angle = j * Mathf.PI * .5f;
                    Vector3 local = new Vector3(Mathf.Cos(angle) * .21f, .39f + j * .08f, Mathf.Sin(angle) * .21f);
                    Transform f = MakePrimitive("Flame", PrimitiveType.Sphere, source, local,
                        new Vector3(.4f, .9f, .4f), j % 2 == 0 ? fireMaterial : flameMaterial, false).transform;
                    flames.Add(f);
                }
                Light light = new GameObject("FireLight").AddComponent<Light>();
                light.transform.SetParent(fireRoot, false);
                light.transform.position = ground + Vector3.up * 1.1f * unit;
                light.type = LightType.Point; light.range = 4f * unit; light.intensity = 1.5f;
                light.color = new Color(1, .39f, .12f); light.shadows = LightShadows.None;
                for (int j = 0; j < 5; j++)
                {
                    Transform puff = MakePrimitive("IllustrativeSmoke", PrimitiveType.Sphere, fireRoot,
                        ground + Vector3.up * (1.6f + j * .5f) * unit, Vector3.one * unit, smokeMaterial, false).transform;
                    smokePuffs.Add(puff); smokeOrigins.Add(ground + new Vector3(.1f * j, 0, .09f * j) * unit);
                }
            }
        }

        private void BuildOverlays()
        {
            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            List<Color> colours = new List<Color>();
            for (int z = 0; z < map.Depth; z++)
                for (int x = 0; x < map.Width; x++)
                    AddTile(vertices, triangles, colours, x, z, .045f, Color.clear);
            heatMesh = NewMesh("ProbabilityCells", vertices, triangles, colours);
            heatMesh.MarkDynamic(); heatColours = colours.ToArray();
            heatObject = MeshObject("ProbabilityHeatmap", heatMesh, overlayMaterial);
            heatObject.SetActive(false);
            // Offset hazard field slightly below probability overlay.
            for (int i = 0; i < vertices.Count; i++) vertices[i] -= Vector3.up * .02f * unit;
            hazardMesh = NewMesh("HazardCells", vertices, triangles, colours);
            hazardMesh.MarkDynamic(); hazardColours = colours.ToArray();
            hazardObject = MeshObject("SyntheticHazardField", hazardMesh, overlayMaterial);
        }

        public void SetAgent(Point position, double speed, double simulationTime, RunStatus status)
        {
            if (map == null || person == null) return;
            Vector3 at = Ground(position, .04f);
            Vector3 delta = at - previousPosition; delta.y = 0;
            if (havePreviousPosition && delta.sqrMagnitude > .000001f)
                facingYaw = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            person.rotation = Quaternion.Euler(0, facingYaw, 0);
            person.position = at;
            previousPosition = at; havePreviousPosition = true;
            float amplitude = status == RunStatus.Moving ? Mathf.Clamp01((float)speed / .8f) * 26f : 0;
            float gait = Mathf.Sin((float)simulationTime * 8.5f) * amplitude;
            leftLeg.localRotation = Quaternion.Euler(gait, 0, 0);
            rightLeg.localRotation = Quaternion.Euler(-gait, 0, 0);
            leftArm.localRotation = Quaternion.Euler(-gait * .7f, 0, -7);
            rightArm.localRotation = Quaternion.Euler(gait * .7f, 0, 7);
            if (status == RunStatus.Incapacitated)
            {
                person.position = at + Vector3.up * .19f * unit;
                person.rotation = Quaternion.Euler(0, person.eulerAngles.y, 85f);
            }
        }

        public void SetHazards(ModelSettings settings, double time, bool visible)
        {
            if (map == null || settings == null) return;
            hazardsVisible = visible;
            if (fireRoot != null) fireRoot.gameObject.SetActive(visible);
            if (hazardObject != null) hazardObject.SetActive(visible);
            if (!visible) return;
            for (int i = 0; i < flames.Count; i++)
            {
                float pulse = .86f + .14f * Mathf.Sin((float)time * 8f + i * 1.7f);
                flames[i].localScale = new Vector3(.35f * pulse, (.85f + .12f * (i % 4)) / pulse, .35f * pulse);
            }
            for (int i = 0; i < smokePuffs.Count; i++)
            {
                float phase = Mathf.Repeat((float)time * .23f + (i % 5) * .2f, 1f);
                smokePuffs[i].position = smokeOrigins[i] + new Vector3(phase * .5f, 1.3f + phase * 3.1f, phase * .3f) * unit;
                smokePuffs[i].localScale = new Vector3(1.0f, .85f, 1f) * (.5f + phase * 1.8f) * Mathf.Clamp((float)settings.SmokeStrength, .15f, 2f) * unit;
                smokePuffs[i].gameObject.SetActive(settings.SmokeStrength > .001);
            }
            // Sampling cadence affects display only. The core samples hazards each model step.
            if (Math.Abs(time - lastHazardTime) < .20 && SameHazard(settings, lastHazardSettings)) return;
            lastHazardTime = time; lastHazardSettings = settings.Clone();
            for (int index = 0; index < map.Cells.Length; index++)
            {
                Color c = Color.clear;
                if (!map.Cells[index].Blocked)
                {
                    Point p = map.Center(index);
                    float smoke = Mathf.Clamp01((float)HazardField.Smoke(map, settings, p, time));
                    float heat = Mathf.Clamp01((float)HazardField.Heat(map, settings, p, time));
                    c = Color.Lerp(new Color(.67f, .63f, .78f, smoke * .25f), new Color(1f, .21f, .035f, .60f), heat);
                    c.a = Mathf.Max(smoke * .25f, heat * .60f);
                }
                for (int v = 0; v < VerticesPerCell; v++) hazardColours[index * VerticesPerCell + v] = c;
            }
            hazardMesh.colors = hazardColours;
        }

        public void SetPath(IList<Point> path) { SetLine(plannedLine, path, .11f); }
        public void SetTrail(IList<Point> path) { SetLine(actualLine, path, .14f); }

        /// <summary>Fixed absolute probability scale shared with legend and image export. Zero is invisible.</summary>
        public static Color ProbabilityColor(float p)
        {
            p = Mathf.Clamp01(p);
            if (p <= 0) return Color.clear;
            Color blue = new Color(.13f, .25f, .92f, .82f);
            Color cyan = new Color(.04f, .84f, .84f, .86f);
            Color yellow = new Color(1f, .91f, .20f, .93f);
            return p <= .25f ? Color.Lerp(blue, cyan, p / .25f) : Color.Lerp(cyan, yellow, (p - .25f) / .75f);
        }

        public void SetHeatmap(double[] probabilities, bool visible)
        {
            if (heatObject == null) return;
            heatObject.SetActive(visible && probabilities != null);
            if (probabilities == null) return;
            if (probabilities.Length != map.Cells.Length) throw new ArgumentException("Heatmap length must match grid.");
            for (int index = 0; index < probabilities.Length; index++)
            {
                Color c = map.Cells[index].Blocked ? Color.clear : ProbabilityColor((float)probabilities[index]);
                for (int v = 0; v < VerticesPerCell; v++) heatColours[index * VerticesPerCell + v] = c;
            }
            heatMesh.colors = heatColours;
        }

        public void ShowEnsemble(IList<RunResult> runs, bool visible)
        {
            if (ensembleRoot == null) return;
            for (int i = ensembleRoot.childCount - 1; i >= 0; i--)
            {
                GameObject child = ensembleRoot.GetChild(i).gameObject;
                child.SetActive(false); Destroy(child);
            }
            ensembleRoot.gameObject.SetActive(visible);
            if (!visible || runs == null || runs.Count == 0) return;
            int count = Mathf.Min(80, runs.Count);
            for (int i = 0; i < count; i++)
            {
                int index = (int)((long)i * runs.Count / count);
                RunResult r = runs[index];
                Color c = r.Status == RunStatus.Evacuated ? new Color(.24f, .92f, .84f, .22f) : new Color(1f, .52f, .31f, .24f);
                LineRenderer line = NewLine("Run_" + r.Seed, c, .035f, ensembleRoot);
                List<Point> positions = new List<Point>();
                for (int j = 0; j < r.Samples.Count; j++) positions.Add(r.Samples[j].Position);
                SetLine(line, positions, .08f);
            }
        }

        public void ResetCamera(bool topDown)
        {
            if (map == null) return;
            EnsureCameraAndLight();
            orbit.ResetView((float)(map.Width * map.CellSize), (float)(map.Depth * map.CellSize), minHeight, maxHeight, topDown);
        }

        private void EnsureCameraAndLight()
        {
            if (sceneCamera == null)
            {
                sceneCamera = Camera.main;
                if (sceneCamera == null)
                {
                    sceneCamera = new GameObject("FireEscapeCamera").AddComponent<Camera>();
                    sceneCamera.tag = "MainCamera";
                    sceneCamera.gameObject.AddComponent<AudioListener>();
                }
                orbit = sceneCamera.GetComponent<OrbitCamera>();
                if (orbit == null) orbit = sceneCamera.gameObject.AddComponent<OrbitCamera>();
                sceneCamera.clearFlags = CameraClearFlags.SolidColor;
                sceneCamera.backgroundColor = new Color(.035f, .055f, .08f);
                sceneCamera.allowHDR = false;
            }
            if (GameObject.Find("FireEscapeKeyLight") == null)
            {
                Light key = new GameObject("FireEscapeKeyLight").AddComponent<Light>();
                key.type = LightType.Directional; key.intensity = 1.1f;
                key.color = new Color(.91f, .96f, 1f);
                key.transform.rotation = Quaternion.Euler(48, -35, 0);
                key.shadows = LightShadows.Soft;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(.50f, .56f, .63f);
            }
        }

        private Vector3 Ground(Point p, float lift)
        {
            return new Vector3((float)p.X, (float)map.HeightAt(p) + lift * unit, (float)p.Z);
        }

        private float CornerHeight(double x, double z)
        {
            double half = map.CellSize * .5;
            return (float)map.HeightAt(new Point(Math.Max(half, Math.Min(x, map.Width * map.CellSize - half)),
                Math.Max(half, Math.Min(z, map.Depth * map.CellSize - half))));
        }

        private void AddTile(List<Vector3> vertices, List<int> indices, List<Color> colours, int x, int z, float lift, Color c)
        {
            int offset = vertices.Count;
            for (int iz = 0; iz < 2; iz++)
                for (int ix = 0; ix < 2; ix++)
                {
                    double px = (x + ix) * map.CellSize;
                    double pz = (z + iz) * map.CellSize;
                    vertices.Add(new Vector3((float)px, CornerHeight(px, pz) + lift * unit, (float)pz));
                    colours.Add(c);
                }
            indices.Add(offset); indices.Add(offset + 2); indices.Add(offset + 3);
            indices.Add(offset); indices.Add(offset + 3); indices.Add(offset + 1);
        }

        private static bool SameHazard(ModelSettings a, ModelSettings b)
        {
            return b != null && a.SmokeStrength == b.SmokeStrength && a.FireGrowth == b.FireGrowth && a.FireStartRadius == b.FireStartRadius
                && a.SmokeRadius == b.SmokeRadius && a.SmokeGrowth == b.SmokeGrowth;
        }

        private Mesh NewMesh(string name, List<Vector3> vertices, List<int> indices, List<Color> colours)
        {
            Mesh mesh = new Mesh(); mesh.name = name;
            if (vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0); mesh.SetColors(colours);
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); ownedMeshes.Add(mesh);
            return mesh;
        }

        private GameObject MeshObject(string name, Mesh mesh, Material material)
        {
            GameObject go = new GameObject(name); go.transform.SetParent(world, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            return go;
        }

        private GameObject MakePrimitive(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material, bool collider)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name; go.transform.SetParent(parent, false);
            go.transform.localPosition = position; go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            if (!collider)
            {
                Collider component = go.GetComponent<Collider>();
                if (component != null) { component.enabled = false; Destroy(component); }
            }
            return go;
        }

        private LineRenderer NewLine(string name, Color colour, float width, Transform parent)
        {
            GameObject go = new GameObject(name); go.transform.SetParent(parent, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = lineMaterial; line.useWorldSpace = true;
            // Lines are seen from kilometres away, so they are wider than the markers scale alone.
            line.startWidth = width * unit * 3; line.endWidth = width * unit * 3; line.startColor = colour; line.endColor = colour;
            line.numCapVertices = 3; line.numCornerVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off; line.receiveShadows = false;
            return line;
        }

        private void SetLine(LineRenderer line, IList<Point> points, float lift)
        {
            if (line == null) return;
            line.positionCount = points == null ? 0 : points.Count;
            if (points == null) return;
            for (int i = 0; i < points.Count; i++) line.SetPosition(i, Ground(points[i], lift));
        }

        private void Ring(string name, Point p, float radius, Color colour, float width)
        {
            LineRenderer line = NewLine(name, colour, width, world);
            line.loop = true; line.positionCount = 32;
            for (int i = 0; i < 32; i++)
            {
                double angle = i * Math.PI * 2 / 32;
                Point point = new Point(p.X + Math.Cos(angle) * radius * unit, p.Z + Math.Sin(angle) * radius * unit);
                line.SetPosition(i, Ground(point, .07f));
            }
        }

        private void ClearWorld()
        {
            if (world != null) { world.gameObject.SetActive(false); Destroy(world.gameObject); }
            for (int i = 0; i < ownedMaterials.Count; i++) if (ownedMaterials[i] != null) Destroy(ownedMaterials[i]);
            for (int i = 0; i < ownedMeshes.Count; i++) if (ownedMeshes[i] != null) Destroy(ownedMeshes[i]);
            ownedMaterials.Clear(); ownedMeshes.Clear(); flames.Clear(); smokePuffs.Clear(); smokeOrigins.Clear();
            havePreviousPosition = false; facingYaw = 0;
            lastHazardSettings = null;
        }

        private void OnDestroy() { ClearWorld(); CameraInputBlocked = false; }
    }
}
