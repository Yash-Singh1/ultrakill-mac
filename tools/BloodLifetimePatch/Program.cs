using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

if(args.Length!=2)throw new ArgumentException("Usage: BloodLifetimePatch SOURCE.dll OUTPUT.dll");
var source=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);
if(source==output || !output.Contains("/mac/"))throw new ArgumentException("Use a separate output under mac/.");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(source));
using var assembly=AssemblyDefinition.ReadAssembly(source,new ReaderParameters{AssemblyResolver=resolver});
var module=assembly.MainModule;
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> list){foreach(var t in list){yield return t;foreach(var child in Types(t.NestedTypes))yield return child;}}
string Body(MethodDefinition m)=>string.Join("\n",m.Body.Instructions.Select(i=>i.OpCode.Name+" "+(i.Operand is Instruction target?m.Body.Instructions.IndexOf(target).ToString():i.Operand is Instruction[] targets?string.Join(",",targets.Select(m.Body.Instructions.IndexOf)):i.Operand?.ToString())));
var methods=Types(module.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
string Key(MethodDefinition m)=>m.FullName+"#"+m.MetadataToken.RID;
var before=methods.ToDictionary(Key,Body);
var parent=module.Types.Single(t=>t.Name=="BloodstainParent");
var manager=module.Types.Single(t=>t.Name=="BloodsplatterManager");
var clear=parent.Methods.Single(m=>m.Name=="ClearChildren");
var bsm=parent.Fields.Single(f=>f.Name=="bsm");var children=parent.Fields.Single(f=>f.Name=="children");var props=manager.Fields.Single(f=>f.Name=="props");
if(clear.Body.Instructions.Any(i=>i.Operand is MethodReference r && r.Name=="get_IsCreated"))throw new Exception("Blood lifetime guard already present; inspect before repatching.");
var clearList=clear.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>().First(r=>r.Name=="Clear" && r.DeclaringType.FullName==children.FieldType.FullName);
var created=new MethodReference("get_IsCreated",module.TypeSystem.Boolean,props.FieldType){HasThis=true};
var originalFirst=clear.Body.Instructions[0];var il=clear.Body.GetILProcessor();
var cleanup=Instruction.Create(OpCodes.Ldarg_0);
var prefix=new[]{
 Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,bsm),Instruction.Create(OpCodes.Brfalse,cleanup),
 Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,bsm),Instruction.Create(OpCodes.Ldflda,props),Instruction.Create(OpCodes.Call,created),Instruction.Create(OpCodes.Brtrue,originalFirst),
 cleanup,Instruction.Create(OpCodes.Ldfld,children),Instruction.Create(OpCodes.Callvirt,clearList),Instruction.Create(OpCodes.Ret)
};
foreach(var instruction in prefix)il.InsertBefore(originalFirst,instruction);
assembly.Write(output);
using var check=AssemblyDefinition.ReadAssembly(output,new ReaderParameters{AssemblyResolver=resolver});
var updated=Types(check.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
var changed=updated.Where(m=>before[Key(m)]!=Body(m)).Select(m=>m.FullName).ToArray();
if(changed.Length!=1 || changed[0]!=clear.FullName)throw new Exception("Unexpected method changes: "+string.Join(",",changed));
var afterClear=updated.Single(m=>m.FullName==clear.FullName);
// The original live-buffer cleanup must remain instruction-for-instruction.
for(int i=prefix.Length;i<afterClear.Body.Instructions.Count;i++){
 var a=afterClear.Body.Instructions[i];var b=clear.Body.Instructions[i];
 if(a.OpCode!=b.OpCode || (a.Operand?.ToString()??"")!=(b.Operand?.ToString()??""))throw new Exception("Original cleanup body changed.");
}
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,changed_methods=changed,compared_methods=before.Count,guard="Clear local child indices without native writes when the manager's stain buffer is disposed",live_cleanup_unchanged=true}));
