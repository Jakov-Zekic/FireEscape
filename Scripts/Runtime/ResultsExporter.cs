using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using FireEscape.Core;

namespace FireEscape
{
    public static class ResultsExporter
    {
        static string N(double value) { return value.ToString("R",CultureInfo.InvariantCulture); }
        [Serializable] sealed class ExportMetadata
        {
            public string schemaVersion="2.0", createdUtc, mapName, mapSource, mapConvention="index=z*width+x; world units=metres; PNG top=+Z", heatmapMode;
            public double selectedTimeSeconds, cellSizeMetres, successFraction, meanSuccessfulEvacuationSeconds;
            public double[] successWilson95;
            public int runCount, width, depth;
            public ModelSettings parameters;
            public TerrainSettings terrainSettings; // only for generated maps; with the seed in mapName it rebuilds the map
            public Point start;
            public Point[] fireSources;
            public int[] exitCells, userBlockedCells;
            public string interpretation="p(cell)=count/N; terminal positions absorbing; all complete runs including failures; fixed colour scale 0..1";
            public string limitations="Synthetic hazard; no CFD or physiological FED; uncalibrated single-person 2.5D research demonstration.";
        }
        /// <summary>terrain is null for real maps; userBlocked are cells the user turned into obstacles.</summary>
        public static string Export(GridMap map,ModelSettings settings,IList<RunResult> runs,double time,bool final,TerrainSettings terrain,IList<int> userBlocked)
        {
            if(runs.Count==0) throw new InvalidOperationException("Nema dovršenih izvođenja.");
            string folder=Path.Combine(Application.persistentDataPath,"Exports",DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff",CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            using(var writer=new StreamWriter(Path.Combine(folder,"trajectories.csv"),false,new UTF8Encoding(false)))
            {
                writer.WriteLine("run,seed,time_s,x_m,y_m,z_m,status,speed_m_s,smoke_proxy,heat_proxy");
                for(int i=0;i<runs.Count;i++) foreach(Sample s in runs[i].Samples)
                    writer.WriteLine(i+","+runs[i].Seed+","+N(s.Time)+","+N(s.Position.X)+","+N(map.HeightAt(s.Position))+","+N(s.Position.Z)+","+s.Status+","+N(s.Speed)+","+N(s.Smoke)+","+N(s.Heat));
            }
            int successes=0;
            using(var writer=new StreamWriter(Path.Combine(folder,"runs.csv"),false,new UTF8Encoding(false)))
            {
                writer.WriteLine("run,seed,status,end_time_s,distance_m,sampled_base_speed_m_s,reaction_delay_s,final_x_m,final_y_m,final_z_m");
                for(int i=0;i<runs.Count;i++)
                {
                    RunResult r=runs[i]; if(r.Status==RunStatus.Evacuated)successes++;
                    writer.WriteLine(i+","+r.Seed+","+r.Status+","+N(r.EndTime)+","+N(r.Distance)+","+N(r.SampledSpeed)+","+N(r.SampledReactionDelay)+","+N(r.FinalPosition.X)+","+N(map.HeightAt(r.FinalPosition))+","+N(r.FinalPosition.Z));
                }
            }
            double[] p=Statistics.Heatmap(map,runs,time,final);
            using(var writer=new StreamWriter(Path.Combine(folder,"heatmap.csv"),false,new UTF8Encoding(false)))
            {
                writer.WriteLine("cell_x,cell_z,center_x_m,center_z_m,mode,time_s,count,n,probability,wilson95_low,wilson95_high");
                for(int i=0;i<p.Length;i++)
                {
                    int count=(int)Math.Round(p[i]*runs.Count); double[] ci=Statistics.WilsonInterval(count,runs.Count);Point c=map.Center(i);
                    writer.WriteLine(i%map.Width+","+i/map.Width+","+N(c.X)+","+N(c.Z)+","+(final?"final":"time")+","+(final?"":N(time))+","+count+","+runs.Count+","+N(p[i])+","+N(ci[0])+","+N(ci[1]));
                }
            }
            var image=HeatmapImage.Create(map,p,time,final,runs.Count);
            image.Apply();File.WriteAllBytes(Path.Combine(folder,"heatmap.png"),image.EncodeToPNG());UnityEngine.Object.Destroy(image);
            var legend=new Texture2D(512,32,TextureFormat.RGBA32,false);
            for(int x=0;x<512;x++)for(int y=0;y<32;y++){Color c=EnvironmentView.ProbabilityColor(x/511f);c.a=1;legend.SetPixel(x,y,c);}
            legend.Apply();File.WriteAllBytes(Path.Combine(folder,"heatmap_legend.png"),legend.EncodeToPNG());UnityEngine.Object.Destroy(legend);
            double mean=Statistics.MeanEvacuationTime(runs);
            var exits=new List<int>(); for(int i=0;i<map.Cells.Length;i++) if(map.Cells[i].Exit) exits.Add(i);
            var metadata=new ExportMetadata{createdUtc=DateTime.UtcNow.ToString("o"),mapName=map.Name,mapSource=terrain==null?"real (python/izvoz_unity.py)":"generated (TerrainGenerator)",
                heatmapMode=final?"final_positions":"position_at_time",selectedTimeSeconds=time,cellSizeMetres=map.CellSize,runCount=runs.Count,width=map.Width,depth=map.Depth,
                parameters=settings.Clone(),terrainSettings=terrain==null?null:terrain.Clone(),start=map.Start,fireSources=map.FireSources.ToArray(),exitCells=exits.ToArray(),
                userBlockedCells=new List<int>(userBlocked).ToArray(),
                successFraction=(double)successes/runs.Count,meanSuccessfulEvacuationSeconds=double.IsNaN(mean)?-1:mean,successWilson95=Statistics.WilsonInterval(successes,runs.Count)};
            File.WriteAllText(Path.Combine(folder,"metadata.json"),JsonUtility.ToJson(metadata,true),Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder,"README.txt"),"FIRE / ESCAPE LAB\n"+
                "N="+runs.Count+"; mode="+(final?"FINAL POSITIONS (per-run end time)":"POSITION AT t="+N(time)+" s")+"\n"+
                "heatmap.png: top is +Z; cell size "+N(map.CellSize)+" m. The map includes its selected time or terminal mode, N and fixed colour legend.\n"+
                "heatmap_legend.png: left 0%, middle 50%, right 100%; zero-probability cells show grey base map.\n"+
                "No smoothing or per-image normalisation. Exact counts/probabilities are in heatmap.csv.\n"+
                "Terminal endpoints remain in the distribution (absorbing convention).\n"+
                "Wilson intervals are marginal per cell, not simultaneous confidence bands.\n"+
                "meanSuccessfulEvacuationSeconds=-1 denotes no successful runs.\n"+
                "trajectories.csv contains integration/waypoint records in addition to fixed snapshot times.\n"+
                "metadata.json names the map (real map, or generator settings + seed in mapName), start, fires, exits and obstacles; seeds in runs.csv identify every run.\n",Encoding.UTF8);
            return folder;
        }
    }
}
