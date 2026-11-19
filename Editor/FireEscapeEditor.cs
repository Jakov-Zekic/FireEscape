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
            var settings=new ModelSettings{Stress=0,SpeedSigma=0,ReactionJitter=0,DecisionNoise=0,ReactionDelay=0};
            GridMap map=ScenarioFactory.Create(ScenarioKind.Baseline);
            RunResult a=new Simulation(map,settings,42).RunToEnd();
            RunResult b=new Simulation(map,settings,42).RunToEnd();
            if(a.Status!=RunStatus.Evacuated)throw new Exception("Baseline nije evakuiran.");
            if(a.EndTime!=b.EndTime||a.FinalPosition.X!=b.FinalPosition.X||a.FinalPosition.Z!=b.FinalPosition.Z)throw new Exception("Reproducibilnost nije zadovoljena.");
            var runs=new System.Collections.Generic.List<RunResult>{a,b};
            foreach(double t in new double[]{0,5,15,120})
            {
                double sum=0;foreach(double p in Statistics.Heatmap(map,runs,t,false))sum+=p;
                if(Math.Abs(sum-1)>1e-10)throw new Exception("Toplinska karta nije normalizirana.");
            }
            Debug.Log("Fire Escape: brze provjere modela uspjele. Baseline t="+a.EndTime+" s. Za puni skup provjera pokreni Tools/verify.sh ili dotnet run --project Tools/Verification.csproj.");
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
