using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

if (args.Length < 2 || !Path.GetFullPath(args[1]).Contains("/mac/") || Path.GetFullPath(args[0]) == Path.GetFullPath(args[1]))
    throw new ArgumentException("Use separate source and output files under mac/.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) { foreach (var t in types) { yield return t; foreach (var n in Types(t.NestedTypes)) yield return n; } }
string Body(MethodDefinition m) => string.Join("\n", m.Body.Instructions.Select(i => (i.OpCode.OperandType == OperandType.ShortInlineBrTarget ? i.OpCode.Name[..^2] : i.OpCode.Name) + " " + (i.Operand is Instruction target ? m.Body.Instructions.IndexOf(target).ToString() : i.Operand is Instruction[] targets ? string.Join(",", targets.Select(m.Body.Instructions.IndexOf)) : i.Operand?.ToString())));
var methods = Types(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
string Key(MethodDefinition m) => m.FullName + "#" + m.MetadataToken.RID;
var before = methods.ToDictionary(Key, Body);
var originals = new Dictionary<(string, int), MethodReference>();
var helper = new TypeReference("ULTRAKILL.MacPort", "JobTuning", module, module.AssemblyReferences.Single(r => r.Name == "PortProbe"));
var changed = new List<string>();
int cacheBeginInstructionCount=0;
foreach (var method in module.Types.Single(t => t.FullName == "ULTRAKILL.Portal.PortalRenderV2").Methods.Where(m => m.HasBody))
{
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand is not MethodReference original || original.DeclaringType.FullName != "UnityEngine.RenderBuffer" || original.Name != "GetNativeRenderBufferPtr") continue;
        if (instruction.Next.OpCode != OpCodes.Pop) throw new Exception("Native pointer is used; cannot omit this query.");
        originals.Add((Key(method), method.Body.Instructions.IndexOf(instruction)), original);
        var wrapper = new MethodReference("NativeBufferQuery", original.ReturnType, helper) { HasThis = false };
        wrapper.Parameters.Add(new ParameterDefinition(new ByReferenceType(original.DeclaringType)));
        instruction.OpCode = OpCodes.Call;
        instruction.Operand = wrapper;
        changed.Add(method.FullName);
    }
}
if (changed.Count != 4 || changed.Distinct().Count() != 2) throw new Exception("Unexpected portal renderer layout.");
if(args.Contains("--portal-mesh-cache"))
{
    var setup=module.Types.Single(t=>t.FullName=="ULTRAKILL.Portal.PortalRenderV2").Methods.Single(m=>m.Name=="Setup");
    var allocate=setup.Body.Instructions.Single(i=>i.Operand is MethodReference r && r.DeclaringType.FullName=="UnityEngine.Mesh" && r.Name=="AllocateWritableMeshData");
    var start=allocate.Previous;
    if(start.OpCode!=OpCodes.Ldc_I4_1) throw new Exception("Unexpected mesh allocation arguments");
    var end=setup.Body.Instructions.Single(i=>i.Operand is MethodReference r && r.DeclaringType.FullName=="UnityEngine.Mesh" && r.Name=="ApplyAndDisposeWritableMeshData").Next;
    var cache=new TypeReference("ULTRAKILL.MacPort","PortalMeshCache",module,helper.Scope);
    var requires=new MethodReference("RequiresRebuild",module.TypeSystem.Boolean,cache){HasThis=false};
    requires.Parameters.Add(new ParameterDefinition(setup.DeclaringType));
    var il=setup.Body.GetILProcessor();
    il.InsertBefore(start,Instruction.Create(OpCodes.Ldarg_0));
    il.InsertBefore(start,Instruction.Create(OpCodes.Call,requires));
    il.InsertBefore(start,Instruction.Create(OpCodes.Brfalse,end));
    var opcodes=typeof(OpCodes).GetFields().Select(f=>f.GetValue(null)).OfType<OpCode>().ToDictionary(o=>o.Name);
    foreach(var instruction in setup.Body.Instructions)
        if(instruction.OpCode.OperandType==OperandType.ShortInlineBrTarget) instruction.OpCode=opcodes[instruction.OpCode.Name[..^2]];
}
if(args.Contains("--portal-sync-timing"))
{
    foreach(var method in module.Types.Single(t=>t.FullName=="ULTRAKILL.Portal.PortalRenderV2").Methods.Where(m=>m.HasBody))
    foreach(var instruction in method.Body.Instructions)
    {
        if(instruction.Operand is not MethodReference original) continue;
        string name = original.DeclaringType.FullName=="UnityEngine.Rendering.AsyncGPUReadbackRequest" && original.Name=="WaitForCompletion" ? "PortalWait" : original.DeclaringType.FullName=="UnityEngine.GL" && original.Name=="Flush" ? "PortalFlush" : null;
        if(name==null) continue;
        originals.Add((Key(method),method.Body.Instructions.IndexOf(instruction)),original);
        var wrapper = new MethodReference(name,module.TypeSystem.Void,helper) { HasThis=false };
        if(name=="PortalWait") wrapper.Parameters.Add(new ParameterDefinition(new ByReferenceType(original.DeclaringType)));
        instruction.OpCode=OpCodes.Call; instruction.Operand=wrapper;
    }
}
if(args.Contains("--portal-async-visibility"))
{
    var render=module.Types.Single(t=>t.FullName=="ULTRAKILL.Portal.PortalRenderV2").Methods.Single(m=>m.Name=="Render");
    var call=render.Body.Instructions.Single(i=>i.Operand is MethodReference r && r.Name=="UpdateOcclusionBurst");
    var original=(MethodReference)call.Operand;
    var request=render.DeclaringType.Fields.Single(f=>f.Name=="occlusionReadback");
    var wrapper=new MethodReference("PortalVisibility",module.TypeSystem.Void,helper){HasThis=false};
    foreach(var parameter in original.Parameters) wrapper.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
    wrapper.Parameters.Add(new ParameterDefinition(new ByReferenceType(request.FieldType)));
    originals.Add((Key(render),render.Body.Instructions.IndexOf(call)),original);
    var il=render.Body.GetILProcessor(); il.InsertBefore(call,Instruction.Create(OpCodes.Ldarg_0)); il.InsertBefore(call,Instruction.Create(OpCodes.Ldflda,request));
    call.Operand=wrapper;
}
if (args.Contains("--portal-viewport"))
{
    var render = module.Types.Single(t => t.FullName == "ULTRAKILL.Portal.PortalRenderV2").Methods.Single(m => m.Name == "Render");
    var data = render.Body.Variables.Single(v => v.VariableType.FullName == "ULTRAKILL.Portal.PortalRenderV2/RenderData");
    var call = render.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.Camera" && m.Name == "Render");
    var viewport = new TypeReference("ULTRAKILL.MacPort", "PortalViewport", module, helper.Scope);
    var wrapper = new MethodReference("Render", module.TypeSystem.Void, viewport) { HasThis = false };
    wrapper.Parameters.Add(new ParameterDefinition(module.ImportReference(((MethodReference)call.Operand).DeclaringType)));
    wrapper.Parameters.Add(new ParameterDefinition(new ByReferenceType(data.VariableType)));
    originals.Add((Key(render), render.Body.Instructions.IndexOf(call)), (MethodReference)call.Operand);
    render.Body.GetILProcessor().InsertBefore(call, Instruction.Create(OpCodes.Ldloca, data));
    call.OpCode = OpCodes.Call; call.Operand = wrapper;
    var opcodes = typeof(OpCodes).GetFields().Select(f=>f.GetValue(null)).OfType<OpCode>().ToDictionary(o=>o.Name);
    foreach(var instruction in render.Body.Instructions)
        if(instruction.OpCode.OperandType==OperandType.ShortInlineBrTarget) instruction.OpCode=opcodes[instruction.OpCode.Name[..^2]];
}
if (args.Contains("--full-resolution"))
{
    var benchmark = new TypeReference("ULTRAKILL.MacPort", "FraudBenchmark", module, helper.Scope);
    var setup = module.Types.Single(t => t.Name == "PostProcessV2_Handler").Methods.Single(m => m.Name == "SetupRTs");
    foreach (var instruction in setup.Body.Instructions)
    {
        if (instruction.Operand is not MethodReference original || original.DeclaringType.FullName != "UnityEngine.Screen" || original.Name is not ("get_width" or "get_height")) continue;
        originals.Add((Key(setup), setup.Body.Instructions.IndexOf(instruction)), original);
        instruction.Operand = new MethodReference(original.Name == "get_width" ? "BenchmarkWidth" : "BenchmarkHeight", module.TypeSystem.Int32, benchmark) { HasThis = false };
    }
    if (originals.Values.Count(m=>m.DeclaringType.FullName=="UnityEngine.Screen") != 2) throw new Exception("Expected two screen-size queries.");
}
if(args.Contains("--portal-visibility-cache"))
{
    if(!args.Contains("--portal-async-visibility") || !args.Contains("--portal-sync-timing")) throw new Exception("Visibility cache requires both visibility and wait wrappers.");
    var renderer=module.Types.Single(t=>t.FullName=="ULTRAKILL.Portal.PortalRenderV2");
    var render=renderer.Methods.Single(m=>m.Name=="Render");
    var prepass=renderer.Fields.Single(f=>f.Name=="prepassPortals");
    var mesh=renderer.Fields.Single(f=>f.Name=="occluderMesh");
    if(methods.Any(m=>m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stfld && i.Operand is FieldReference f && f.FullName==mesh.FullName))) throw new Exception("Occluder mesh can change at runtime; cache is not applicable.");
    var prepassType=((GenericInstanceType)prepass.FieldType).GenericArguments.Single();
    var depth=renderer.Methods.Single(m=>m.Name=="DepthPrepass");
    var ptr=depth.Body.Instructions.Select(i=>i.Operand).OfType<GenericInstanceMethod>().Single(m=>m.Name=="GetUnsafeReadOnlyPtr" && m.GenericArguments.Single().FullName==prepassType.FullName);
    var length=depth.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>().First(m=>m.Name=="get_Length" && m.DeclaringType.FullName==prepass.FieldType.FullName);
    var utility=module.GetTypeReferences().Single(t=>t.FullName=="Unity.Collections.LowLevel.Unsafe.UnsafeUtility").Resolve();
    var size=new GenericInstanceMethod(module.ImportReference(utility.Methods.Single(m=>m.Name=="SizeOf" && m.HasGenericParameters && m.Parameters.Count==0))); size.GenericArguments.Add(prepassType);
    var cache=new TypeReference("ULTRAKILL.MacPort","PortalVisibilityCache",module,helper.Scope);
    var begin=new MethodReference("Begin",module.TypeSystem.Void,cache){HasThis=false};
    foreach(var type in new[]{renderer,render.Parameters.Single().ParameterType,mesh.FieldType,module.TypeSystem.IntPtr,module.TypeSystem.Int32}) begin.Parameters.Add(new ParameterDefinition(type));
    var anchor=render.Body.Instructions.Single(i=>i.OpCode==OpCodes.Stfld && i.Operand is FieldReference f && f.Name=="mainCam").Next;
    var injected=new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,mesh),
        Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,prepass),Instruction.Create(OpCodes.Call,ptr),Instruction.Create(OpCodes.Conv_I),
        Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldflda,prepass),Instruction.Create(OpCodes.Call,length),Instruction.Create(OpCodes.Call,size),Instruction.Create(OpCodes.Mul),Instruction.Create(OpCodes.Call,begin)};
    cacheBeginInstructionCount=injected.Length;
    var il=render.Body.GetILProcessor(); foreach(var instruction in injected)il.InsertBefore(anchor,instruction);
    var opcodes=typeof(OpCodes).GetFields().Select(f=>f.GetValue(null)).OfType<OpCode>().ToDictionary(o=>o.Name);
    foreach(var instruction in render.Body.Instructions)if(instruction.OpCode.OperandType==OperandType.ShortInlineBrTarget)instruction.OpCode=opcodes[instruction.OpCode.Name[..^2]];
}
assembly.Write(args[1]);
using var check = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
var checkMethods = Types(check.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
if (checkMethods.Length != methods.Length) throw new Exception("Unexpected method count.");
foreach (var method in checkMethods)
{
    if(args.Contains("--portal-visibility-cache") && method.DeclaringType.FullName=="ULTRAKILL.Portal.PortalRenderV2" && method.Name=="Render")
    {
        var end=method.Body.Instructions.Single(i=>i.Operand is MethodReference r && r.DeclaringType.Name=="PortalVisibilityCache" && r.Name=="Begin");
        var block=new List<Instruction>(); var cursor=end;
        for(int n=0;n<cacheBeginInstructionCount;n++) { block.Add(cursor); cursor=cursor.Previous; }
        foreach(var instruction in block)method.Body.GetILProcessor().Remove(instruction);
    }
    if(args.Contains("--portal-async-visibility") && method.DeclaringType.FullName=="ULTRAKILL.Portal.PortalRenderV2" && method.Name=="Render")
    {
        var call=method.Body.Instructions.Single(i=>i.Operand is MethodReference r && r.Name=="PortalVisibility");
        var field=call.Previous; var owner=field.Previous;
        if(field.OpCode!=OpCodes.Ldflda || owner.OpCode!=OpCodes.Ldarg_0) throw new Exception("Missing readback argument");
        var il=method.Body.GetILProcessor(); il.Remove(owner); il.Remove(field);
        call.Operand=originals.Single(p=>p.Key.Item1==Key(method) && p.Value.Name=="UpdateOcclusionBurst").Value;
    }
    if(args.Contains("--portal-mesh-cache") && method.DeclaringType.FullName=="ULTRAKILL.Portal.PortalRenderV2" && method.Name=="Setup")
    {
        var call=method.Body.Instructions.Single(i=>i.Operand is MethodReference r && r.DeclaringType.Name=="PortalMeshCache");
        var argument=call.Previous; var branch=call.Next;
        if(argument.OpCode!=OpCodes.Ldarg_0 || branch.OpCode!=OpCodes.Brfalse) throw new Exception("Unexpected mesh cache guard");
        var il=method.Body.GetILProcessor(); il.Remove(argument); il.Remove(call); il.Remove(branch);
    }
    if(args.Contains("--portal-viewport") && method.DeclaringType.FullName == "ULTRAKILL.Portal.PortalRenderV2" && method.Name == "Render")
    {
        var call=method.Body.Instructions.Single(i=>i.Operand is MethodReference m && m.DeclaringType.Name=="PortalViewport");
        var injected=call.Previous;
        if(injected.OpCode != OpCodes.Ldloca) throw new Exception("Missing render-data argument");
        method.Body.GetILProcessor().Remove(injected);
        // Replace with the original instance call after removing its extra argument.
        call.OpCode=OpCodes.Callvirt;
        call.Operand=originals.Single(p=>p.Key.Item1==Key(method) && p.Value.DeclaringType.FullName=="UnityEngine.Camera").Value;
    }
    for (int i = 0; i < method.Body.Instructions.Count; i++)
        if (originals.TryGetValue((Key(method), i), out var original))
        {
            if(original.DeclaringType.FullName=="UnityEngine.Camera" || original.Name=="UpdateOcclusionBurst") continue;
            if (method.Body.Instructions[i].Operand is not MethodReference hook || hook.Name is not ("NativeBufferQuery" or "BenchmarkWidth" or "BenchmarkHeight" or "PortalWait" or "PortalFlush")) throw new Exception("Missing wrapper.");
            method.Body.Instructions[i].Operand = original;
        }
    if (Body(method) != before[Key(method)]) throw new Exception("Unexpected method change: " + method.FullName);
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, pointer_calls_wrapped = changed.Count, changed_methods = changed.Distinct(), original_behavior_retained_by_default = true }));
