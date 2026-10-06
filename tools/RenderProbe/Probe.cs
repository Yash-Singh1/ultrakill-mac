using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ULTRAKILL.MacPort
{
    public static class Probe
    {
        private static bool enabled;
        private static int stage;
        private static float next;
        private static string lastControl;
        private static bool playMode;
        private static float nextJump = 15;
        private static readonly Dictionary<int, bool> originalEnabled = new Dictionary<int, bool>();
        public static void Install()
        {
            enabled = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_RENDER_PROBE") == "1";
            playMode = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_RENDER_PLAY") == "1";
            if (enabled) Camera.onPreCull += ForceView;
        }
        private static void ForceView(Camera c)
        {
            if (c.name != "Main Camera") return;
            var control = File.ReadAllText(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_RENDER_CONTROL")).Trim();
            var fields = control.Split(',');
            var values = new float[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                if (!float.TryParse(fields[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i])) return;
            if (values.Length < 5) return;
            if (!playMode)
            {
                c.transform.rotation = Quaternion.Euler(values[0], 0, 0);
                c.transform.position = new Vector3(values[1], values[2], values[3]);
            }
            else if (values.Length > 5)
                c.transform.rotation = Quaternion.Euler(values[0], values[5], 0);
            if (lastControl == control) return;
            lastControl = control;
            Debug.Log("[RenderProbe] Control " + control);
            var mode = values.Length > 4 ? (int)values[4] : 0;
            Debug.Log("[RenderProbe] Fog " + RenderSettings.fogColor + " global=" + Shader.GetGlobalColor("unity_FogColor"));
            c.backgroundColor = mode == 4 ? Color.white : Color.black;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (!originalEnabled.ContainsKey(r.GetInstanceID())) originalEnabled[r.GetInstanceID()] = r.enabled;
                var wasEnabled = originalEnabled[r.GetInstanceID()];
                if (r.sharedMaterials.Any(m => m != null && m.shader.name == "Hidden/ULTRAKILL/ULTRAKILL-Stationary"))
                    r.enabled = wasEnabled && mode != 2;
                else if (!Path(r.transform).Contains("Virtual Camera") && !Path(r.transform).Contains("HUD") && !Path(r.transform).Contains("v1_combined"))
                    r.enabled = wasEnabled && mode != 5;
                if (Math.Abs(r.bounds.center.x) < 12 && Math.Abs(r.bounds.center.z - 253) < 12 && r.bounds.center.y > 100)
                    Debug.Log("[RenderProbe] Top renderer " + Path(r.transform) + " enabled=" + wasEnabled + " bounds=" + r.bounds + " layer=" + r.gameObject.layer + " mat=" + string.Join(";", r.sharedMaterials.Select(m => m == null ? "null" : m.name + " shader=" + m.shader.name)));
                if (!Path(r.transform).Contains("v1_combined")) continue;
                Debug.Log("[RenderProbe] Body renderer " + Path(r.transform) + " layer=" + r.gameObject.layer + " shadows=" + r.shadowCastingMode + " enabled=" + r.enabled);
                r.enabled = wasEnabled && mode != 1;
            }
            var ray = c.ViewportPointToRay(new Vector3(.5f,.5f,0));
            foreach (var r in Object.FindObjectsOfType<Renderer>().Where(r => r.enabled && r.bounds.IntersectRay(ray)).OrderBy(r => Vector3.Distance(c.transform.position, r.bounds.center)).Take(10))
                Debug.Log("[RenderProbe] View renderer " + Path(r.transform) + " materials=" + string.Join(";", r.sharedMaterials.Select(m => m.name + " shader=" + m.shader.name)));
        }
        private static string Path(Transform t)
        {
            return t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
        }
        public static void Tick()
        {
            if (!enabled || SceneHelper.CurrentScene != "Level 0-1") return;
            if (playMode)
            {
                var movement = MonoSingleton<NewMovement>.Instance;
                if (stage == 0 && Time.unscaledTime > 12)
                {
                    stage = 1;
                    foreach (var optimizer in Object.FindObjectsOfType<StaticSceneOptimizer>(true))
                    {
                        var buffer = typeof(StaticSceneOptimizer).GetField("cbGlobalLightsData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(optimizer);
                        Debug.Log("[RenderProbe] Optimizer " + Path(optimizer.transform) + " enabled=" + optimizer.enabled + " active=" + optimizer.gameObject.activeInHierarchy + " compute=" + optimizer.usedComputeShadersAtStart + " lights=" + optimizer.globalLights.Length + " buffer=" + buffer);
                    }
                    Debug.Log("[RenderProbe] Optimizer count " + Object.FindObjectsOfType<StaticSceneOptimizer>(true).Length);
                }
                if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_RENDER_JUMP") == "1" && Time.unscaledTime > nextJump)
                {
                    nextJump = Time.unscaledTime + 3;
                    movement.Jump();
                    Debug.Log("[RenderProbe] Invoked player Jump at " + movement.transform.position.ToString("F3"));
                }
                if (Time.unscaledTime < next) return;
                next = Time.unscaledTime + .5f;
                var camera = MonoSingleton<CameraController>.Instance.cam;
                Debug.Log("[RenderProbe] Live position=" + movement.transform.position.ToString("F3") + " velocity=" + movement.rb.velocity.ToString("F3") + " camera=" + camera.transform.eulerAngles.ToString("F3") + " timeScale=" + Time.timeScale + " activated=" + movement.activated);
                return;
            }
            var controller = MonoSingleton<CameraController>.Instance;
            controller.rotationX = -90;
            controller.rotationY = 0;
            if (Time.unscaledTime < next) return;
            if (stage == 0)
            {
                next = Time.unscaledTime + 0.15f;
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                Debug.Log("[RenderProbe] Scene " + SceneHelper.CurrentScene);
                foreach (var c in Object.FindObjectsOfType<Camera>())
                {
                    Debug.Log("[RenderProbe] Camera " + Path(c.transform) + " position=" + c.transform.position.ToString("F3") + " forward=" + c.transform.forward.ToString("F3") + " clear=" + c.clearFlags + " bg=" + c.backgroundColor + " far=" + c.farClipPlane + " target=" + c.targetTexture + " mask=" + c.cullingMask);
                    if (!c.enabled || c.fieldOfView < 10) continue;
                    var ray = c.ViewportPointToRay(new Vector3(.5f,.5f,0));
                    foreach (var r in Object.FindObjectsOfType<Renderer>().Where(r => r.enabled && r.bounds.IntersectRay(ray)).OrderBy(r => Vector3.Distance(c.transform.position, r.bounds.center)).Take(35))
                    {
                        Debug.Log("[RenderProbe] Center renderer " + Path(r.transform) + " bounds=" + r.bounds + " materials=" + string.Join(";", r.sharedMaterials.Select(m => m == null ? "null" : m.name + " shader=" + m.shader.name + " color=" + (m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "none") + " keywords=" + string.Join(",", m.shaderKeywords))));
                    }
                }
                Time.timeScale = 0;
                next = Time.unscaledTime + 600;
                stage = 2;
            }
            else if (stage == 2)
            {
                foreach (var c in Object.FindObjectsOfType<Camera>()) c.backgroundColor = Color.green;
                Debug.Log("[RenderProbe] All camera backgrounds changed to green.");
                stage = 3;
                next = Time.unscaledTime + 60;
            }
            else if (stage == 3)
            {
                Application.Quit();
                enabled = false;
            }
        }
    }
}
