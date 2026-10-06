using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2 || !Path.GetFullPath(args[1]).Contains("/mac/")) throw new ArgumentException("Use an output copy under mac/.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetFullPath("mac/ULTRAKILL.app/Contents/Resources/Data/Managed"));
using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
var probe = new TypeReference("ULTRAKILL.MacPort", "Probe", module, module.AssemblyReferences.Single(a => a.Name == "PortProbe"));
var begin = new MethodReference("Begin", module.TypeSystem.Void, probe) { HasThis = false };
begin.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
var end = new MethodReference("End", module.TypeSystem.Void, probe) { HasThis = false };
if (Environment.GetEnvironmentVariable("ULTRAKILL_MAC_VISUAL_INSTRUMENT") == "1")
{
    var callback = new MethodReference("OnBloodPrepared", module.TypeSystem.Void, probe) { HasThis = false };
    var method = module.Types.Single(t => t.Name == "PostProcessV2_Handler").Methods.Single(m => m.Name == "OnPreRenderCallback");
    callback.Parameters.Add(new ParameterDefinition(method.Parameters[0].ParameterType));
    var il = method.Body.GetILProcessor();
    foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
    {
        ret.OpCode = OpCodes.Ldarg_1;
        var call = Instruction.Create(OpCodes.Call, callback);
        il.InsertAfter(ret, call); il.InsertAfter(call, Instruction.Create(OpCodes.Ret));
    }
    foreach (var instruction in method.Body.Instructions)
        if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            instruction.OpCode = typeof(OpCodes).GetFields().Select(f => f.GetValue(null)).OfType<OpCode>().Single(o => o.Name == instruction.OpCode.Name[..^2]);
}
var opcodes = typeof(OpCodes).GetFields().Select(f => f.GetValue(null)).OfType<OpCode>().ToDictionary(o => o.Name);
foreach (var type in module.Types)
{
    foreach (var method in type.Methods.Where(m => m.HasBody))
    {
        string? label = type.Name == "BloodsplatterManager" && method.Name is "Update" or "LateUpdate" or "RebuildMesh" ? "MacBlood." + method.Name :
            type.Name == "PortalParticles" && method.Name == "ScheduleJobs" ? "MacParticles.ScheduleJobs" : null;
        if (label == null) continue;
        var il = method.Body.GetILProcessor(); var first = method.Body.Instructions[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldstr, label));
        il.InsertBefore(first, Instruction.Create(OpCodes.Call, begin));
        foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            il.InsertBefore(ret, Instruction.Create(OpCodes.Call, end));
        foreach (var instruction in method.Body.Instructions)
            if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                instruction.OpCode = opcodes[instruction.OpCode.Name[..^2]];
        Console.WriteLine(label);
    }
}
assembly.Write(args[1]);
