using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Jobs.LowLevel.Unsafe;

namespace ULTRAKILL.MacPort
{
    // Explicit isolated-test mode. Does not run during ordinary launches.
    [DefaultExecutionOrder(32000)]
    public sealed class FraudBenchmark : MonoBehaviour
    {
        readonly int[] workers = { 17, 4, 4, 17 };
        readonly bool[] native = { false, true, true, false };
        readonly bool[] queries = { true, true, true, true };
        readonly List<double> times = new List<double>();
        int phase = -1, initialWorkers;
        double readyAt, warmUntil, endAt, previous;
        bool started;
        float originalTimeScale;
        Camera view;
        Vector3 viewPosition;
        Quaternion viewRotation;
        static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        void Start() { initialWorkers = JobsUtility.JobWorkerCount; }
        void LateUpdate()
        {
            double now = Now;
            if (string.IsNullOrEmpty(SceneHelper.CurrentScene) || !SceneHelper.CurrentScene.StartsWith("Level 8") || SceneManager.GetActiveScene().name == "Bootstrap") return;
            if (!started)
            {
                started = true; readyAt = now + 15;
                UnityEngine.Debug.Log("[MacFraudBench] Scene ready; warming shaders before comparison.");
            }
            if (now < readyAt) return;
            if (phase < 0)
            {
                originalTimeScale = Time.timeScale; Time.timeScale = 0;
                FramePacing.BenchmarkUncapped();
                view = MonoSingleton<CameraController>.Instance.cam;
                viewPosition = view.transform.position; viewRotation = view.transform.rotation;
                int best = -1;
                var portalManager = MonoSingleton<ULTRAKILL.Portal.PortalManagerV2>.Instance;
                var portals = new List<ULTRAKILL.Portal.Portal>(UnityEngine.Object.FindObjectsOfType<ULTRAKILL.Portal.Portal>());
                var player = MonoSingleton<NewMovement>.Instance.transform;
                UnityEngine.Debug.Log("[MacFraudBench] Active portal components=" + portals.Count);
                int tested = 0;
                foreach (var portal in portals.ToArray())
                {
                    UnityEngine.Debug.Log("[MacFraudBench] Portal=" + portal.name + " entry=" + (portal.entry ? portal.entry.name + ":" + portal.entry.gameObject.activeInHierarchy : "missing") + " exit=" + (portal.exit ? portal.exit.name + ":" + portal.exit.gameObject.activeInHierarchy : "missing"));
                    if (!portal.entry || !portal.exit) continue;
                    foreach (var endpoint in new[] { portal.entry, portal.exit })
                        for (var ancestor = endpoint; ancestor; ancestor = ancestor.parent) ancestor.gameObject.SetActive(true);
                    player.position = portal.entry.position;
                    portals = new List<ULTRAKILL.Portal.Portal>(UnityEngine.Object.FindObjectsOfType<ULTRAKILL.Portal.Portal>());
                    portalManager.Scene.Sync(portals);
                    var nativePortals = portalManager.Scene.nativeScene.renderPortals;
                    for (int portalIndex = 0; portalIndex < nativePortals.Length; portalIndex++)
                    {
                        var native = nativePortals[portalIndex];
                        var centerNative = (native.vertices[0] + native.vertices[1] + native.vertices[2] + native.vertices[3]) * .25f;
                        Vector3 center = new Vector3(centerNative.x, centerNative.y, centerNative.z);
                        var normalNative = native.plane.Normal;
                        Vector3 normal = new Vector3(normalNative.x, normalNative.y, normalNative.z);
                        if (normal.sqrMagnitude < .5f) continue;
                        Vector3 position = center + normal * 2;
                        Quaternion rotation = Quaternion.LookRotation(-normal, Math.Abs(normal.y) > .9f ? Vector3.forward : Vector3.up);
                        view.transform.SetPositionAndRotation(position, rotation);
                        view.ResetCullingMatrix(); view.ResetWorldToCameraMatrix();
                        portalManager.render.Setup(portalManager.Scene, view, portalManager.portalCamera);
                        view.Render();
                        int count = portalManager.render.renderDatas.Length;
                        if (count > best) { best = count; viewPosition = position; viewRotation = rotation; }
                    }
                    if (++tested >= 24) break;
                }
                player.position = viewPosition;
                portalManager.Scene.Sync(portals);
                UnityEngine.Debug.Log("[MacFraudBench] Fixed heavy portal view renders=" + best + " position=" + viewPosition + " rotation=" + viewRotation.eulerAngles);
                Advance(now);
            }
            else if (now >= endAt)
            {
                times.Sort(); double sum = 0; foreach (double t in times) sum += t;
                UnityEngine.Debug.Log("[MacFraudBench] RESULT phase=" + phase + " workers=" + JobsUtility.JobWorkerCount + " native_burst=" + Unity.Burst.BurstCompiler.IsEnabled + " native_queries=" + JobTuning.NativeBufferQueries + " frames=" + times.Count + " mean_ms=" + (sum / Math.Max(1, times.Count)).ToString("F4", CultureInfo.InvariantCulture) + " p95_ms=" + times[Math.Min(times.Count - 1, (int)(times.Count * .95))].ToString("F4", CultureInfo.InvariantCulture));
                Advance(now);
                if (phase == workers.Length) return;
            }
            if (previous != 0 && now >= warmUntil) times.Add((now - previous) * 1000);
            previous = now;
            view.transform.SetPositionAndRotation(viewPosition, viewRotation);
            // Batch players do not present a game window. Invoke the same camera
            // callbacks explicitly to exercise the level's real portal renderer.
            if (Application.isBatchMode)
            {
                var camera = MonoSingleton<CameraController>.Instance.cam;
                var manager = MonoSingleton<ULTRAKILL.Portal.PortalManagerV2>.Instance;
                manager.render.Setup(manager.Scene, camera, manager.portalCamera);
                camera.Render();
            }
        }
        void Advance(double now)
        {
            phase++;
            if (phase == workers.Length)
            {
                JobsUtility.JobWorkerCount = initialWorkers; JobTuning.NativeBufferQueries = true; Unity.Burst.BurstCompiler.Options.EnableBurstCompilation = true;
                Time.timeScale = originalTimeScale;
                UnityEngine.Debug.Log("[MacFraudBench] Complete. Fixed-view real level rendering, isolated profile.");
                enabled = false; Application.Quit(); return;
            }
            JobsUtility.JobWorkerCount = Math.Min(workers[phase], JobsUtility.JobWorkerMaximumCount);
            JobTuning.NativeBufferQueries = queries[phase];
            Unity.Burst.BurstCompiler.Options.EnableBurstCompilation = native[phase];
            times.Clear(); previous = 0; warmUntil = now + 3; endAt = now + 11;
            UnityEngine.Debug.Log("[MacFraudBench] Starting phase=" + phase + " workers=" + JobsUtility.JobWorkerCount + " native_burst=" + Unity.Burst.BurstCompiler.IsEnabled + " native_queries=" + JobTuning.NativeBufferQueries);
        }
    }
}
