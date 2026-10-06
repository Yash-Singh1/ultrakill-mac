using System;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ULTRAKILL.MacPort
{
    public static class Probe
    {
        static bool enabled;
        static int stage;
        static float next = 12;
        static int deleted;
        static int failures;
        static GoreZone testZone;
        static readonly FieldInfo count = typeof(BloodsplatterManager).GetField("currentBloodCount", BindingFlags.Instance | BindingFlags.NonPublic);
        public static void Install() { enabled = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_BLOOD_PROBE") == "1"; }
        public static void Tick()
        {
            if (!enabled || Time.unscaledTime < next) return;
            try
            {
                var b = MonoSingleton<BloodsplatterManager>.Instance;
                if (!b || !b.props.IsCreated) return;
                if (stage == 0 || stage == 4)
                {
                    var pos = MonoSingleton<NewMovement>.Instance.transform.position;
                    // Include axis normals and oblique half-rounded normals.
                    for (int i = 0; i < 64; i++)
                        deleted = b.CreateBloodstain(pos + new Vector3(i * .03f, 0, 0), i % 2 == 0 ? Vector3.up : new Vector3(.3f, .7f, .4f).normalized, true);
                    Debug.Log("[BloodProbe] Created 64 stains in " + SceneHelper.CurrentScene);
                }
                else if (stage == 1 || stage == 5) Check(b, false);
                else if (stage == 2 || stage == 6)
                {
                    for (int i = deleted - 31; i <= deleted; i++) b.DeleteBloodstain(i);
                    Debug.Log("[BloodProbe] Deleted 32 stains, keeping slots in the mesh.");
                }
                else if (stage == 3 || stage == 7)
                {
                    Check(b, true);
                    if (stage == 3) SceneHelper.LoadScene("Level 0-2", true);
                    else testZone = new GameObject("Blood probe gore zone").AddComponent<GoreZone>();
                }
                else if (stage == 8)
                {
                    // Exercise a real level collider and the gameplay stain API.
                    var pos = MonoSingleton<NewMovement>.Instance.transform.position;
                    RaycastHit surface = default;
                    bool found = false;
                    foreach (var hit in Physics.RaycastAll(pos + Vector3.forward * 5 + Vector3.up * 3, Vector3.down, 20))
                        if (hit.collider.CompareTag("Floor") || hit.collider.CompareTag("Wall")) { surface = hit; found = true; break; }
                    if (!found) throw new Exception("No level surface found for blood collision test.");
                    var gore = b.GetGore(GoreType.Small);
                    gore.transform.SetParent(testZone.transform, true);
                    gore.transform.position = surface.point + Vector3.up;
                    gore.SetActive(true);
                    var blood = gore.GetComponent<Bloodsplatter>();
                    blood.gz = testZone; blood.GetReady();
                    for (int i = 0; i < 100; i++) blood.CreateBloodstain(in surface, b);
                    var main = blood.part.main;
                    main.simulationSpace = ParticleSystemSimulationSpace.World;
                    blood.part.Emit(new ParticleSystem.EmitParams { position = surface.point + Vector3.up, velocity = Vector3.down * 4, startLifetime = 3, startSize = .1f }, 1000);
                    Debug.Log("[BloodProbe] Created collision stains on " + surface.collider.name + " and emitted 1000 blood particles.");
                }
                else if (stage == 9) { Check(b, true); Object.Destroy(testZone.gameObject); }
                else if (stage == 10)
                {
                    Check(b, true);
                    var pos = MonoSingleton<NewMovement>.Instance.transform.position;
                    for (int i = 0; i < 11000; i++) deleted = b.CreateBloodstain(pos + new Vector3(i % 20 * .02f, 0, 0), Vector3.up, true);
                    Debug.Log("[BloodProbe] Created 11000 stains for 32-bit mesh indices.");
                }
                else if (stage == 11)
                {
                    Check(b, true); b.SaveBloodstains();
                    for (int i = deleted - 127; i <= deleted; i++) b.DeleteBloodstain(i);
                }
                else if (stage == 12) { Check(b, true); b.LoadBloodstains(); }
                else if (stage == 13) { Check(b, true); SceneHelper.LoadScene("Main Menu", true); }
                else if (stage == 14)
                {
                    Debug.Log("[BloodProbe] " + (failures == 0 ? "PASS" : "FAIL") + ": finite stain vertices, deleted quads and transitions; failures=" + failures);
                    enabled = false;
                }
                stage++; next = Time.unscaledTime + (stage == 4 || stage == 14 ? 12 : stage == 9 ? 20 : 3);
            }
            catch (Exception e) { Debug.LogError("[BloodProbe] " + e); enabled = false; }
        }
        static void Check(BloodsplatterManager b, bool expectDeleted)
        {
            if (b.usedComputeShadersAtStart)
            {
                int size = (int)count.GetValue(b), missing = 0, gpuTombstones = 0;
                var data = new BloodsplatterManager.InstanceProperties[b.props.Length];
                b.instanceBuffer.GetData(data);
                for (int i = 0; i < size; i++)
                {
                    var p = b.props[i]; var uploaded = data[i];
                    if (!p.pos.Equals(uploaded.pos) || !p.norm.Equals(uploaded.norm) || p.clipState != uploaded.clipState || p.parentIndex != uploaded.parentIndex) missing++;
                    if (p.norm.x == 0 && p.norm.y == 0 && p.norm.z == 0) gpuTombstones++;
                }
                var args = new uint[5]; b.argsBuffer.GetData(args);
                bool gpuOk = missing == 0 && args[1] == size && (!expectDeleted || gpuTombstones > 0);
                if (!gpuOk) failures++;
                Debug.Log("[BloodProbe] " + SceneHelper.CurrentScene + " GPU stains=" + size + " mismatches=" + missing + " tombstones=" + gpuTombstones + " draw_instances=" + args[1] + " ok=" + gpuOk);
                return;
            }
            var vertices = b.totalStainMesh.vertices;
            int nonfinite = 0, tombstones = 0, collapsed = 0, mismatches = 0;
            foreach (var v in vertices) if (!Finite(v.x) || !Finite(v.y) || !Finite(v.z)) nonfinite++;
            int n = (int)count.GetValue(b);
            for (int i = 0; i < n; i++)
            {
                var p = b.props[i];
                var matrix=BloodMesh.Transform(p.pos,p.norm,i);
                for (int j=0;j<4;j++) {
                    var expected=Unity.Mathematics.math.mul(matrix,new Unity.Mathematics.float4(j==0||j==3?-1:1,j<2?1:-1,0,1)).xyz;
                    if ((vertices[i*4+j]-(Vector3)expected).sqrMagnitude>1e-9f) mismatches++;
                }
                if (p.norm.x == 0 && p.norm.y == 0 && p.norm.z == 0)
                {
                    tombstones++;
                    int k = i * 4;
                    if (k + 3 < vertices.Length && vertices[k] == vertices[k+1] && vertices[k] == vertices[k+2] && vertices[k] == vertices[k+3]) collapsed++;
                }
            }
            bool ok = vertices.Length >= n * 4 && b.totalStainMesh.GetIndexCount(0) == n*6 && mismatches == 0 && nonfinite == 0 && (!expectDeleted || tombstones > 0 && collapsed == tombstones);
            if (!ok) failures++;
            Debug.Log("[BloodProbe] " + SceneHelper.CurrentScene + " vertices=" + vertices.Length + " mismatches=" + mismatches + " nonfinite=" + nonfinite + " tombstones=" + tombstones + " collapsed=" + collapsed + " bounds=" + b.totalStainMesh.bounds + " ok=" + ok);
        }
        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
