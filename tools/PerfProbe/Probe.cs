using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace ULTRAKILL.MacPort
{
    public static class Probe
    {
        static bool enabled;
        static int phase = -1, frames;
        static float next = 18, warmUntil;
        static double last, mutationsMs;
        static Vector3 surface;
        static int[] amounts = { 0, 2000, 10000, 30000, 60000 };
        static bool particles;
        static Bloodsplatter blood;
        static double emitCarry;
        static readonly List<double> frameTimes = new List<double>();
        static readonly Dictionary<string, ProfilerRecorder> recorders = new Dictionary<string, ProfilerRecorder>();
        static readonly Dictionary<string, List<double>> samples = new Dictionary<string, List<double>>();
        static readonly FieldInfo count = typeof(BloodsplatterManager).GetField("currentBloodCount", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo cursor = typeof(BloodsplatterManager).GetField("propIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        public static void Begin(string name) { if (enabled) Profiler.BeginSample(name); }
        public static void End() { if (enabled) Profiler.EndSample(); }
        public static void Install()
        {
            enabled = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_PERF_PROBE") == "1";
            particles = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_PERF_PARTICLES") == "1";
            if (particles) amounts = new[] { 0, 5000, 20000, 50000 };
            if (enabled) { Debug.Log("[PerfProbe] Installed."); Camera.onPreCull += View; }
        }
        static void View(Camera cam)
        {
            if (!enabled || phase < 0 || cam != MonoSingleton<CameraController>.Instance.cam) return;
            cam.transform.rotation=Quaternion.LookRotation(surface-cam.transform.position,Vector3.up);
        }
        public static void Tick()
        {
            if (!enabled) return;
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            double dt = last == 0 ? 0 : (now - last) * 1000; last = now;
            try
            {
                var b = MonoSingleton<BloodsplatterManager>.Instance;
                if (!b || !b.props.IsCreated || Time.unscaledTime < 18) return;
                if (phase < 0)
                {
                    QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
                    var movement = MonoSingleton<NewMovement>.Instance;
                    movement.activated = false; movement.enabled = false; movement.rb.isKinematic = true;
                    Vector3 pos = movement.transform.position;
                    surface = pos + Vector3.forward * 8;
                    foreach (var h in Physics.RaycastAll(surface + Vector3.up * 3, Vector3.down, 20))
                        if (h.collider.CompareTag("Floor") || h.collider.CompareTag("Wall")) { surface = h.point; break; }
                    var handles = new List<ProfilerRecorderHandle>(); ProfilerRecorderHandle.GetAvailable(handles);
                    var descriptions = new List<string>();
                    foreach (var h in handles)
                    {
                        var d = ProfilerRecorderHandle.GetDescription(h); descriptions.Add(d.Category.Name + " / " + d.Name);
                        if (d.Name.StartsWith("MacBlood.") || d.Name.Contains("GenerateBloodMeshJob") || d.Name == "PlayerLoop" || d.Name == "Main Thread" || d.Name == "Render Thread" || d.Name == "Gfx.WaitForPresentOnGfxThread" || d.Name == "WaitForJobGroupID" || d.Name == "GC.Alloc" || d.Name == "Draw Calls Count" || d.Name == "Triangles Count" || d.Name == "Vertices Count")
                        {
                            recorders[d.Name] = ProfilerRecorder.StartNew(d.Category, d.Name, 1);
                            samples[d.Name] = new List<double>();
                        }
                    }
                    File.WriteAllLines(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_PERF_MARKERS"), descriptions);
                    Debug.Log("[PerfProbe] scene=" + SceneHelper.CurrentScene + " size=" + Screen.width + "x" + Screen.height + " bloodGPU=" + b.usedComputeShadersAtStart + " shader=" + b.stainMat.shader.name + " instancing=" + b.stainMat.enableInstancing + " keywords=" + string.Join(",", b.stainMat.shaderKeywords));
                    Debug.Log("[PerfProbe] stainMesh bounds=" + b.stainMesh.bounds + " particle_mode=" + particles);
                    if (particles)
                    {
                        var gore = b.GetGore(GoreType.Small);
                        gore.transform.SetParent(new GameObject("Performance gore zone").AddComponent<GoreZone>().transform, true);
                        gore.transform.position = surface + Vector3.up;
                        gore.SetActive(true); blood = gore.GetComponent<Bloodsplatter>(); blood.GetReady();
                        var main = blood.part.main; main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = 12000; main.loop = true; main.stopAction = ParticleSystemStopAction.None;
                        var emission = blood.part.emission; emission.enabled = false;
                        blood.part.Clear(); blood.part.Play();
                        b.overrideBloodstainChance = true; b.bloodstainChance = 100;
                    }
                }
                if (Time.unscaledTime >= next)
                {
                    if (phase >= 0) Summarize(b);
                    phase++;
                    if (phase == amounts.Length)
                    {
                        Debug.Log("[PerfProbe] PASS: benchmark complete.");
                        foreach (var r in recorders.Values) r.Dispose(); enabled = false; return;
                    }
                    int n = (int)count.GetValue(b);
                    for (int i = n; i < amounts[phase]; i++)
                        b.CreateBloodstain(surface + new Vector3(i % 100 * .04f - 2, 0, i / 100 % 100 * .04f - 2), Vector3.up, true);
                    frameTimes.Clear(); foreach (var values in samples.Values) values.Clear();
                    frames = 0; mutationsMs = 0;
                    warmUntil = Time.unscaledTime + 3; next = Time.unscaledTime + 11;
                    Debug.Log("[PerfProbe] Phase " + phase + " stains=" + count.GetValue(b));
                }
                if (particles && amounts[phase] > 0)
                {
                    // Keep a steady stream of real particle collisions against
                    // the level floor, independent of the benchmark frame rate.
                    emitCarry += dt * 2;
                    int emit = Math.Min(2000, (int)emitCarry); emitCarry -= emit;
                    blood.part.Emit(new ParticleSystem.EmitParams { position = surface + Vector3.up, velocity = Vector3.down * 4, startLifetime = 3, startSize = .1f }, emit);
                }
                else if (amounts[phase] > 0)
                {
                    var sw = Stopwatch.StartNew();
                    cursor.SetValue(b, frames * 8 % (amounts[phase] - 8));
                    for (int i = 0; i < 8; i++) b.CreateBloodstain(surface + new Vector3((frames+i) % 100 * .04f - 2, 0, (frames+i) / 100 % 100 * .04f - 2), Vector3.up, true);
                    count.SetValue(b, amounts[phase]);
                    sw.Stop(); if (Time.unscaledTime >= warmUntil) mutationsMs += sw.Elapsed.TotalMilliseconds;
                }
                if (Time.unscaledTime >= warmUntil)
                {
                    frames++; frameTimes.Add(dt);
                    foreach (var pair in recorders) if (pair.Value.Valid) samples[pair.Key].Add(pair.Value.LastValue);
                }
            }
            catch (Exception e) { Debug.LogError("[PerfProbe] FAIL " + e); enabled = false; }
        }
        static void Summarize(BloodsplatterManager b)
        {
            frameTimes.Sort();
            if (frames == 0) throw new Exception("No benchmark samples.");
            double sum = 0; foreach (var f in frameTimes) sum += f;
            Debug.Log("[PerfProbe] RESULT phase=" + phase + " target=" + amounts[phase] + " stains=" + count.GetValue(b) + " frames=" + frames + " mean_ms=" + (sum/frames).ToString("F3", CultureInfo.InvariantCulture) + " p95_ms=" + frameTimes[Math.Min(frames-1, (int)(frames*.95))].ToString("F3", CultureInfo.InvariantCulture) + " mutation_ms=" + (mutationsMs/frames).ToString("F3", CultureInfo.InvariantCulture));
            foreach (var p in samples)
            {
                double value = 0; foreach (var v in p.Value) value += v;
                Debug.Log("[PerfProbe] COUNTER phase=" + phase + " name=" + p.Key + " mean=" + (value/Math.Max(1,p.Value.Count)).ToString("F3", CultureInfo.InvariantCulture));
            }
        }
    }
}
