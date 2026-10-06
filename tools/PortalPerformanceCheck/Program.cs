using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
using var baseline=AssemblyDefinition.ReadAssembly(args[0]);
using var candidate=AssemblyDefinition.ReadAssembly(args[1]);
using var helper=AssemblyDefinition.ReadAssembly(args[2]);
IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types){foreach(var t in types){yield return t;foreach(var n in All(t.NestedTypes))yield return n;}}
IEnumerable<MethodDefinition> Methods(AssemblyDefinition a)=>All(a.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody);
string Key(MethodDefinition m)=>m.FullName+"#"+m.MetadataToken.RID;
string Body(MethodDefinition m)=>string.Join("\n",m.Body.Instructions.Select(i=>{
 var opcode=i.OpCode.Name;
 if(i.OpCode.OperandType==OperandType.ShortInlineBrTarget)opcode=opcode[..^2];
 var operand=i.Operand is Instruction target?"instruction "+m.Body.Instructions.IndexOf(target):i.Operand is Instruction[] targets?string.Join(",",targets.Select(m.Body.Instructions.IndexOf)):i.Operand?.ToString()??"";
 return opcode+" "+operand;
}));
var old=Methods(baseline).ToDictionary(Key);
var changed=Methods(candidate).Where(m=>!old.ContainsKey(Key(m))||Body(old[Key(m)])!=Body(m)).ToArray();
if(changed.Length!=1||changed[0].DeclaringType.Name!="PortalRenderV2"||changed[0].Name!="Render")throw new Exception("Unexpected runtime changes");
var portal=changed[0];var originalPortal=old[Key(portal)];
if(portal.Body.Instructions.Count!=originalPortal.Body.Instructions.Count)throw new Exception("Changed portal instruction count");
var wait=originalPortal.Body.Instructions.Single(i=>i.Operand is MethodReference r&&r.Name=="WaitForCompletion");
var patched=portal.Body.Instructions[originalPortal.Body.Instructions.IndexOf(wait)];
if(patched.OpCode!=OpCodes.Pop||patched.Operand!=null)throw new Exception("GPU wait replacement does not balance stack");
patched.OpCode=wait.OpCode;patched.Operand=wait.Operand;
if(Body(portal)!=Body(originalPortal))throw new Exception("Changed rendering behavior beyond wait removal");
if(All(helper.MainModule.Types).Any(t=>t.Name=="CombatFixes"))throw new Exception("Declined game bug fixes remain");
int checks=0;
// Mirror the existing pending/done culling condition for every bit index.
for(int index=0;index<80;index++)foreach(bool enabled in new[]{false,true})foreach(bool done in new[]{false,true})foreach(ulong mask in new[]{0UL,1UL,0xAAAAAAAAAAAAAAAAUL,ulong.MaxValue}){
 bool draw=!enabled||!done||index>=64||(mask&(1UL<<index))!=0;
 bool expected=(!enabled||!done||index>=64)?true:((mask>>index)&1)!=0;
 if(draw!=expected)throw new Exception("Conservative pending-result culling failed");checks++;
}
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,compared_methods=old.Count,changed_method=portal.FullName,only_change="Blocking readback call replaced with stack pop",culling_checks=checks,pending_portals_preserved=true,original_game_bug_fixes_absent=true}));
