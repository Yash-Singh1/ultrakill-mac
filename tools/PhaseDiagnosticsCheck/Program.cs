using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;
using ULTRAKILL.MacPort;
using System.Reflection;
using System.Text.Json;
var directory=Path.GetFullPath("mac/runtime-state/phase-check/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
Application.dataPath=Path.Combine(directory,"Fixture.app/Contents");
var order=new List<string>();
var original=new PlayerLoopSystem {type=typeof(Root),subSystemList=new[]{
 new PlayerLoopSystem{type=typeof(Update),updateDelegate=()=>order.Add("update"),subSystemList=new[]{new PlayerLoopSystem{type=typeof(Child),updateDelegate=()=>order.Add("child")}}},
 new PlayerLoopSystem{type=typeof(Render),updateDelegate=()=>{order.Add("render");Thread.Sleep(35);}}
}};
PlayerLoop.root=original;PhaseDiagnostics.Install(new GameObject());
var instance=(PhaseDiagnostics)typeof(PhaseDiagnostics).GetField("instance",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
void Invoke(string name)=>typeof(PhaseDiagnostics).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(instance,null);
Invoke("Start");
void Run(PlayerLoopSystem system){system.updateDelegate?.Invoke();if(system.subSystemList!=null)foreach(var child in system.subSystemList)Run(child);}
Run(PlayerLoop.root);
var duringFight=Directory.GetFiles(Path.Combine(directory,"diagnostics"),"phases-*.csv").Single();
if(File.ReadAllLines(duringFight).Length!=1)throw new Exception("Capture wrote rows during a frame");
SceneManager.Change();Run(PlayerLoop.root);Thread.Sleep(40);Run(PlayerLoop.root);Invoke("OnDestroy");
if(!order.SequenceEqual(Enumerable.Range(0,3).SelectMany(_=>new[]{"update","child","render"})))throw new Exception("Original loop callback order changed");
if(PlayerLoop.root.subSystemList.Length!=2 || PlayerLoop.root.subSystemList[0].subSystemList.Length!=1)throw new Exception("Original loop layout not restored");
var csv=Directory.GetFiles(Path.Combine(directory,"diagnostics"),"phases-*.csv").SelectMany(File.ReadLines).ToArray();
if(csv.Count(line=>line.StartsWith("utc,"))!=1)throw new Exception("Repeated schema for identical loop");
var rows=csv.Where(line=>!line.StartsWith("utc,")).ToArray();if(rows.Length!=3)throw new Exception("Scene change lost capture rows");
foreach(var line in rows){var fields=line.Split(',');if(double.Parse(fields[6],System.Globalization.CultureInfo.InvariantCulture)<25)throw new Exception("Slow render phase not attributed");}
if(double.Parse(rows[2].Split(',')[3],System.Globalization.CultureInfo.InvariantCulture)<25)throw new Exception("Outside-loop stall not captured");
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,original_callback_order_preserved=true,original_child_systems_preserved=true,repeated_scene_install_does_not_duplicate_probes=true,scene_change_preserves_captures=true,slow_render_phase_detected=true,outside_loop_delay_detected=true,no_capture_disk_writes_during_frames=true,scope="PlayerLoop API stubs with real 35 ms callback; no game window launched"}));
class Root{}class Update{}class Render{}class Child{}
