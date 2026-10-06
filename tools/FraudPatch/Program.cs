using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

if (args.Length != 2 || !Path.GetFullPath(args[1]).Contains("/mac/") || Path.GetFullPath(args[0]) == Path.GetFullPath(args[1]))
    throw new ArgumentException("Use separate source and output files under mac/.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) { foreach (var t in types) { yield return t; foreach (var n in Types(t.NestedTypes)) yield return n; } }
string Body(MethodDefinition m) => string.Join("\n", m.Body.Instructions.Select(i => i.OpCode.Name + " " + (i.Operand is Instruction target ? m.Body.Instructions.IndexOf(target).ToString() : i.Operand is Instruction[] targets ? string.Join(",", targets.Select(m.Body.Instructions.IndexOf)) : i.Operand?.ToString())));
var methods = Types(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
string Key(MethodDefinition m) => m.FullName + "#" + m.MetadataToken.RID;
var before = methods.ToDictionary(Key, Body);
var originals = new Dictionary<(string, int), MethodReference>();
var helper = new TypeReference("ULTRAKILL.MacPort", "JobTuning", module, module.AssemblyReferences.Single(r => r.Name == "PortProbe"));
var changed = new List<string>();
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
assembly.Write(args[1]);
using var check = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
var checkMethods = Types(check.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
if (checkMethods.Length != methods.Length) throw new Exception("Unexpected method count.");
foreach (var method in checkMethods)
{
    for (int i = 0; i < method.Body.Instructions.Count; i++)
        if (originals.TryGetValue((Key(method), i), out var original))
        {
            if (method.Body.Instructions[i].Operand is not MethodReference hook || hook.Name != "NativeBufferQuery") throw new Exception("Missing wrapper.");
            method.Body.Instructions[i].Operand = original;
        }
    if (Body(method) != before[Key(method)]) throw new Exception("Unexpected method change: " + method.FullName);
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, pointer_calls_wrapped = changed.Count, changed_methods = changed.Distinct(), original_behavior_retained_by_default = true }));
