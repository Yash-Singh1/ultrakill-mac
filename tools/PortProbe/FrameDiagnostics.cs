using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Jobs.LowLevel.Unsafe;

namespace ULTRAKILL.MacPort
{
    public sealed class FrameDiagnostics : MonoBehaviour
    {
        readonly string[] names = { "Main Thread", "Render Thread", "Gfx.WaitForPresentOnGfxThread", "WaitForJobGroupID", "GC.Alloc", "Draw Calls Count", "Triangles Count", "MacPacing.Wait", "MacFrame.PortalRender", "MacFrame.PostProcess", "MacFrame.WallCheck", "Gfx.WaitForRenderThread", "Gfx.PresentFrame", "Camera.Render" };
        ProfilerRecorder[] recorders;
        readonly double[] totals = new double[14];
        double portalPassTotal, auxiliaryPassTotal;
        readonly FrameTiming[] timingBuffer = new FrameTiming[1];
        readonly double[] gpuTimes = new double[4096];
        bool frameTimingEnabled;
        ulong lastTimingTimestamp;
        int timingCount;
        double gpuTotal, cpuFrameTotal, cpuActiveTotal, renderActiveTotal, presentWaitTotal;
        readonly double[] frameTimes = new double[4096];
        StreamWriter log;
        double previous, windowStart;
        int count;
        StreamWriter spikes;
        int collectionCount;
        readonly string[] spikeRows = new string[256];
        int spikeCount;
        void Start()
        {
            try
            {
                string dir = PortPaths.Diagnostics(Application.dataPath);
                Directory.CreateDirectory(dir);
                log = new StreamWriter(Path.Combine(dir, "frames-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".csv"));
                log.WriteLine("utc,scene,vsync,requested_fps,precise_pacing,width,height,frames,mean_ms,p95_ms,max_ms,main_ms,render_ms,present_ms,jobs_ms,gc_bytes,draw_calls,triangles,pacing_ms,portal_ms,postprocess_ms,wallcheck_ms,render_thread_wait_ms,present_frame_ms,camera_render_ms,worker_count,native_buffer_queries,portal_camera_passes,portal_auxiliary_passes,render_width,render_height,gpu_ms,gpu_p95_ms,frame_cpu_ms,frame_cpu_active_ms,frame_render_active_ms,frame_present_wait_ms,timing_samples");
                recorders = new ProfilerRecorder[names.Length];
                for (int i=0;i<names.Length;i++)
                {
                    var category = i == 4 ? ProfilerCategory.Memory : i == 5 || i == 6 ? ProfilerCategory.Render : ProfilerCategory.Internal;
                    // User markers use the Scripts category.
                    if (i >= 7 && i <= 10) category = ProfilerCategory.Scripts;
                    if (i == 13) category = ProfilerCategory.Render;
                    recorders[i] = ProfilerRecorder.StartNew(category, names[i], 1);
                }
                UnityEngine.Debug.Log("[MacFrames] Rendering counters available: render_thread_wait=" + recorders[11].Valid + " present_frame=" + recorders[12].Valid + " camera_render=" + recorders[13].Valid);
                frameTimingEnabled = FrameTimingManager.IsFeatureEnabled();
                UnityEngine.Debug.Log("[MacFrames] CPU/GPU frame timing available=" + frameTimingEnabled + ". GPU results arrive after their originating frame; window statistics deduplicate timestamps.");
                spikes = new StreamWriter(Path.Combine(dir, "spikes-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".csv"));
                spikes.WriteLine("utc,scene,frame_ms,gc_collections,requested_fps,vsync,draw_calls,triangles,pacing_ms,portal_ms,postprocess_ms,render_thread_wait_ms,present_frame_ms,camera_render_ms,portal_camera_passes,portal_auxiliary_passes");
                collectionCount = GC.CollectionCount(0);
                previous = windowStart = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[MacFrames] Diagnostics unavailable: " + e.Message); enabled=false; }
        }
        void LateUpdate()
        {
            if (log == null) return;
            // Read asynchronous timing results without waiting for the GPU.
            // A timestamp can be returned more than once. Count each result once.
            if (frameTimingEnabled && SceneHelper.CurrentScene != null && SceneHelper.CurrentScene.StartsWith("Level 8-", StringComparison.Ordinal))
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timingBuffer) != 0)
                {
                    var timing = timingBuffer[0];
                    if (timing.frameStartTimestamp != 0 && timing.frameStartTimestamp != lastTimingTimestamp && timing.gpuFrameTime > 0 && timingCount < gpuTimes.Length)
                    {
                        lastTimingTimestamp = timing.frameStartTimestamp;
                        gpuTimes[timingCount++] = timing.gpuFrameTime;
                        gpuTotal += timing.gpuFrameTime; cpuFrameTotal += timing.cpuFrameTime;
                        cpuActiveTotal += timing.cpuMainThreadFrameTime; renderActiveTotal += timing.cpuRenderThreadFrameTime;
                        presentWaitTotal += timing.cpuMainThreadPresentWaitTime;
                    }
                }
            }
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            double frameMs = (now-previous)*1000;
            frameTimes[count++] = frameMs; previous=now;
            int currentCollections = GC.CollectionCount(0);
            if (frameMs >= 25 && spikeCount < spikeRows.Length)
            {
                string metric(int i) => (recorders[i].Valid ? recorders[i].LastValue / (i >= 7 ? 1000000.0 : 1) : -1).ToString("F3",CultureInfo.InvariantCulture);
                // Buffer disk writes until the existing periodic flush.
                spikeRows[spikeCount++] = DateTime.UtcNow.ToString("O") + "," + SceneHelper.CurrentScene + "," + frameMs.ToString("F3",CultureInfo.InvariantCulture) + "," + (currentCollections-collectionCount) + "," + FramePacing.RequestedRate + "," + QualitySettings.vSyncCount + "," + metric(5) + "," + metric(6) + "," + metric(7) + "," + metric(8) + "," + metric(9) + "," + metric(11) + "," + metric(12) + "," + metric(13) + "," + FrameScopes.PortalCameraPasses + "," + FrameScopes.PortalAuxiliaryPasses;
            }
            collectionCount = currentCollections;
            portalPassTotal += FrameScopes.PortalCameraPasses;
            auxiliaryPassTotal += FrameScopes.PortalAuxiliaryPasses;
            for (int i=0;i<recorders.Length;i++) if (recorders[i].Valid) totals[i] += recorders[i].LastValue;
            if (now-windowStart < 5 && count < frameTimes.Length) return;
            double sum=0;for(int i=0;i<count;i++)sum+=frameTimes[i];Array.Sort(frameTimes,0,count);
            string number(double x) => x.ToString("F3",CultureInfo.InvariantCulture);
            log.Write(DateTime.UtcNow.ToString("O")+","+SceneManager.GetActiveScene().name+","+QualitySettings.vSyncCount+","+FramePacing.RequestedRate+","+FramePacing.Precise+","+Screen.width+","+Screen.height+","+count+","+number(sum/count)+","+number(frameTimes[Math.Min(count-1,(int)(count*.95))])+","+number(frameTimes[count-1]));
            for(int i=0;i<totals.Length;i++) { log.Write(","+number(recorders[i].Valid ? totals[i]/count/(i<=3 || i>=7 ? 1000000.0 : 1) : -1)); totals[i]=0; }
            var post = MonoSingleton<PostProcessV2_Handler>.Instance;
            if (timingCount > 0) Array.Sort(gpuTimes, 0, timingCount);
            double average(double total) => timingCount > 0 ? total/timingCount : -1;
            log.WriteLine("," + JobsUtility.JobWorkerCount + "," + JobTuning.NativeBufferQueries + "," + number(portalPassTotal/count) + "," + number(auxiliaryPassTotal/count) + "," + (post && post.mainTex ? post.mainTex.width : 0) + "," + (post && post.mainTex ? post.mainTex.height : 0) + "," + number(average(gpuTotal)) + "," + number(timingCount > 0 ? gpuTimes[Math.Min(timingCount-1,(int)(timingCount*.95))] : -1) + "," + number(average(cpuFrameTotal)) + "," + number(average(cpuActiveTotal)) + "," + number(average(renderActiveTotal)) + "," + number(average(presentWaitTotal)) + "," + timingCount);log.Flush();
            portalPassTotal = auxiliaryPassTotal = 0;
            timingCount = 0; gpuTotal = cpuFrameTotal = cpuActiveTotal = renderActiveTotal = presentWaitTotal = 0;
            for (int i=0;i<spikeCount;i++) { spikes.WriteLine(spikeRows[i]); spikeRows[i]=null; }
            spikes.Flush();spikeCount=0;count=0;windowStart=now;
        }
        void OnDestroy()
        {
            if(recorders!=null)foreach(var r in recorders)r.Dispose();
            if(spikes!=null) { for(int i=0;i<spikeCount;i++)spikes.WriteLine(spikeRows[i]); spikes.Dispose();spikes=null; }
            log?.Dispose();log=null;
        }
    }
}
