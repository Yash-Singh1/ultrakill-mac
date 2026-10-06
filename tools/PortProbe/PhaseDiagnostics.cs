using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace ULTRAKILL.MacPort
{
    // Only observes callback boundaries. Original loop systems stay in their original order.
    public sealed class PhaseDiagnostics : MonoBehaviour
    {
        sealed class Boundary { }
        static PhaseDiagnostics instance;
        string[] names;
        long[] starts;
        double[] durations;
        long frameStart, previousEnd;
        double gap;
        StreamWriter output;
        readonly Row[] rows = new Row[4096];
        int count;
        struct Row { public DateTime utc; public string scene; public double total, gap; public double[] phases; public int collections; }
        int collectionStart;
        string lastSchema;
        public static void Install(GameObject root)
        {
            if (!instance) instance = root.AddComponent<PhaseDiagnostics>();
        }
        void Start()
        {
            try
            {
                string dir=PortPaths.Diagnostics(Application.dataPath);
                Directory.CreateDirectory(dir);
                output=new StreamWriter(Path.Combine(dir,"phases-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".csv"));
                InstallLoop();
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            catch(Exception e) { StopRecording(e); }
        }
        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try { InstallLoop(); }
            catch(Exception e) { StopRecording(e); }
        }
        void StopRecording(Exception error)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            RemoveLoop();
            output?.Dispose();output=null;
            UnityEngine.Debug.LogWarning("[MacPhases] Diagnostics unavailable: "+error.Message);
            enabled=false;
        }
        static void RemoveLoop()
        {
            var loop=PlayerLoop.GetCurrentPlayerLoop();
            if(loop.subSystemList==null)return;
            loop.subSystemList=Array.FindAll(loop.subSystemList,system=>system.type!=typeof(Boundary));
            PlayerLoop.SetPlayerLoop(loop);
        }
        void InstallLoop()
        {
            Flush();
            var loop=PlayerLoop.GetCurrentPlayerLoop();
            if(loop.subSystemList==null)throw new InvalidOperationException("Unity player loop is unavailable.");
            var original=Array.FindAll(loop.subSystemList,system=>system.type!=typeof(Boundary));
            names=new string[original.Length];starts=new long[original.Length];durations=new double[original.Length];
            for(int i=0;i<rows.Length;i++)rows[i].phases=new double[original.Length];
            var wrapped=new PlayerLoopSystem[original.Length*3+2];
            wrapped[0]=new PlayerLoopSystem { type=typeof(Boundary),updateDelegate=BeginFrame };
            for(int i=0;i<original.Length;i++)
            {
                int slot=i;names[i]=original[i].type?.Name??"Unknown";
                wrapped[1+i*3]=new PlayerLoopSystem { type=typeof(Boundary),updateDelegate=()=>BeginPhase(slot) };
                wrapped[2+i*3]=original[i];
                wrapped[3+i*3]=new PlayerLoopSystem { type=typeof(Boundary),updateDelegate=()=>EndPhase(slot) };
            }
            wrapped[wrapped.Length-1]=new PlayerLoopSystem { type=typeof(Boundary),updateDelegate=EndFrame };
            string schema="utc,scene,frame_ms,between_updates_ms,gc_collections,"+string.Join(",",names);
            if (schema != lastSchema)
            {
                if(lastSchema != null) { output.Dispose(); output=new StreamWriter(Path.Combine(PortPaths.Diagnostics(Application.dataPath),"phases-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".csv")); }
                output.WriteLine(schema); output.Flush(); lastSchema=schema;
            }
            loop.subSystemList=wrapped;PlayerLoop.SetPlayerLoop(loop);
            previousEnd=0;
        }
        static double Milliseconds(long ticks) => ticks*1000.0/Stopwatch.Frequency;
        void BeginFrame()
        {
            frameStart=Stopwatch.GetTimestamp();gap=previousEnd==0?0:Milliseconds(frameStart-previousEnd);
            collectionStart=GC.CollectionCount(0);
        }
        void BeginPhase(int i) { starts[i]=Stopwatch.GetTimestamp(); }
        void EndPhase(int i) { durations[i]=Milliseconds(Stopwatch.GetTimestamp()-starts[i]); }
        void EndFrame()
        {
            long now=Stopwatch.GetTimestamp();double total=Milliseconds(now-frameStart)+gap;
            if(total>=25 && count<rows.Length)
            {
                rows[count].utc=DateTime.UtcNow;rows[count].scene=SceneHelper.CurrentScene;
                rows[count].total=total;rows[count].gap=gap;rows[count].collections=GC.CollectionCount(0)-collectionStart;
                Array.Copy(durations,rows[count].phases,durations.Length);count++;
            }
            // Flush at most once per scene and on quit; never write CSV during a fight.
            previousEnd=Stopwatch.GetTimestamp();
        }
        void Flush()
        {
            if(output==null)return;
            for(int i=0;i<count;i++)
            {
                var row=rows[i];output.Write(row.utc.ToString("O")+","+row.scene+","+row.total.ToString("F3",CultureInfo.InvariantCulture)+","+row.gap.ToString("F3",CultureInfo.InvariantCulture)+","+row.collections);
                foreach(var phase in row.phases)output.Write(","+phase.ToString("F3",CultureInfo.InvariantCulture));output.WriteLine();
            }
            output.Flush();count=0;
        }
        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            RemoveLoop();
            Flush();output?.Dispose();output=null;instance=null;
        }
    }
}
