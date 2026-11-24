using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using FireEscape.Core;

namespace FireEscape
{
    /// <summary>Owns live and Monte Carlo runs. Rendering never advances the mathematical model.</summary>
    public sealed class AppController : MonoBehaviour
    {
        public ModelSettings parameters = new ModelSettings();
        public TerrainSettings terrain = new TerrainSettings();
        GridMap map;
        Simulation simulation;
        EnvironmentView view;
        readonly List<RunResult> runs = new List<RunResult>();
        readonly List<Point> trail = new List<Point>();
        bool running, batching, cancelBatch, heatVisible = true, hazardsVisible = true, ensembleVisible = true;
        bool finalMap, heatDirty = true, showHelp, recorded;
        int panelTab, editTool, completedTarget, replayRun;
        string batchText = "100", seedText = "12345", message = "Spremno. Odaberi ili generiraj mapu.";
        double accumulator;
        float timeScale = 30, selectedTime = 300, visualClock;
        // Map step: 0 = real map from Resources/FireEscape/Maps, 1 = random map from TerrainSettings.
        int mapMode = 1, realIndex;
        readonly List<TextAsset> realMaps = new List<TextAsset>();
        string terrainSeedText = "";
        TerrainSettings mapTerrain;              // settings of the current generated map; null for a real map
        readonly HashSet<int> userBlocked = new HashSet<int>();
        static readonly int[] MapSizes = { 65, 129, 257 };
        Vector2 panelScroll, helpScroll;
        GUIStyle titleStyle, subtitleStyle, bodyStyle, smallStyle, sectionStyle, panelStyle;
        Texture2D panelTexture, buttonTexture, legend;
        float UiScale { get { return Mathf.Clamp(Screen.width / 1440f, 0.68f, 1.25f); } }
        float UiWidth { get { return Screen.width / UiScale; } }
        float UiHeight { get { return Screen.height / UiScale; } }
        public GridMap Map { get { return map; } }
        public Simulation CurrentSimulation { get { return simulation; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<AppController>() == null)
                new GameObject("Fire Escape • Simulation").AddComponent<AppController>();
        }
        void Awake()
        {
            Application.targetFrameRate = 60;
            view = GetComponent<EnvironmentView>();
            if (view == null) view = gameObject.AddComponent<EnvironmentView>();
            seedText = parameters.Seed.ToString(CultureInfo.InvariantCulture);
            realMaps.AddRange(Resources.LoadAll<TextAsset>("FireEscape/Maps"));
            realMaps.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            BuildMap();
            panelTab = 0;
        }
        /// <summary>Step 1: build the map (real or random), then place default start, fire and exits.</summary>
        void BuildMap()
        {
            try
            {
                GridMap next; TerrainSettings used = null; int seed;
                if (mapMode == 0)
                {
                    if (realMaps.Count == 0) throw new InvalidOperationException("Nema stvarnih mapa u Resources/FireEscape/Maps (pokreni python izvoz_unity.py).");
                    realIndex = Mathf.Clamp(realIndex, 0, realMaps.Count - 1);
                    TextAsset asset = realMaps[realIndex];
                    next = MapFile.Read(asset.bytes, asset.name, terrain.MaxSlopeDeg);
                    seed = parameters.Seed;
                }
                else
                {
                    used = terrain.Clone();
                    int fixedSeed;
                    used.Seed = int.TryParse(terrainSeedText, out fixedSeed) && fixedSeed >= 0 ? fixedSeed : -1;
                    next = TerrainGenerator.Generate(used, out seed);
                    used.Seed = seed;
                }
                ScenarioSetup.PlaceDefaults(next, seed);
                map = next; mapTerrain = used; userBlocked.Clear(); editTool = 0;
                view.Build(map); ResetRun(true); view.ResetCamera(false);
                message = "Mapa " + map.Name + " (" + map.Width + " × " + map.Depth + ", ćelija " + map.CellSize.ToString("0.#", CultureInfo.InvariantCulture)
                    + " m). Zadani početak, požar i izlazi su postavljeni; uredi ih u koraku 2.";
            }
            catch (Exception e) { message = "Mapa nije napravljena: " + e.Message; Debug.LogException(e); }
        }
        void ResetRun(bool clearResults)
        {
            running = false; recorded = false; accumulator = 0;
            if (clearResults) { runs.Clear(); replayRun = 0; }
            simulation = new Simulation(map, parameters, parameters.Seed);
            trail.Clear(); trail.Add(simulation.Position);
            view.SetAgent(simulation.Position, 0, 0, simulation.Status);
            view.SetTrail(trail); view.SetPath(simulation.PlannedPath);
            view.SetHazards(parameters, 0, hazardsVisible);
            view.ShowEnsemble(runs, ensembleVisible); heatDirty = true;
        }
        void Update()
        {
            Vector2 mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / UiScale;
            EnvironmentView.CameraInputBlocked = showHelp || map == null || mp.x < 370 || mp.y < 72 || mp.y > UiHeight - 156 || (mp.x < 670 && mp.y < 245);
            if (Input.GetKeyDown(KeyCode.Escape)) { editTool = 0; showHelp = false; }
            if (map == null || simulation == null) return;   // map step failed; the panel shows why
            if (!showHelp && !batching && Input.GetKeyDown(KeyCode.Space)) ToggleRun();
            if (!showHelp && Input.GetKeyDown(KeyCode.T)) view.ResetCamera(true);
            if (!showHelp && Input.GetKeyDown(KeyCode.V)) view.ResetCamera(false);
            if (editTool > 0 && !running && !batching && !EnvironmentView.CameraInputBlocked && Input.GetMouseButtonDown(0)) EditAtMouse();
            if (running && !batching)
            {
                accumulator += Time.unscaledDeltaTime * timeScale;
                int steps = 0;
                while (accumulator >= parameters.TimeStep && !simulation.IsComplete && steps++ < 250)
                {
                    simulation.Step(); accumulator -= parameters.TimeStep;
                    if (trail.Count == 0 || Distance(trail[trail.Count - 1], simulation.Position) > .08) trail.Add(simulation.Position);
                }
                view.SetAgent(simulation.Position, simulation.CurrentSpeed, simulation.Time, simulation.Status);
                view.SetTrail(trail); view.SetPath(simulation.PlannedPath);
                if (simulation.IsComplete)
                {
                    running = false;
                    if (!recorded) { runs.Add(simulation.Result); recorded = true; }
                    heatDirty = true; selectedTime = (float)simulation.Time;
                    message = "Izvođenje završeno: " + StatusName(simulation.Status) + ". Za procjenu raspodjele pokreni više ponavljanja.";
                }
            }
            visualClock += Time.unscaledDeltaTime;
            if (visualClock > .2f)
            {
                double t = running ? simulation.Time : (runs.Count > 0 ? selectedTime : simulation.Time);
                view.SetHazards(parameters, t, hazardsVisible); visualClock = 0;
            }
            if (heatDirty) { UpdateHeatmap(); heatDirty = false; }
        }
        static double Distance(Point a, Point b) { double x = a.X - b.X, z = a.Z - b.Z; return Math.Sqrt(x*x + z*z); }
        void ToggleRun()
        {
            if (batching) return;
            editTool = 0;
            if (recorded || simulation.IsComplete || runs.Count > 0) ResetRun(true);
            running = !running;
            message = running ? "Simulacija se izvodi s fiksnim vremenskim korakom." : "Pauzirano.";
        }
        IEnumerator RunBatch(int count)
        {
            ResetRun(true); batching = true; running = false; cancelBatch = false; editTool = 0;
            completedTarget = count;
            ModelSettings frozen = parameters.Clone();
            var budget = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < count && !cancelBatch; i++)
            {
                Simulation trial = new Simulation(map, frozen, unchecked(frozen.Seed + i));
                while (!trial.IsComplete && !cancelBatch)
                {
                    trial.Step();
                    if (budget.ElapsedMilliseconds >= 12)
                    {
                        message = "Monte Carlo: " + runs.Count + " / " + count + " završenih izvođenja";
                        yield return null; budget.Restart();
                    }
                }
                if (!trial.IsComplete) break; // Never count a partially cancelled run.
                runs.Add(trial.Result);
                if (runs.Count % 20 == 0) heatDirty = true;
            }
            batching = false; recorded = true; heatDirty = true;
            view.ShowEnsemble(runs, ensembleVisible); Replay();
            message = (cancelBatch ? "Serija zaustavljena. " : "Serija završena. ") + runs.Count + " potpunih izvođenja; prikaz i izvoz koriste upravo taj N.";
        }
        void UpdateHeatmap() { view.SetHeatmap(Statistics.Heatmap(map, runs, selectedTime, finalMap), heatVisible && runs.Count > 0); }
        void Replay()
        {
            if (runs.Count == 0 || running) return;
            replayRun = Mathf.Clamp(replayRun, 0, runs.Count - 1);
            RunResult r = runs[replayRun];
            double t = finalMap ? r.EndTime : selectedTime;
            view.SetAgent(r.PositionAt(t), 0, t, t >= r.EndTime ? r.Status : RunStatus.Moving);
            var partial = new List<Point>();
            foreach (Sample sample in r.Samples) if (sample.Time <= t) partial.Add(sample.Position);
            partial.Add(r.PositionAt(t));
            view.SetTrail(partial); view.SetPath(new List<Point>());
        }
        void EditAtMouse()
        {
            Camera camera = Camera.main; if (camera == null) return;
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            // Select the terrain collider even if decorations or a wall also intersect the ray.
            RaycastHit[] hits = Physics.RaycastAll(ray, float.PositiveInfinity);
            Array.Sort(hits, (a,b) => a.distance.CompareTo(b.distance));
            int index = -1;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.gameObject.name != "TerrainSurface") continue;
                index = map.CellIndex(new Point(hit.point.x, hit.point.z)); break;
            }
            if (index < 0 || index >= map.Cells.Length) return;
            Cell c = map.Cells[index]; Point p = map.Center(index);
            if (editTool == 1 && !c.Blocked) map.Start = p;
            else if (editTool == 2 && !c.Blocked)
            {
                int found = map.FireSources.FindIndex(f => map.CellIndex(f) == index);
                if (found >= 0) map.FireSources.RemoveAt(found);
                else if (map.FireSources.Count < 100) map.FireSources.Add(p);
                else { message="Najviše 100 izvora požara po scenariju."; return; }
            }
            else if (editTool == 3 && index != map.CellIndex(map.Start) && !c.Exit && !map.FireSources.Exists(f => map.CellIndex(f) == index))
            {
                // Only obstacles placed by the user can be removed; water and cliffs stay blocked.
                if (userBlocked.Contains(index)) { userBlocked.Remove(index); c.Blocked = false; }
                else if (!c.Blocked) { userBlocked.Add(index); c.Blocked = true; }
                else return;
            }
            else if (editTool == 4 && !c.Blocked) c.Exit = !c.Exit;
            else return;
            view.Build(map); ResetRun(true); message = "Scenarij izmijenjen. Prethodni rezultati očišćeni.";
        }
        void InitStyles()
        {
            if (titleStyle != null) return;
            panelTexture = Solid(new Color(.045f,.075f,.105f,.97f));
            buttonTexture = Solid(new Color(.13f,.24f,.30f,1));
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 27, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = new Color(.89f,.97f,1);
            subtitleStyle = new GUIStyle(GUI.skin.label) { fontSize=12 }; subtitleStyle.normal.textColor = new Color(.58f,.74f,.80f);
            bodyStyle = new GUIStyle(GUI.skin.label) { fontSize=13, wordWrap=true }; bodyStyle.normal.textColor = new Color(.82f,.9f,.93f);
            smallStyle = new GUIStyle(bodyStyle) { fontSize=11 };
            sectionStyle = new GUIStyle(bodyStyle) { fontSize=15,fontStyle=FontStyle.Bold }; sectionStyle.normal.textColor = new Color(.32f,.89f,.78f);
            panelStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(14,14,12,12) }; panelStyle.normal.background = panelTexture;
            legend = new Texture2D(256,1,TextureFormat.RGBA32,false); legend.wrapMode=TextureWrapMode.Clamp;
            for(int x=0;x<256;x++) { Color col=EnvironmentView.ProbabilityColor(x/255f); col.a=1; legend.SetPixel(x,0,col); } legend.Apply();
        }
        static Texture2D Solid(Color color) { var t = new Texture2D(1,1); t.SetPixel(0,0,color); t.Apply(); return t; }
        bool Button(string text) { return GUILayout.Button(text, GUILayout.Height(30)); }
        void Label(string text) { GUILayout.Label(text,bodyStyle); }
        void Section(string text) { GUILayout.Space(10); GUILayout.Label(text,sectionStyle); }
        double Slider(string label,double value,float min,float max,string unit="")
        {
            Label(label + "  " + value.ToString("0.00",CultureInfo.InvariantCulture) + unit);
            float result = GUILayout.HorizontalSlider((float)value, Mathf.Min(min,(float)value), Mathf.Max(max,(float)value));
            return result == (float)value ? value : result;
        }
        void OnGUI()
        {
            InitStyles(); GUI.matrix=Matrix4x4.Scale(Vector3.one*UiScale);
            GUI.skin.button.fontSize=12; GUI.skin.button.normal.background=buttonTexture;
            GUI.skin.button.normal.textColor=Color.white; GUI.skin.label.wordWrap=true;
            GUI.Label(new Rect(18,12,650,34),"FIRE / ESCAPE LAB",titleStyle);
            GUI.Label(new Rect(20,47,UiWidth-40,22),"3D SIMULACIJA  /  JEDNA OSOBA  /  "+(map==null?"":map.Name.ToUpperInvariant()+"  /  ")+"TEREN + POŽAR + NESIGURNOST",subtitleStyle);
            GUILayout.BeginArea(new Rect(16,82,342,UiHeight-98),panelStyle);
            int tab=GUILayout.Toolbar(panelTab,new[]{"1 · Mapa","2 · Požar i osoba","3 · Simulacija"},GUILayout.Height(30));
            if(tab!=panelTab){panelTab=tab;editTool=0;}
            panelScroll=GUILayout.BeginScrollView(panelScroll);
            if(panelTab==0) DrawMapPanel();
            else if(panelTab==1) { DrawEditorPanel(); DrawParameterPanel(); }
            else DrawSimulationPanel();
            GUILayout.Space(10); GUILayout.Label(message,smallStyle);
            GUILayout.EndScrollView();
            if(Button("Upute i značenje toplinske karte")) showHelp=true;
            GUILayout.EndArea();
            if(simulation!=null) { DrawMetrics(); DrawTimeline(); }
            if(showHelp) DrawHelp();
        }
        void DrawSimulationPanel()
        {
            Section("01  JEDNO IZVOĐENJE");
            GUI.enabled=!batching;
            GUILayout.BeginHorizontal(); if(Button(running?"Pauziraj  [Space]":"Pokreni  [Space]")) ToggleRun();
            if(Button("Reset")) { ResetRun(true); message="Početno stanje vraćeno; parametri i prostor sačuvani."; } GUILayout.EndHorizontal();
            Label("Brzina prikaza: "+timeScale.ToString("0")+"×"); timeScale=GUILayout.HorizontalSlider(timeScale,1,300);
            GUI.enabled=true;
            Section("02  MONTE CARLO"); GUI.enabled=!batching && !running;
            Label("Broj ponavljanja (1–2000)"); batchText=GUILayout.TextField(batchText,5);
            GUILayout.Label("Na velikoj mapi jedno izvođenje traje do nekoliko sekundi; za prvu procjenu dovoljno je 50–100.",smallStyle);
            Label("Početni seed"); string changedSeed=GUILayout.TextField(seedText,10);
            if(changedSeed!=seedText) { seedText=changedSeed; int seed; if(int.TryParse(seedText,out seed) && seed>=0 && seed<int.MaxValue-2000) { parameters.Seed=seed; ResetRun(true); } }
            if(Button("Pokreni seriju ponavljanja"))
            {
                int n,seed;
                if(int.TryParse(batchText,out n) && n>=1 && n<=2000 && int.TryParse(seedText,out seed) && seed>=0 && seed<int.MaxValue-2000)
                { parameters.Seed=seed; StartCoroutine(RunBatch(n)); }
                else message="Unesi N od 1 do 2000 i seed od 0 do 2147481646.";
            }
            GUI.enabled=batching;
            if(Button("Zaustavi seriju")) cancelBatch=true;
            GUI.enabled=true;
            if(batching) Label("Napredak: "+runs.Count+" / "+completedTarget);
            GUILayout.Label("Svako izvođenje ima jednu osobu i vlastiti seed. Uzorkuju se brzina, reakcija i odluke.",smallStyle);
            Section("03  PRIKAZ I IZVOZ");
            bool h=GUILayout.Toggle(heatVisible,"Toplinska karta vjerojatnosti"); if(h!=heatVisible){heatVisible=h;heatDirty=true;}
            hazardsVisible=GUILayout.Toggle(hazardsVisible,"Prikaži požar i dim");
            bool e=GUILayout.Toggle(ensembleVisible,"Putanje ponavljanja (najviše 80)"); if(e!=ensembleVisible){ensembleVisible=e;view.ShowEnsemble(runs,e);}
            GUILayout.BeginHorizontal(); if(Button("Tlocrt  [T]")) view.ResetCamera(true); if(Button("3D pogled  [V]")) view.ResetCamera(false); GUILayout.EndHorizontal();
            GUI.enabled=!batching && !running && runs.Count>0;
            if(Button("Izvezi CSV + JSON + PNG")) TryExport();
            GUI.enabled=true;
        }
        void DrawParameterPanel()
        {
            GUI.enabled=!batching && !running;
            bool before=GUI.changed; GUI.changed=false;
            Section("OSOBA: KRETANJE");
            parameters.BaseSpeed=Slider("Slobodna brzina",parameters.BaseSpeed,.5f,2.2f," m/s");
            parameters.SpeedSigma=Slider("Standardna devijacija brzine",parameters.SpeedSigma,0,.4f," m/s");
            parameters.ReactionDelay=Slider("Osnovno kašnjenje reakcije",parameters.ReactionDelay,0,300," s");
            parameters.ReactionJitter=Slider("Odstupanje reakcije (±)",parameters.ReactionJitter,0,120," s");
            Section("OSOBA: ODLUČIVANJE");
            parameters.Stress=Slider("Stres (heuristika)",parameters.Stress,0,1);
            parameters.Familiarity=Slider("Poznavanje prostora",parameters.Familiarity,0,1);
            parameters.RiskAversion=Slider("Izbjegavanje opasnosti",parameters.RiskAversion,0,10);
            parameters.DecisionNoise=Slider("Nesigurnost izbora puta",parameters.DecisionNoise,0,1);
            Section("POŽAR, DIM I VRIJEME");
            parameters.FireStartRadius=Slider("Početni polumjer požara",parameters.FireStartRadius,5,300," m");
            parameters.FireGrowth=Slider("Širenje požara",parameters.FireGrowth,0,1," m/s");
            parameters.SmokeStrength=Slider("Intenzitet dima",parameters.SmokeStrength,0,3);
            parameters.SmokeRadius=Slider("Početni polumjer dima",parameters.SmokeRadius,20,1000," m");
            parameters.SmokeGrowth=Slider("Širenje dima",parameters.SmokeGrowth,0,3," m/s");
            parameters.Horizon=Math.Round(Slider("Trajanje scenarija",parameters.Horizon,300,7200," s"));
            GUILayout.Label("Korak modela: "+parameters.TimeStep.ToString("0.00")+" s; interval snimki: "+parameters.SnapshotInterval.ToString("0.0")+" s. Zapis uključuje i međukorake. Napredni parametri dostupni su u JSON-u i Inspectoru.",smallStyle);
            bool changed=GUI.changed; GUI.changed=before||changed;
            if(changed) { selectedTime=Mathf.Min(selectedTime,(float)parameters.Horizon); ResetRun(true); message="Parametri promijenjeni; prethodni rezultati očišćeni."; }
            if(Button("Vrati zadane parametre")) { parameters=new ModelSettings();seedText=parameters.Seed.ToString();ResetRun(true); }
            GUI.enabled=true;
        }
        void DrawEditorPanel()
        {
            Section("POČETAK, POŽAR I IZLAZI");
            Label("Odaberi alat pa lijevim klikom označi teren u 3D prikazu. Svaka izmjena briše rezultate.");
            GUI.enabled=!batching && !running;
            editTool=GUILayout.SelectionGrid(editTool,new[]{"Bez alata","Početak osobe","Požar ±","Prepreka ±","Izlaz ±"},2,GUILayout.Height(92));
            GUILayout.Label("Požar, prepreka i izlaz uključuju se / uklanjaju ponovnim klikom. Voda i stijene (zatamnjeno) su neprohodne. Izlazi su zelene ćelije.",smallStyle);
            GUILayout.BeginHorizontal();
            if(Button("Svi rubovi = izlaz")) EditMap(()=>ScenarioSetup.SetAllEdgeExits(map),"Svi prohodni rubovi mape su izlazi.");
            if(Button("Ukloni izlaze")) EditMap(()=>ScenarioSetup.ClearExits(map),"Izlazi uklonjeni; dodaj nove alatom Izlaz ±.");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if(Button("Ukloni požare")) EditMap(()=>map.FireSources.Clear(),"Požari uklonjeni.");
            if(Button("Zadani raspored")) EditMap(()=>{foreach(int i in userBlocked)map.Cells[i].Blocked=false;userBlocked.Clear();ScenarioSetup.PlaceDefaults(map,parameters.Seed);},"Vraćen zadani početak, požar i izlazi.");
            GUILayout.EndHorizontal();
            if(Button("Dalje: simulacija  →")) { editTool=0; panelTab=2; }
            GUI.enabled=true;
            Section("KAMERA"); Label("Desna tipka: rotacija\nSrednja tipka: pomicanje\nKotačić: približavanje\nT: tlocrt · V: 3D · Esc: kraj uređivanja");
        }
        void EditMap(Action change,string done)
        {
            change(); view.Build(map); ResetRun(true); message=done+" Prethodni rezultati očišćeni.";
        }
        void DrawMapPanel()
        {
            GUI.enabled=!batching && !running;
            Section("IZVOR MAPE");
            mapMode=GUILayout.Toolbar(mapMode,new[]{"Stvarna mapa","Nasumična mapa"},GUILayout.Height(28));
            if(mapMode==0)
            {
                if(realMaps.Count==0) Label("Nema izvezenih mapa. U python/ pokreni: uv run python izvoz_unity.py");
                else
                {
                    var names=new string[realMaps.Count]; for(int i=0;i<names.Length;i++) names[i]=realMaps[i].name;
                    realIndex=GUILayout.SelectionGrid(Mathf.Clamp(realIndex,0,names.Length-1),names,2,GUILayout.Height(30*((names.Length+1)/2)));
                    GUILayout.Label("Copernicus DEM GLO-30 i ESA WorldCover 2021, izvezeno iz python/mape/.",smallStyle);
                }
            }
            else
            {
                Section("TEREN");
                int sizeIndex=Mathf.Max(0,Array.IndexOf(MapSizes,terrain.Size));
                Label("Veličina mreže (čvorova po strani)");
                sizeIndex=GUILayout.Toolbar(sizeIndex,new[]{"65","129","257"}); terrain.Size=MapSizes[sizeIndex];
                terrain.CellSize=Math.Round(Slider("Veličina ćelije",terrain.CellSize,2,30," m"));
                GUILayout.Label("Mapa je "+((terrain.Size-1)*terrain.CellSize/1000).ToString("0.00",CultureInfo.InvariantCulture)+" km × "+((terrain.Size-1)*terrain.CellSize/1000).ToString("0.00",CultureInfo.InvariantCulture)+" km.",smallStyle);
                terrain.ReliefM=Math.Round(Slider("Visina grebena",terrain.ReliefM,0,1000," m"));
                terrain.RidgeShare=Share("Grebeni i brda (brijegovi)",terrain.RidgeShare);
                terrain.ValleyShare=Share("Doline (udoline)",terrain.ValleyShare);
                Section("VODA");
                terrain.WaterShare=Math.Min(.9,Share("Voda",terrain.WaterShare));
                terrain.Water=(WaterMode)GUILayout.Toolbar((int)terrain.Water,new[]{"More uz rub","Jezera"});
                Section("POKROV KOPNA");
                terrain.ForestShare=Share("Šuma",terrain.ForestShare);
                terrain.ShrubShare=Share("Makija",terrain.ShrubShare);
                terrain.BareShare=Share("Golo / kamenjar",terrain.BareShare);
                double rest=1-terrain.ForestShare-terrain.ShrubShare-terrain.BareShare;
                GUILayout.Label(rest>=-1e-9?"Travnjak (ostatak): "+(100*Math.Max(0,rest)).ToString("0")+" %":"Šuma + makija + golo prelaze 100 %: smanji neki udio.",smallStyle);
                Section("SEED");
                Label("Prazno = nova nasumična mapa svaki put; broj = ista mapa.");
                terrainSeedText=GUILayout.TextField(terrainSeedText,7);
            }
            Section("PROHODNOST");
            terrain.MaxSlopeDeg=Math.Round(Slider("Najveći prohodni nagib",terrain.MaxSlopeDeg,20,70,"°"));
            GUILayout.Space(8);
            bool valid=mapMode==0?realMaps.Count>0:terrain.ForestShare+terrain.ShrubShare+terrain.BareShare<=1+1e-9;
            GUI.enabled=!batching && !running && valid;
            if(Button(mapMode==0?"Učitaj mapu":"Generiraj mapu")) BuildMap();
            GUI.enabled=!batching && !running && map!=null;
            if(Button("Dalje: požar i osoba  →")) panelTab=1;
            GUI.enabled=true;
            if(map!=null && mapTerrain!=null)
            {
                var m=TerrainGenerator.Measure(map);
                GUILayout.Label("Izmjereno: voda "+Pct(m["water"])+", šuma "+Pct(m["forest"])+", makija "+Pct(m["shrub"])+", travnjak "+Pct(m["grass"])+", golo "+Pct(m["bare"])+" (pokrov od kopna), neprohodno kopno "+Pct(m["blockedLand"])+".",smallStyle);
            }
        }
        double Share(string label,double value){ return Math.Round(Slider(label+" (udio)",value,0,1),2); }
        static string Pct(double x){ return (100*x).ToString("0",CultureInfo.InvariantCulture)+" %"; }
        void DrawMetrics()
        {
            GUILayout.BeginArea(new Rect(378,82,290,155),panelStyle);
            GUILayout.Label(running?"IZVOĐENJE U TIJEKU":"REZULTATI",sectionStyle);
            if(running || runs.Count==0) Label("t = "+simulation.Time.ToString("0.0")+" s  ·  v = "+simulation.CurrentSpeed.ToString("0.00")+" m/s");
            else Label(finalMap ? "Prikaz konačnih položaja" : "Pregled pri t = "+selectedTime.ToString("0.0")+" s");
            if(runs.Count>0)
            {
                int success=0; foreach(var r in runs) if(r.Status==RunStatus.Evacuated) success++;
                double[] ci=Statistics.WilsonInterval(success,runs.Count);
                Label("Evakuirano: "+success+" / "+runs.Count+"  ("+(100.0*success/runs.Count).ToString("0.0")+" %)");
                Label("95 % Wilson CI: "+(ci[0]*100).ToString("0.0")+"–"+(ci[1]*100).ToString("0.0")+" %");
                double mean=Statistics.MeanEvacuationTime(runs);
                Label("Prosjek uspješnih: "+(double.IsNaN(mean)?"—":mean.ToString("0.0")+" s"));
            }
            else { Label(StatusName(simulation.Status)); GUILayout.Label("Po završetku se prikazuju statistike.",smallStyle); }
            GUILayout.EndArea();
        }
        void DrawTimeline()
        {
            float w=UiWidth-398;
            GUILayout.BeginArea(new Rect(378,UiHeight-148,w,132),panelStyle);
            GUILayout.BeginHorizontal(); GUILayout.Label("VJEROJATNOST POLOŽAJA",sectionStyle);
            GUI.enabled=runs.Count>0 && !running;
            bool f=GUILayout.Toggle(finalMap,"Konačni položaji",GUILayout.Width(150));
            if(f!=finalMap){finalMap=f;heatDirty=true;Replay();}
            GUILayout.EndHorizontal();
            if(!finalMap)
            {
                GUILayout.BeginHorizontal(); Label("t = "+selectedTime.ToString("0.0")+" s");
                float t=GUILayout.HorizontalSlider(selectedTime,0,(float)parameters.Horizon,GUILayout.Width(Mathf.Max(120,w-180)));
                if(Mathf.Abs(t-selectedTime)>.01){selectedTime=t;heatDirty=true;Replay();} GUILayout.EndHorizontal();
            }
            else Label("Konačna raspodjela: stanje pri završetku svakog izvođenja (različita vremena).");
            GUI.enabled=true;
            Rect bar=GUILayoutUtility.GetRect(w-28,10); GUI.DrawTexture(bar,legend);
            GUILayout.BeginHorizontal(); GUILayout.Label("0 %",smallStyle); GUILayout.FlexibleSpace();GUILayout.Label("50 %",smallStyle);GUILayout.FlexibleSpace();GUILayout.Label("100 %",smallStyle);GUILayout.EndHorizontal();
            GUILayout.Label("N = "+runs.Count+" · ćelija "+map.CellSize.ToString("0.0")+" m · p = broj položaja / N · završeni položaji ostaju u raspodjeli",smallStyle);
            GUILayout.EndArea();
        }
        void DrawHelp()
        {
            GUI.Box(new Rect(0,0,UiWidth,UiHeight),GUIContent.none,panelStyle);
            GUILayout.BeginArea(new Rect(Mathf.Max(30,(UiWidth-800)/2),35,Mathf.Min(800,UiWidth-60),UiHeight-70),panelStyle);
            GUILayout.Label("KAKO KORISTITI PROJEKT",titleStyle); helpScroll=GUILayout.BeginScrollView(helpScroll);
            Label("1. Mapa: učitaj stvarnu mapu (izvezenu iz python/mape/) ili generiraj nasumičnu iz postavki (udio grebena, dolina, vode i pokrova). Nasumična mapa se ne sprema; njezin seed je u imenu, pa se može ponoviti.");
            Label("2. Požar i osoba: zadani početak, požar i izlazi postavljaju se sami; promijeni ih alatima i namjesti parametre osobe i požara.");
            Label("3. Simulacija: klikni Pokreni. Jedna osoba odabire put do izlaza uz trošak pokrova tla, nagiba, dima i topline. Pauziraj ili ubrzaj prikaz bez promjene vremenskog koraka modela.");
            Section("PONAVLJANJA I TOPLINSKA KARTA");
            Label("2. Pokreni npr. 200 ponavljanja. Isti početni seed i parametri reproduciraju iste rezultate. Svako izvođenje uzorkuje vlastitu brzinu, reakciju i odluke.");
            Label("3. Pomakni vremenski klizač. Svaka boja predstavlja p(X(t) u ćeliji): broj ponavljanja u kojima je osoba ondje / broj završenih ponavljanja. Zbroj je 1. Ovo nije broj posjeta ćeliji niti karta temperature. Nula je prozirna; skala boja uvijek je 0–100 %.");
            Label("Nakon evakuacije ili drugog završetka osoba ostaje na završnoj lokaciji za potrebe statistike. Zato zelena područja izlaza mogu imati visoku vjerojatnost. Prekidač Konačni položaji koristi završetak svakog izvođenja, a ne zajedničko vrijeme.");
            Label("Prikazana osoba i plava putanja u pregledu rezultata pripadaju prvom izvođenju. Ostale tanke putanje prikazuju najviše 80 ponavljanja; toplinska karta uvijek koristi sva završena izvođenja.");
            Section("UREĐIVANJE I IZVOZ");
            Label("Korak 2 → alat → lijevi klik na teren. Moguće je mijenjati početak, požare, prepreke i izlaze. Parametri upravljaju brzinom, reakcijom, stresom, poznavanjem prostora, dimom i širenjem požara. Promjena briše stare rezultate.");
            Label("Izvoz stvara trajectories.csv, runs.csv, heatmap.csv, heatmap.png, heatmap_legend.png i metadata.json u mapi Exports unutar persistentDataPath. metadata.json sadrži ime mape (i postavke generatora sa seedom), početak, požare, izlaze i prepreke. PNG ima sjever (+Z) gore; CSV sadrži točne koordinate i vjerojatnosti.");
            Section("ŠTO MODEL PREDSTAVLJA");
            Label("Ovo je istraživački demonstrator, bez empirijske kalibracije. Požar i dim su sintetička polja (krug koji raste zadanom brzinom), ne model širenja požara: ne ovise o vjetru, nagibu ni gorivu. Pokazatelj izloženosti nije fiziološki FED. Nema drugih osoba, sudara u gomili ni socijalnog ponašanja. Kretanje je po mreži na 3D terenu (2,5D); brzina ovisi o nagibu i pokrovu tla, a voda i stijene su neprohodne. Detaljne jednadžbe i izvori nalaze se u Documentation.");
            GUILayout.EndScrollView(); if(Button("Zatvori  [Esc]"))showHelp=false; GUILayout.EndArea();
        }
        void TryExport()
        {
            try { message="Izvezeno: "+ResultsExporter.Export(map,parameters,runs,selectedTime,finalMap,mapTerrain,new List<int>(userBlocked)); }
            catch(Exception e) { message="Izvoz nije uspio: "+e.Message; Debug.LogException(e); }
        }
        static string StatusName(RunStatus status)
        {
            switch(status){case RunStatus.Evacuated:return "Evakuiran";case RunStatus.Incapacitated:return "Zaustavljen zbog izloženosti";case RunStatus.TimedOut:return "Istek vremena";default:return "Kretanje / reakcija";}
        }
        void OnDestroy(){if(panelTexture!=null)Destroy(panelTexture);if(buttonTexture!=null)Destroy(buttonTexture);if(legend!=null)Destroy(legend);}
    }
}
