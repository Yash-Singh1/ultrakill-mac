using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Jobs.LowLevel.Unsafe;

namespace ULTRAKILL.MacPort
{
    // Explicit isolated-test mode. Does not run during ordinary launches.
    [DefaultExecutionOrder(32000)]
    public sealed class FraudBenchmark : MonoBehaviour
    {
        int[] workers = { 4, 4, 4, 4 };
        bool[] queries = { true, false, false, true };
        bool[] viewports = { false, false, false, false };
        bool[] flushes = { true, true, true, true };
        bool[] meshes = { false, false, false, false };
        bool[] asyncVisibility = { false, false, false, false };
        bool[] visibilityCaches = { false, false, false, false };
        readonly List<double> times = new List<double>();
        int phase = -1, initialWorkers;
        double readyAt, warmUntil, endAt, previous;
        bool started;
        float originalTimeScale;
        Camera view;
        Vector3 viewPosition;
        Quaternion viewRotation;
        Texture2D pixel;
        static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        public static int BenchmarkWidth() => int.Parse(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_WIDTH") ?? "1728", CultureInfo.InvariantCulture);
        public static int BenchmarkHeight() => int.Parse(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_HEIGHT") ?? "1117", CultureInfo.InvariantCulture);
        void Start()
        {
            initialWorkers = JobsUtility.JobWorkerCount;
            string phases = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_PHASES");
            if (phases == null) return;
            var fields = phases.Split(',');
            workers = new int[fields.Length]; queries = new bool[fields.Length]; viewports = new bool[fields.Length]; flushes = new bool[fields.Length]; meshes = new bool[fields.Length]; asyncVisibility = new bool[fields.Length]; visibilityCaches=new bool[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var values = fields[i].Split(':');
                workers[i] = int.Parse(values[0], CultureInfo.InvariantCulture);
                queries[i] = values[1] == "1";
                viewports[i] = values.Length > 2 && values[2] == "1";
                flushes[i] = values.Length < 4 || values[3] == "1";
                meshes[i] = values.Length > 4 && values[4] == "1";
                asyncVisibility[i] = values.Length > 5 && values[5] == "1";
                visibilityCaches[i] = values.Length > 5 && values[5] == "2";
            }
        }
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
                MonoSingleton<CameraController>.Instance.enabled = false;
                MonoSingleton<NewMovement>.Instance.enabled = false;
                foreach(var flipper in UnityEngine.Object.FindObjectsOfType<PortalFlipper>()) flipper.enabled=false;
                view.aspect = BenchmarkWidth() / (float)BenchmarkHeight();
                viewPosition = view.transform.position; viewRotation = view.transform.rotation;
                if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_RENDER_CHECK") == "1")
                {
                    CheckRenderTargets(); enabled = false; Application.Quit(); return;
                }
                string fixturePath=Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_FIXTURE");
                if(fixturePath!=null)
                {
                    var fixture=Fixtures.Replay.Apply(fixturePath);
                    viewPosition=fixture.cameraPosition;viewRotation=fixture.cameraRotation;
                    pixel=new Texture2D(1,1,TextureFormat.RGBA32,false);
                    var fixtureManager=MonoSingleton<ULTRAKILL.Portal.PortalManagerV2>.Instance;
                    fixtureManager.render.Setup(fixtureManager.Scene,view,fixtureManager.portalCamera);view.Render();
                    UnityEngine.Debug.Log("[MacFraudFixture] Rendered candidates="+fixtureManager.render.renderDatas.Length+" passes="+FrameScopes.PortalCameraPasses+" image="+HasRenderedImage());
                    CompareImmediate();enabled=false;Application.Quit();return;
                }
                int best = -1;
                double bestCost = -1;
                pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                var portalManager = MonoSingleton<ULTRAKILL.Portal.PortalManagerV2>.Instance;
                var portals = new List<ULTRAKILL.Portal.Portal>(UnityEngine.Object.FindObjectsOfType<ULTRAKILL.Portal.Portal>());
                var movement = MonoSingleton<NewMovement>.Instance;
                var player = movement.transform;
                UnityEngine.Debug.Log("[MacFraudBench] Active portal components=" + portals.Count);
                int tested = 0;
                foreach (var portal in portals.ToArray())
                {
                    UnityEngine.Debug.Log("[MacFraudBench] Portal=" + portal.name + " entry=" + (portal.entry ? portal.entry.name + ":" + portal.entry.gameObject.activeInHierarchy : "missing") + " exit=" + (portal.exit ? portal.exit.name + ":" + portal.exit.gameObject.activeInHierarchy : "missing"));
                    if (!portal.entry || !portal.exit) continue;
                    foreach (var endpoint in new[] { portal.entry, portal.exit })
                        for (var ancestor = endpoint; ancestor; ancestor = ancestor.parent) ancestor.gameObject.SetActive(true);
                    player.position = portal.entry.position;
                    portalManager.UpdateTraveller(movement);
                    portals = new List<ULTRAKILL.Portal.Portal>(UnityEngine.Object.FindObjectsOfType<ULTRAKILL.Portal.Portal>());
                    portalManager.Scene.Sync(portals);
                    var nativePortals = portalManager.Scene.nativeScene.renderPortals.AsArray().ToArray();
                    for (int portalIndex = 0; portalIndex < nativePortals.Length; portalIndex++)
                    {
                        var native = nativePortals[portalIndex];
                        var centerNative = (native.vertices[0] + native.vertices[1] + native.vertices[2] + native.vertices[3]) * .25f;
                        Vector3 center = new Vector3(centerNative.x, centerNative.y, centerNative.z);
                        var normalNative = native.plane.Normal;
                        Vector3 normal = new Vector3(normalNative.x, normalNative.y, normalNative.z);
                        if (normal.sqrMagnitude < .5f) continue;
                        float baseDistance=float.Parse(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_DISTANCE") ?? "2",CultureInfo.InvariantCulture);
                        foreach(float distance in Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_SCAN_ANGLES")=="1" ? new[]{baseDistance,baseDistance*2,baseDistance*4} : new[]{baseDistance})
                        foreach(float yaw in Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_SCAN_ANGLES")=="1" ? new[]{0f,-30f,30f,-60f,60f} : new[]{0f})
                        {
                        Vector3 position = center + normal * distance;
                        Quaternion rotation = Quaternion.LookRotation(-normal, Math.Abs(normal.y) > .9f ? Vector3.forward : Vector3.up) * Quaternion.Euler(0,yaw,0);
                        player.position=position; portalManager.UpdateTraveller(movement); portalManager.Scene.Sync(portals);
                        view.transform.SetPositionAndRotation(position, rotation);
                        view.ResetCullingMatrix(); view.ResetWorldToCameraMatrix();
                        portalManager.render.Setup(portalManager.Scene, view, portalManager.portalCamera);
                        view.Render();
                        int count = portalManager.render.renderDatas.Length;
                        if (FrameScopes.PortalCameraPasses < 1 || !HasRenderedImage()) continue;
                        if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_IMMEDIATE") == "1" && FrameScopes.PortalCameraPasses >= int.Parse(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_MIN_PASSES") ?? "3",CultureInfo.InvariantCulture))
                        {
                            CompareImmediate(); enabled=false; Application.Quit(); return;
                        }
                        var costs = new double[3];
                        for (int repeat = 0; repeat < 3; repeat++)
                        {
                            double begin = Now;
                            portalManager.render.Setup(portalManager.Scene, view, portalManager.portalCamera);
                            view.Render();
                            var active = RenderTexture.active;
                            RenderTexture.active = MonoSingleton<PostProcessV2_Handler>.Instance.mainTex;
                            pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); pixel.Apply();
                            RenderTexture.active = active;
                            costs[repeat] = (Now - begin) * 1000;
                        }
                        Array.Sort(costs); double cost = costs[1];
                        if (count > 0 && cost > bestCost) { bestCost = cost; best = count; viewPosition = position; viewRotation = rotation; }
                        }
                    }
                    if (++tested >= 24) break;
                }
                player.position = viewPosition;
                portalManager.UpdateTraveller(movement);
                portalManager.Scene.Sync(portals);
                var post = MonoSingleton<PostProcessV2_Handler>.Instance;
                UnityEngine.Debug.Log("[MacFraudBench] Fixed heavy portal view renders=" + best + " position=" + viewPosition + " rotation=" + viewRotation.eulerAngles + " target=" + post.mainTex.width + "x" + post.mainTex.height + " viewport=" + view.pixelRect + " occlusion=" + portalManager.render.doOcclusionPass + " scan_ms=" + bestCost);
                Advance(Now);
            }
            else if (now >= endAt)
            {
                times.Sort(); double sum = 0; foreach (double t in times) sum += t;
                {
                    string images = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_IMAGES");
                    var texture = MonoSingleton<PostProcessV2_Handler>.Instance.mainTex;
                    var active = RenderTexture.active; RenderTexture.active = texture;
                    var snapshot = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                    snapshot.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0); snapshot.Apply();
                    var colors = snapshot.GetPixels32(); int nonuniform = 0;
                    foreach (var color in colors) if (!color.Equals(colors[0])) nonuniform++;
                    if (images != null)
                    {
                        Directory.CreateDirectory(images); File.WriteAllBytes(Path.Combine(images, "phase-" + phase + ".png"), snapshot.EncodeToPNG());
                    }
                    RenderTexture.active = active; Destroy(snapshot);
                    if (nonuniform < colors.Length / 100)
                    {
                        UnityEngine.Debug.LogError("[MacFraudBench] INVALID: captured target is blank or nearly uniform. Timing cannot establish gameplay performance or image parity.");
                        enabled = false; Application.Quit(2); return;
                    }
                }
                UnityEngine.Debug.Log("[MacFraudBench] RESULT phase=" + phase + " workers=" + JobsUtility.JobWorkerCount + " native_burst=" + Unity.Burst.BurstCompiler.IsEnabled + " native_queries=" + JobTuning.NativeBufferQueries + " viewport=" + PortalViewport.Enabled + " mesh_hits=" + PortalMeshCache.Hits + " mesh_misses=" + PortalMeshCache.Misses + " frames=" + times.Count + " mean_ms=" + (sum / Math.Max(1, times.Count)).ToString("F4", CultureInfo.InvariantCulture) + " p95_ms=" + times[Math.Min(times.Count - 1, (int)(times.Count * .95))].ToString("F4", CultureInfo.InvariantCulture));
                Advance(Now);
                if (phase == workers.Length) return;
            }
            if (previous != 0 && now >= warmUntil) times.Add((now - previous) * 1000);
            previous = now;
            view.transform.SetPositionAndRotation(viewPosition, viewRotation);
            view.ResetCullingMatrix(); view.ResetWorldToCameraMatrix();
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
            PortalViewport.Enabled = viewports[phase];
            JobTuning.PortalFlushEnabled = flushes[phase]; JobTuning.PortalAsyncVisibility = asyncVisibility[phase]; PortalVisibilityCache.Enabled=visibilityCaches[phase];
            PortalMeshCache.Enabled = meshes[phase];
            PortalMeshCache.Hits = PortalMeshCache.Misses = 0;
            Unity.Burst.BurstCompiler.Options.EnableBurstCompilation = true;
            times.Clear(); previous = 0; warmUntil = now + 3; endAt = now + 11;
            UnityEngine.Debug.Log("[MacFraudBench] Starting phase=" + phase + " workers=" + JobsUtility.JobWorkerCount + " native_burst=" + Unity.Burst.BurstCompiler.IsEnabled + " native_queries=" + JobTuning.NativeBufferQueries);
        }
        void CheckRenderTargets()
        {
            var post = MonoSingleton<PostProcessV2_Handler>.Instance;
            var manager = MonoSingleton<ULTRAKILL.Portal.PortalManagerV2>.Instance;
            string images = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_IMAGES");
            Directory.CreateDirectory(images);
            UnityEngine.Debug.Log("[MacFraudRender] main mask=" + view.cullingMask + " position=" + view.transform.position + " clear=" + view.clearFlags + " renderers=" + UnityEngine.Object.FindObjectsOfType<Renderer>().Length);
            for (int mode = 0; mode < 3; mode++)
            {
                Camera camera = view;
                RenderTexture target = post.mainTex;
                GameObject fixture = null;
                if (mode == 1) view.SetTargetBuffers(post.buffers, post.depthBuffer.depthBuffer);
                if (mode == 2)
                {
                    fixture = new GameObject("Isolated render target check");
                    camera = fixture.AddComponent<Camera>(); camera.CopyFrom(view); camera.enabled = false;
                    camera.transform.SetPositionAndRotation(view.transform.position, view.transform.rotation);
                    target = new RenderTexture(BenchmarkWidth(), BenchmarkHeight(), 24); target.Create(); camera.targetTexture = target;
                    Shader.SetGlobalVector("_PortalClipPlane", Vector4.zero);
                }
                if (mode < 2) manager.render.Setup(manager.Scene, view, manager.portalCamera);
                camera.Render();
                var active = RenderTexture.active; RenderTexture.active = target;
                var snapshot = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                snapshot.ReadPixels(new Rect(0,0,target.width,target.height),0,0); snapshot.Apply();
                File.WriteAllBytes(Path.Combine(images,"probe-"+mode+".png"),snapshot.EncodeToPNG());
                var colors = snapshot.GetPixels32(); int nonuniform = 0;
                foreach(var color in colors) if (!color.Equals(colors[0])) nonuniform++;
                UnityEngine.Debug.Log("[MacFraudRender] mode=" + mode + " nonuniform=" + nonuniform + "/" + colors.Length + " portal_passes=" + FrameScopes.PortalCameraPasses);
                RenderTexture.active = active; Destroy(snapshot);
                if(fixture) { Destroy(fixture); Destroy(target); }
            }
        }
        static bool HasRenderedImage()
        {
            var scaled = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(MonoSingleton<PostProcessV2_Handler>.Instance.mainTex, scaled);
            var active = RenderTexture.active; RenderTexture.active = scaled;
            var sample = new Texture2D(32,32,TextureFormat.RGBA32,false);
            sample.ReadPixels(new Rect(0,0,32,32),0,0); sample.Apply();
            var colors = sample.GetPixels32(); int different=0;
            foreach(var color in colors) if(!color.Equals(colors[0])) different++;
            RenderTexture.active=active; Destroy(sample); RenderTexture.ReleaseTemporary(scaled);
            return different > colors.Length / 10;
        }
        void CompareImmediate()
        {
            var manager=MonoSingleton<ULTRAKILL.Portal.PortalManagerV2>.Instance;
            var post=MonoSingleton<PostProcessV2_Handler>.Instance;
            if(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_CACHE_CHECK")=="1")
            {
                CheckCacheInvalidation(manager,post); return;
            }
            string images=Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_IMAGES");
            Directory.CreateDirectory(images);
            if(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_INTERLEAVED")=="1")
            {
                CompareInterleaved(manager,post,images); return;
            }
            for(int step=0;step<workers.Length;step++)
            {
                JobsUtility.JobWorkerCount=workers[step]; JobTuning.NativeBufferQueries=queries[step]; PortalViewport.Enabled=viewports[step];
                JobTuning.PortalFlushEnabled=flushes[step]; JobTuning.PortalAsyncVisibility=asyncVisibility[step]; PortalVisibilityCache.Enabled=visibilityCaches[step]; JobTuning.PortalWaitTicks=0; JobTuning.PortalWaitCalls=0;
                PortalMeshCache.Enabled=meshes[step]; PortalMeshCache.Hits=PortalMeshCache.Misses=0;
                PortalVisibilityCache.Hits=PortalVisibilityCache.Misses=0;
                var costs=new double[80]; double passes=0, batchBegin=0;
                for(int repeat=-12;repeat<costs.Length;repeat++)
                {
                    if(repeat==0) { HasRenderedImage(); batchBegin=Now; }
                    double begin=Now;
                    manager.render.Setup(manager.Scene,view,manager.portalCamera); view.Render();
                    var active=RenderTexture.active; RenderTexture.active=post.mainTex;
                    if(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_NO_FENCE")!="1") { pixel.ReadPixels(new Rect(0,0,1,1),0,0); pixel.Apply(); } RenderTexture.active=active;
                    if(repeat>=0) { costs[repeat]=(Now-begin)*1000; passes+=FrameScopes.PortalCameraPasses; }
                }
                if(!HasRenderedImage()) throw new Exception("Immediate portal fixture became blank");
                double completedMs=(Now-batchBegin)*1000/costs.Length;
                var previous=RenderTexture.active; RenderTexture.active=post.mainTex;
                var snapshot=new Texture2D(post.mainTex.width,post.mainTex.height,TextureFormat.RGBA32,false);
                snapshot.ReadPixels(new Rect(0,0,snapshot.width,snapshot.height),0,0); snapshot.Apply();
                File.WriteAllBytes(Path.Combine(images,"immediate-"+step+".png"),snapshot.EncodeToPNG());
                RenderTexture.active=previous; Destroy(snapshot);
                Array.Sort(costs); double sum=0;foreach(double cost in costs)sum+=cost;
                UnityEngine.Debug.Log("[MacFraudBench] IMMEDIATE phase="+step+" viewport="+PortalViewport.Enabled+" workers="+workers[step]+" queries="+queries[step]+" cache="+visibilityCaches[step]+" cache_hits="+PortalVisibilityCache.Hits+" cache_misses="+PortalVisibilityCache.Misses+" flush="+flushes[step]+" mesh_hits="+PortalMeshCache.Hits+" mesh_misses="+PortalMeshCache.Misses+" mean_ms="+(sum/costs.Length).ToString("F4",CultureInfo.InvariantCulture)+" completed_ms="+completedMs.ToString("F4",CultureInfo.InvariantCulture)+" p95_ms="+costs[76].ToString("F4",CultureInfo.InvariantCulture)+" wait_ms="+(JobTuning.PortalWaitTicks*1000.0/Stopwatch.Frequency/92).ToString("F4",CultureInfo.InvariantCulture)+" wait_calls="+JobTuning.PortalWaitCalls+" passes="+passes/costs.Length+" position="+view.transform.position);
            }
        }
        void CheckCacheInvalidation(ULTRAKILL.Portal.PortalManagerV2 manager, PostProcessV2_Handler post)
        {
            var position=view.transform.position; var rotation=view.transform.rotation;
            float fov=view.fieldOfView;
            for(int step=0;step<5;step++)
            {
                PortalVisibilityCache.Enabled=false;
                view.transform.SetPositionAndRotation(position+(step==3 ? Vector3.right*.25f : Vector3.zero), rotation*Quaternion.Euler(0,step==1 ? 15 : step==2 ? -15 : 0,0));
                view.fieldOfView=step==4 ? fov+5 : fov;
                view.ResetCullingMatrix(); view.ResetWorldToCameraMatrix();
                manager.render.Setup(manager.Scene,view,manager.portalCamera); view.Render();
                // Drain and render twice so the baseline callback and target
                // contain this pose rather than the previous pose's result.
                CapturePixels(post.mainTex);
                manager.render.Setup(manager.Scene,view,manager.portalCamera); view.Render();
                var expected=CapturePixels(post.mainTex); int expectedPasses=FrameScopes.PortalCameraPasses;
                PortalVisibilityCache.Enabled=true;
                int misses=PortalVisibilityCache.Misses, hits=PortalVisibilityCache.Hits;
                manager.render.Setup(manager.Scene,view,manager.portalCamera); view.Render();
                var fresh=CapturePixels(post.mainTex);
                if(PortalVisibilityCache.Misses!=misses+1) throw new Exception("Visibility cache did not refresh for changed inputs");
                manager.render.Setup(manager.Scene,view,manager.portalCamera); view.Render();
                var reused=CapturePixels(post.mainTex);
                if(PortalVisibilityCache.Hits!=hits+1) throw new Exception("Visibility cache did not reuse identical inputs");
                int differences=0;
                for(int i=0;i<expected.Length;i++) if(!expected[i].Equals(fresh[i]) || !expected[i].Equals(reused[i])) differences++;
                if(differences!=0 || FrameScopes.PortalCameraPasses!=expectedPasses) throw new Exception("Visibility cache changed the image or portal passes");
                UnityEngine.Debug.Log("[MacPortalCacheCheck] pose="+step+" changed_pixels="+differences+" passes="+expectedPasses+" refresh_then_reuse=PASS");
            }
            // Move while keeping the cache enabled. The new inputs must miss
            // without relying on the baseline mode to invalidate the cache.
            int previousMisses=PortalVisibilityCache.Misses;
            view.transform.rotation*=Quaternion.Euler(0,10,0); view.ResetCullingMatrix(); view.ResetWorldToCameraMatrix();
            manager.render.Setup(manager.Scene,view,manager.portalCamera); view.Render(); CapturePixels(post.mainTex);
            if(PortalVisibilityCache.Misses!=previousMisses+1) throw new Exception("Camera motion reused stale visibility");
            UnityEngine.Debug.Log("[MacPortalCacheCheck] moving_camera_refresh=PASS");
            PortalVisibilityCache.Enabled=false; PortalVisibilityCache.Invalidate();
        }
        static Color32[] CapturePixels(RenderTexture texture)
        {
            var active=RenderTexture.active; RenderTexture.active=texture;
            var snapshot=new Texture2D(texture.width,texture.height,TextureFormat.RGBA32,false);
            snapshot.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0); snapshot.Apply();
            var result=snapshot.GetPixels32(); RenderTexture.active=active; Destroy(snapshot); return result;
        }
        void CompareInterleaved(ULTRAKILL.Portal.PortalManagerV2 manager, PostProcessV2_Handler post, string images)
        {
            var costs=new List<double>[workers.Length];
            var waits=new double[workers.Length];
            var passes=new int[workers.Length];
            var hits=new int[workers.Length];
            for(int i=0;i<workers.Length;i++) costs[i]=new List<double>();
            for(int round=-5;round<40;round++)
            for(int offset=0;offset<workers.Length;offset++)
            {
                int step=(offset+(round+5)%workers.Length)%workers.Length;
                JobsUtility.JobWorkerCount=workers[step]; JobTuning.NativeBufferQueries=queries[step];
                JobTuning.PortalFlushEnabled=flushes[step]; JobTuning.PortalAsyncVisibility=asyncVisibility[step]; PortalVisibilityCache.Enabled=visibilityCaches[step]; PortalViewport.Enabled=viewports[step]; PortalMeshCache.Enabled=meshes[step];
                long waitStart=JobTuning.PortalWaitTicks; int hitStart=PortalMeshCache.Hits;
                double begin=Now;
                manager.render.Setup(manager.Scene,view,manager.portalCamera); view.Render();
                var active=RenderTexture.active; RenderTexture.active=post.mainTex;
                if(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_NO_FENCE")!="1") { pixel.ReadPixels(new Rect(0,0,1,1),0,0); pixel.Apply(); } RenderTexture.active=active;
                if(round>=0)
                {
                    costs[step].Add((Now-begin)*1000);
                    waits[step]+=(JobTuning.PortalWaitTicks-waitStart)*1000.0/Stopwatch.Frequency;
                    passes[step]+=FrameScopes.PortalCameraPasses; hits[step]+=PortalMeshCache.Hits-hitStart;
                }
                if(round==0)
                {
                    if(!HasRenderedImage()) throw new Exception("Interleaved portal fixture is blank");
                    active=RenderTexture.active; RenderTexture.active=post.mainTex;
                    var snapshot=new Texture2D(post.mainTex.width,post.mainTex.height,TextureFormat.RGBA32,false);
                    snapshot.ReadPixels(new Rect(0,0,snapshot.width,snapshot.height),0,0); snapshot.Apply();
                    File.WriteAllBytes(Path.Combine(images,"interleaved-"+step+".png"),snapshot.EncodeToPNG());
                    RenderTexture.active=active; Destroy(snapshot);
                }
            }
            for(int step=0;step<workers.Length;step++)
            {
                costs[step].Sort(); double sum=0; foreach(double cost in costs[step])sum+=cost;
                UnityEngine.Debug.Log("[MacFraudBench] INTERLEAVED phase="+step+" workers="+workers[step]+" queries="+queries[step]+" viewport="+viewports[step]+" flush="+flushes[step]+" visibility_cache="+visibilityCaches[step]+" cache_hits="+PortalVisibilityCache.Hits+" cache_misses="+PortalVisibilityCache.Misses+" async_visibility="+asyncVisibility[step]+" mesh="+meshes[step]+" mesh_hits="+hits[step]+" mean_ms="+(sum/costs[step].Count).ToString("F4",CultureInfo.InvariantCulture)+" p95_ms="+costs[step][(int)(costs[step].Count*.95)].ToString("F4",CultureInfo.InvariantCulture)+" wait_ms="+(waits[step]/costs[step].Count).ToString("F4",CultureInfo.InvariantCulture)+" passes="+passes[step]/(double)costs[step].Count+" candidates="+manager.render.renderDatas.Length);
            }
        }
    }
}
