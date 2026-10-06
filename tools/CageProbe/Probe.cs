using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using Object=UnityEngine.Object;
namespace ULTRAKILL.MacPort
{
    public static class Probe
    {
        static bool enabled,dumped;
        static Vector3 target;
        static float yaw,distance=5;
        static string control="";
        static Renderer selected;
        public static void Install() { enabled=Environment.GetEnvironmentVariable("ULTRAKILL_MAC_PERF_PROBE")=="1"; if(enabled) Camera.onPreCull+=View; }
        public static string PathOf(Transform t) => t.parent?PathOf(t.parent)+"/"+t.name:t.name;
        public static void Tick()
        {
            if(!enabled || Time.unscaledTime<15) return;
            if(!dumped) {
                dumped=true;
                var move=MonoSingleton<NewMovement>.Instance;move.enabled=false;move.activated=false;move.rb.isKinematic=true;
                var entries=new List<object>();
                foreach(var r in Object.FindObjectsOfType<Renderer>(true)) {
                    if(!r.sharedMaterials.Any(m=>m)) continue;
                    entries.Add(new Entry { path=PathOf(r.transform), active=r.gameObject.activeInHierarchy,enabled=r.enabled, center=r.bounds.center,size=r.bounds.size,materials=r.sharedMaterials.Where(m=>m).Select(m=>new Mat { name=m.name,shader=m.shader.name,keywords=string.Join(",",m.shaderKeywords),queue=m.renderQueue,cutoff=m.HasProperty("_Cutoff")?m.GetFloat("_Cutoff"):-1,texture=m.HasProperty("_MainTex")&&m.mainTexture?m.mainTexture.name:"" }).ToArray() });
                }
                var data=new Inventory { entries=entries.Cast<Entry>().ToArray() };File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_PERF_MARKERS")),"cage-renderers.json"),Newtonsoft.Json.JsonConvert.SerializeObject(new { entries=data.entries.Select(x=>new { x.path,x.active,x.enabled,center=new { x.center.x,x.center.y,x.center.z },size=new { x.size.x,x.size.y,x.size.z },x.materials }).ToArray() },Newtonsoft.Json.Formatting.Indented));
                foreach(var g in Object.FindObjectsOfType<GroundCheck>(true)) if(!g.capsule) Debug.Log("[CageProbe] Missing capsule "+PathOf(g.transform)+" active="+g.gameObject.activeInHierarchy+" enabled="+g.enabled);
                Debug.Log("[CageProbe] Dumped "+entries.Count+" renderers in "+SceneHelper.CurrentScene);
            }
            var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_PERF_MARKERS")),"cage-control.txt");
            if(!File.Exists(path))return;var text=File.ReadAllText(path).Trim();if(text==control)return;control=text;
            var fields=text.Split('|');selected=Object.FindObjectsOfType<Renderer>(true).FirstOrDefault(r=>PathOf(r.transform)==fields[0] && r.sharedMaterials.Any(m=>m&&m.HasProperty("_MainTex")&&m.mainTexture&&m.mainTexture.name.StartsWith("tile_fancypanelstransparent")));
            if(selected) { var root=selected.transform.root; foreach(var r in root.GetComponentsInChildren<Renderer>(true)) {for(var t=r.transform;t;t=t.parent)t.gameObject.SetActive(true);r.enabled=true;} Time.timeScale=1; foreach(var g in Object.FindObjectsOfType<GroundCheck>(true)) if(!g.capsule) Debug.Log("[CageProbe] Components "+PathOf(g.transform)+" "+string.Join(",",g.GetComponents<Component>().Select(c=>c.GetType().Name))); target=selected.bounds.center; yaw=float.Parse(fields[1],System.Globalization.CultureInfo.InvariantCulture);distance=float.Parse(fields[2],System.Globalization.CultureInfo.InvariantCulture);var move=MonoSingleton<NewMovement>.Instance;move.rb.position=target+Quaternion.Euler(0,yaw,0)*new Vector3(0,.2f,-distance);Debug.Log("[CageProbe] View "+control+" target="+target); foreach(var m in selected.sharedMaterials) Debug.Log("[CageProbe] Material "+m.name+" "+string.Join(";",new[]{"_Opacity","_SrcBlend","_DstBlend","_SrcBlend1","_DstBlend1","_ZWrite","_VertexColors"}.Where(m.HasProperty).Select(p=>p+"="+m.GetFloat(p)))); }
        }
        static void View(Camera c)
        {
            if(!enabled||!selected||c.name!="Main Camera")return;
            target=selected.bounds.center;c.transform.position=target+Quaternion.Euler(0,yaw,0)*new Vector3(0,.2f,-distance);
            c.transform.rotation=Quaternion.LookRotation(target-c.transform.position,Vector3.up);
        }
        [Serializable] public class Mat {public string name,shader,keywords,texture;public int queue;public float cutoff;}
        [Serializable] public class Entry {public string path;public bool active,enabled;public Vector3 center,size;public Mat[] materials;}
        [Serializable] public class Inventory {public Entry[] entries;}
    }
}
