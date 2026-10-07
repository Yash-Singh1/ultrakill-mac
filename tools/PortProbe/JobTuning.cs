using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace ULTRAKILL.MacPort
{
    public static class JobTuning
    {
        public static bool NativeBufferQueries = true;
        #if FRAUD_TEST_TOOLS || PORTAL_OPTIMIZATION
        public static bool PortalFlushEnabled = true;
        public static bool PortalAsyncVisibility;
        public static long PortalWaitTicks;
        public static int PortalWaitCalls;
        #endif
        public static int OriginalWorkers { get; private set; }

        public static void Install(GameObject root)
        {
            OriginalWorkers = JobsUtility.JobWorkerCount;
            root.AddComponent<JobComparison>();
        }

        public static IntPtr NativeBufferQuery(ref RenderBuffer buffer)
        {
            return NativeBufferQueries ? buffer.GetNativeRenderBufferPtr() : IntPtr.Zero;
        }
        #if FRAUD_TEST_TOOLS || PORTAL_OPTIMIZATION
        public static void PortalFlush() { if (PortalFlushEnabled) GL.Flush(); }
        public static void PortalWait(ref AsyncGPUReadbackRequest request)
        {
            if (PortalAsyncVisibility || PortalVisibilityCache.CanReuse) return;
            long start = Stopwatch.GetTimestamp(); request.WaitForCompletion();
            PortalWaitTicks += Stopwatch.GetTimestamp()-start; PortalWaitCalls++;
        }

        public static void PortalVisibility(ref NativeList<ULTRAKILL.Portal.PortalRenderV2.OnscreenPortalData> data, ulong bitset, ref NativeArray<bool> visibility, ref AsyncGPUReadbackRequest request)
        {
            // Pending visibility must never reuse indices from another frame.
            // The GPU stencil still limits each camera's output to its portal.
            if (PortalVisibilityCache.Enabled) bitset=PortalVisibilityCache.Resolve(ref data,bitset,ref request);
            else if (PortalAsyncVisibility && (!request.done || request.hasError)) bitset = ulong.MaxValue;
            ULTRAKILL.Portal.PortalRenderV2.UpdateOcclusionBurst(ref data, bitset, ref visibility);
        }

        #endif
        public sealed class JobComparison : MonoBehaviour
        {
            string control;
            string last;
            float next;
            bool active, audioPaused;
            float volume;
            void Start()
            {
                control = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_CONTROL") ?? Path.Combine(Path.GetDirectoryName(PortPaths.Diagnostics(Application.dataPath)), "runtime-state", "fraud-control.txt");
                active = File.Exists(control);
                audioPaused = AudioListener.pause;
                volume = AudioListener.volume;
            }
            void Update()
            {
                if (!active || Time.unscaledTime < next) return;
                next = Time.unscaledTime + .5f;
                try
                {
                    if (!File.Exists(control))
                    {
                        JobsUtility.JobWorkerCount = OriginalWorkers;
                        NativeBufferQueries = true;
                        AudioListener.pause = audioPaused;
                        AudioListener.volume = volume;
                        active = false;
                        return;
                    }
                    string text = File.ReadAllText(control).Trim();
                    if (text == last) return;
                    string[] fields = text.Split(',');
                    if (fields.Length != 2 || !int.TryParse(fields[0], out int workers) || workers < 1 || workers > JobsUtility.JobWorkerMaximumCount) return;
                    JobsUtility.JobWorkerCount = workers;
                    NativeBufferQueries = fields[1] == "1";
                    AudioListener.pause = true;
                    AudioListener.volume = 0;
                    last = text;
                    UnityEngine.Debug.Log("[MacFraudComparison] workers=" + workers + " native_buffer_queries=" + NativeBufferQueries + "; audio muted, settings unchanged.");
                }
                catch (IOException) { }
            }
        }

        [DllImport("libc", EntryPoint = "sysctlbyname")]
        static extern int Sysctl(string name, out int value, ref UIntPtr size, IntPtr newValue, UIntPtr newSize);

        public static int PerformanceCores()
        {
            try
            {
                UIntPtr size = (UIntPtr)4;
                return Sysctl("hw.perflevel0.physicalcpu", out int value, ref size, IntPtr.Zero, UIntPtr.Zero) == 0 ? value : 0;
            }
            catch { return 0; }
        }

        public static void Benchmark()
        {
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_RENDER_JOB_BENCH") == "1")
            {
                RenderBenchmark();
                return;
            }
            if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_JOB_BENCH") != "1") return;
            int original = JobsUtility.JobWorkerCount;
            using (var data = new NativeArray<float>(2048, Allocator.Persistent))
            {
                foreach (int requested in new[] { original, 8, 4, 2, 1, 4, original })
                {
                    int workers = Math.Min(requested, JobsUtility.JobWorkerMaximumCount);
                    JobsUtility.JobWorkerCount = workers;
                    for (int i = 0; i < 20; i++) new Work { values = data }.Schedule(data.Length, 32).Complete();
                    var times = new double[200];
                    for (int i = 0; i < times.Length; i++)
                    {
                        long begin = Stopwatch.GetTimestamp();
                        for (int j = 0; j < 8; j++) new Work { values = data }.Schedule(data.Length, 32).Complete();
                        times[i] = (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency;
                    }
                    double sum = 0; foreach (double t in times) sum += t;
                    Array.Sort(times);
                    UnityEngine.Debug.Log("[MacJobBench] workers=" + workers + " performance_cores=" + PerformanceCores() + " mean_ms=" + (sum / times.Length).ToString("F4", CultureInfo.InvariantCulture) + " p95_ms=" + times[190].ToString("F4", CultureInfo.InvariantCulture));
                }
            }
            JobsUtility.JobWorkerCount = original;
            UnityEngine.Debug.Log("[MacJobBench] Complete. Synthetic job-fence workload; not a gameplay FPS measurement.");
            Application.Quit();
        }

        static void RenderBenchmark()
        {
            int original = JobsUtility.JobWorkerCount;
            var root = new GameObject("Isolated render-job benchmark");
            var camera = root.AddComponent<Camera>();
            camera.enabled = false;
            camera.useOcclusionCulling = false;
            camera.transform.position = new Vector3(0, 0, -30);
            var texture = new RenderTexture(64, 64, 24);
            camera.targetTexture = texture;
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            var material = new Material(primitive.GetComponent<MeshRenderer>().sharedMaterial);
            material.color = Color.white;
            UnityEngine.Object.DestroyImmediate(primitive);
            for (int i = 0; i < 1000; i++)
            {
                var obj = new GameObject("Benchmark mesh");
                obj.transform.SetParent(root.transform, false);
                obj.transform.localPosition = new Vector3((i % 20) - 10, (i / 20 % 20) - 10, i / 400 * 2);
                obj.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            try
            {
                foreach (int requested in new[] { original, 8, 4, 2, 1, 4, original })
                {
                    int workers = Math.Min(requested, JobsUtility.JobWorkerMaximumCount);
                    JobsUtility.JobWorkerCount = workers;
                    for (int i = 0; i < 10; i++) camera.Render();
                    var times = new double[80];
                    for (int i = 0; i < times.Length; i++)
                    {
                        long begin = Stopwatch.GetTimestamp();
                        camera.Render();
                        times[i] = (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency;
                    }
                    double sum = 0; foreach (double t in times) sum += t;
                    Array.Sort(times);
                    UnityEngine.Debug.Log("[MacRenderJobBench] workers=" + workers + " mean_ms=" + (sum / times.Length).ToString("F4", CultureInfo.InvariantCulture) + " p95_ms=" + times[76].ToString("F4", CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                JobsUtility.JobWorkerCount = original;
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(material);
            }
            UnityEngine.Debug.Log("[MacRenderJobBench] Complete. Offscreen native culling/render workload; not a gameplay FPS measurement.");
            Application.Quit();
        }

        struct Work : IJobParallelFor
        {
            public NativeArray<float> values;
            public void Execute(int i)
            {
                float value = values[i];
                for (int j = 0; j < 8; j++) value = value * 0.99f + i * 0.00001f;
                values[i] = value;
            }
        }
    }
}
