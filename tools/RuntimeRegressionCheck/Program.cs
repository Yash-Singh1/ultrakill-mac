using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
var baseline=AssemblyDefinition.ReadAssembly(args[0]);
var candidate=AssemblyDefinition.ReadAssembly(args[1]);
var helper=AssemblyDefinition.ReadAssembly(args[2]);
IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types){foreach(var t in types){yield return t;foreach(var n in All(t.NestedTypes))yield return n;}}
IEnumerable<MethodDefinition> Methods(AssemblyDefinition a)=>All(a.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody);
string Body(MethodDefinition m)=>string.Join("\n",m.Body.Instructions.Select(i=>{
 var opcode=i.OpCode.Name;
 if(i.OpCode.OperandType==OperandType.ShortInlineBrTarget)opcode=opcode[..^2];
 var operand=i.Operand is Instruction target?"instruction "+m.Body.Instructions.IndexOf(target):i.Operand is Instruction[] targets?string.Join(",",targets.Select(m.Body.Instructions.IndexOf)):i.Operand?.ToString()??"";
 return opcode+" "+operand;
}));
string Key(MethodDefinition m)=>m.FullName+"#"+m.MetadataToken.RID;
var old=Methods(baseline).ToDictionary(Key);
var changed=Methods(candidate).Where(m=>!old.ContainsKey(Key(m))||Body(old[Key(m)])!=Body(m)).Select(m=>m.FullName).ToArray();
var allowed=new[]{"Coin::CoinReflectCheck", "Coin::ExplosiveReflectCheck", "Explosion::Collide", "PortalRenderV2::Render"};
using var original=AssemblyDefinition.ReadAssembly(args[3]);
var shipped=Methods(original).ToArray();
foreach(var name in allowed.Take(3)){
 var m=Methods(baseline).Single(m=>m.FullName.Contains(name));
 if(Body(m)!=Body(shipped.Single(o=>o.FullName==m.FullName)))throw new Exception("Pre-existing game method differs from original: "+m.FullName);
}
if(changed.Length!=4 || changed.Any(n=>!allowed.Any(n.Contains)))throw new Exception("Unexpected changes against installed build: "+string.Join("; ",changed));
string PortalBody(MethodDefinition m){
 var instructions=m.Body.Instructions.Where(i=>!(i.Operand is MethodReference r && r.DeclaringType.Name=="FrameScopes" && r.Name is "BeginPortalWait" or "EndPortalWait")).ToList();
 return string.Join("\n",instructions.Select(i=>{
  var opcode=i.OpCode.Name;
  if(i.OpCode.OperandType==OperandType.ShortInlineBrTarget)opcode=opcode[..^2];
  var operand=i.Operand is Instruction target?"instruction "+instructions.IndexOf(target):i.Operand?.ToString()??"";
  return opcode+" "+operand;
 }));
}
var oldPortal=Methods(baseline).Single(m=>m.DeclaringType.Name=="PortalRenderV2"&&m.Name=="Render");
var newPortal=Methods(candidate).Single(m=>m.DeclaringType.Name=="PortalRenderV2"&&m.Name=="Render");
if(PortalBody(oldPortal)!=PortalBody(newPortal))throw new Exception("Portal behavior changed beyond timing calls");
var helperCalls=Methods(helper).SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand?.ToString()??"").ToArray();
if(!helperCalls.Any(s=>s.Contains("FpsDisplay::Install")))throw new Exception("FPS overlay removed");
var install=Methods(helper).Single(m=>m.DeclaringType.Name=="Probe"&&m.Name=="Install");
if(!install.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="SetStackTraceLogType"))throw new Exception("Routine stack trace fix absent");
var contactCalls=Methods(candidate).Single(m=>m.DeclaringType.Name=="Explosion"&&m.Name=="Collide").Body.Instructions.Where(i=>i.Operand is MethodReference r&&r.Name=="EnvironmentContact").Count();
if(contactCalls!=1)throw new Exception("Environment debris query scope changed");
foreach(var m in Methods(candidate))foreach(var i in m.Body.Instructions){
 if(i.Operand is Instruction target && !m.Body.Instructions.Contains(target))throw new Exception("Invalid branch in "+m.FullName);
}
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,compared_methods=old.Count,changed_methods=changed,pacing_and_shaders_unchanged=true,environment_debris_query_only=true,fps_overlay_preserved=true}));
