#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;
using FireEscape.Core;

namespace FireEscape.Editor
{
    public static class FireEscapeEditor
    {
        const string ScenePath="Assets/FireEscape/Scenes/Main.unity";
        [MenuItem("Fire Escape/Otvori glavnu scenu")]
        public static void OpenScene()
        {
            if(EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())EditorSceneManager.OpenScene(ScenePath);
        }
        [MenuItem("Fire Escape/Provjeri model (brza provjera)")]
        public static void VerifyModel()
        {
            // Generator: shares match the settings, and a fixed seed gives the same map.
            var terrain=new TerrainSettings{Size=65,CellSize=10,Seed=7,WaterShare=.2,ForestShare=.25,ShrubShare=.35,BareShare=.15};
            int seedA,seedB;
            GridMap g1=TerrainGenerator.Generate(terrain,out seedA), g2=TerrainGenerator.Generate(terrain,out seedB);
            for(int i=0;i<g1.Cells.Length;i++) if(g1.Cells[i].Height!=g2.Cells[i].Height||g1.Cells[i].Terrain!=g2.Cells[i].Terrain) throw new Exception("Generator s istim seedom ne daje istu mapu.");
            var measured=TerrainGenerator.Measure(g1);
            if(Math.Abs(measured["water"]-.2)>.02||Math.Abs(measured["forest"]-.25)>.02||Math.Abs(measured["shrub"]-.35)>.02||Math.Abs(measured["bare"]-.15)>.02)
                throw new Exception("Udjeli generirane mape ne odgovaraju postavkama.");

            // Evacuation on a flat generated map without fire: start in the middle, every edge is an exit.
            var settings=new ModelSettings{Stress=0,SpeedSigma=0,ReactionJitter=0,DecisionNoise=0,ReactionDelay=0};
            int flatSeed;
            GridMap map=TerrainGenerator.Generate(new TerrainSettings{Size=65,CellSize=10,Seed=1,RidgeShare=0,ValleyShare=0,WaterShare=0},out flatSeed);
            ScenarioSetup.PlaceDefaults(map,1); map.FireSources.Clear(); ScenarioSetup.SetAllEdgeExits(map);
            RunResult a=new Simulation(map,settings,42).RunToEnd();
            RunResult b=new Simulation(map,settings,42).RunToEnd();
            if(a.Status!=RunStatus.Evacuated)throw new Exception("Baseline nije evakuiran.");
            if(a.EndTime!=b.EndTime||a.FinalPosition.X!=b.FinalPosition.X||a.FinalPosition.Z!=b.FinalPosition.Z)throw new Exception("Reproducibilnost nije zadovoljena.");
            var runs=new System.Collections.Generic.List<RunResult>{a,b};
            foreach(double t in new double[]{0,30,120,600})
            {
                double sum=0;foreach(double p in Statistics.Heatmap(map,runs,t,false))sum+=p;
                if(Math.Abs(sum-1)>1e-10)throw new Exception("Toplinska karta nije normalizirana.");
            }
            Debug.Log("Fire Escape: brze provjere generatora i modela uspjele. Evakuacija na ravnoj mapi t="+a.EndTime.ToString("0.0")+" s.");
        }
        [MenuItem("Fire Escape/Build Windows x64")]
        public static void BuildWindows()
        {
            string path=EditorUtility.SaveFilePanel("Build Windows x64","Builds","FireEscapeLab","exe");
            if(string.IsNullOrEmpty(path))return;Build(path,BuildTarget.StandaloneWindows64);
        }
        [MenuItem("Fire Escape/Build Linux x64")]
        public static void BuildLinux()
        {
            string path=EditorUtility.SaveFilePanel("Build Linux x64","Builds","FireEscapeLab","x86_64");
            if(string.IsNullOrEmpty(path))return;Build(path,BuildTarget.StandaloneLinux64);
        }
        static void Build(string path,BuildTarget target)
        {
            string parent=Path.GetDirectoryName(path);if(!string.IsNullOrEmpty(parent))Directory.CreateDirectory(parent);
            BuildReport report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath},locationPathName=path,target=target,options=BuildOptions.None});
            if(report.summary.result!=BuildResult.Succeeded)throw new Exception("Unity build nije uspio: "+report.summary.result);
            Debug.Log("Build završen: "+path);
        }
        // Headless: Unity -batchmode -quit -projectPath ... -executeMethod FireEscape.Editor.FireEscapeEditor.BatchVerify -logFile verify.log
        public static void BatchVerify(){VerifyModel();}
    }
}
#endif
