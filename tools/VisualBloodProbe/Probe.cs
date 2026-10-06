using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Jobs;
using Debug = UnityEngine.Debug;

namespace ULTRAKILL.MacPort
{
    public static class Probe
    {
        static bool enabled, requested;
        static int stage, failures;
        static float next = 15;
        static Vector3 target, normal;
        static int emptyPixels, gpuPixels, referencePixels;
        static readonly FieldInfo count = typeof(BloodsplatterManager).GetField("currentBloodCount", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo commands = typeof(PostProcessV2_Handler).GetField("bloodOilCB", BindingFlags.NonPublic | BindingFlags.Instance);
        public static void Begin(string name) { }
        public static void End() { }
        public static void Install()
        {
            enabled = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_PERF_PROBE") == "1";
            if (enabled) Camera.onPreCull += View;
        }
        static void View(Camera cam)
        {
            if (!enabled || stage == 0 || cam != MonoSingleton<CameraController>.Instance.cam) return;
            cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
        }
        public static void Tick()
        {
            if (!enabled || Time.unscaledTime < next) return;
            try
            {
                var b = MonoSingleton<BloodsplatterManager>.Instance;
                if (!b || !b.props.IsCreated) return;
                if (stage == 0)
                {
                    var movement = MonoSingleton<NewMovement>.Instance;
                    movement.enabled = false; movement.rb.isKinematic = true;
                    var cam = MonoSingleton<CameraController>.Instance.cam;
                    var ray = cam.ViewportPointToRay(new Vector3(.5f, .25f, 0));
                    RaycastHit best = default; bool found = false;
                    foreach (var hit in Physics.RaycastAll(ray, 30))
                        if (hit.collider.CompareTag("Floor") || hit.collider.CompareTag("Wall"))
                            if (!found || hit.distance < best.distance) { best = hit; found = true; }
                    if (!found) throw new Exception("No visible stain surface found.");
                    target = best.point; normal = best.normal;
                    Debug.Log("[VisualBlood] Target " + best.collider.name + " " + target + " normal=" + normal);
                    stage = 1;
                }
                else if (stage == 1)
                {

                    for (int i = 0; i < 9; i++) b.CreateBloodstain(target + new Vector3(i % 3 * .25f, 0, i / 3 * .25f), normal, true);
                    stage = 2;
                }
                else if (stage == 2)
                {
                    int n = (int)count.GetValue(b);
                    var data = Mesh.AllocateWritableMeshData(1); var md=data[0];
                    md.SetVertexBufferParams(n*4,new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3),new VertexAttributeDescriptor(VertexAttribute.Normal,VertexAttributeFormat.Float16,4),new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float16,2),new VertexAttributeDescriptor(VertexAttribute.TexCoord1,VertexAttributeFormat.Float32,3));
                    md.SetIndexBufferParams(n*6,IndexFormat.UInt32);
                    new GenerateBloodMeshJob { props=b.props,meshData=md,isUInt16=false }.Run(n);
                    var reference = new Mesh(); Mesh.ApplyAndDisposeWritableMeshData(data,reference);
                    reference.subMeshCount=1;reference.SetSubMesh(0,new SubMeshDescriptor(0,n*6)); reference.RecalculateBounds();
                    b.totalStainMesh=reference; b.meshDirty=false;
                    Debug.Log("[VisualBlood] Switched to original full mesh job for " + n + " stains.");
                    stage = 3;
                }
                else if (stage == 3)
                {
                    Debug.Log("[VisualBlood] " + (failures == 0 && gpuPixels > emptyPixels ? "PASS" : "FAIL") + ": cached blood coverage matches original mesh job; failures=" + failures);
                    enabled = false;
                }
                requested = false; next = Time.unscaledTime + 8;
            }
            catch (Exception e) { Debug.LogError("[VisualBlood] FAIL " + e); enabled = false; }
        }
        public static void OnBloodPrepared(Camera cam)
        {
            if (!enabled || requested || stage == 0 || Time.unscaledTime < next - 3) return;
            var p = MonoSingleton<PostProcessV2_Handler>.Instance;
            if (cam != p.mainCam) return;
            requested = true; int captureStage = stage;
            var cb = (CommandBuffer)commands.GetValue(p);
            cb.RequestAsyncReadback(p.reusableBufferB, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError) { Debug.LogError("[VisualBlood] Readback error"); failures++; return; }
                var bytes = request.GetData<byte>(); int redPixels = 0, peak = 0;
                for (int i = 0; i < bytes.Length; i += 4) { if (bytes[i] != 0) redPixels++; peak = Math.Max(peak, bytes[i]); }
                if (captureStage == 1) emptyPixels = redPixels;
                if (captureStage == 2) gpuPixels = redPixels;
                if (captureStage == 3) { referencePixels=redPixels; if (redPixels==emptyPixels || Math.Abs(redPixels-gpuPixels)>Math.Max(10,redPixels/100)) failures++; }
                Debug.Log("[VisualBlood] MASK stage=" + captureStage + " pixels=" + redPixels + " peak=" + peak + " mode=" + (captureStage == 2 ? "cached" : captureStage == 3 ? "original" : "empty"));
            });
        }
    }
}
