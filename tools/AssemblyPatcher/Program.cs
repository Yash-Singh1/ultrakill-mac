using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) throw new ArgumentException("Usage: AssemblyPatcher SOURCE.dll OUTPUT.dll");
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (source == output || !output.Contains("/mac/")) throw new ArgumentException("Output must be a separate copy under mac/.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(source));
using var assembly = AssemblyDefinition.ReadAssembly(source, new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;

IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
{
    foreach (var type in types)
    {
        yield return type;
        foreach (var nested in Types(type.NestedTypes)) yield return nested;
    }
}

var methods = Types(module.Types).SelectMany(type => type.Methods).Where(method => method.HasBody).ToArray();
var calls = methods.SelectMany(method => method.Body.Instructions.Select(instruction => (method, instruction)))
    .Where(pair => pair.instruction.Operand is MethodReference reference &&
                   reference.DeclaringType.FullName == "UnityEngine.Application" && reference.Name == "get_dataPath")
    .ToArray();
var originalGetter = (MethodReference)calls.First().instruction.Operand;
var paths = new TypeDefinition("ULTRAKILL.MacPort", "Paths",
    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
module.Types.Add(paths);
var getter = new MethodDefinition("GetDataPath", MethodAttributes.Public | MethodAttributes.Static,
    module.TypeSystem.String);
paths.Methods.Add(getter);

MethodReference StringMethod(string nameSpace, string type, string name, int parameters)
{
    var reference = new MethodReference(name, module.TypeSystem.String,
        new TypeReference(nameSpace, type, module, module.TypeSystem.CoreLibrary)) { HasThis = false };
    for (var i = 0; i < parameters; i++) reference.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
    return reference;
}

var il = getter.Body.GetILProcessor();
var fallback = Instruction.Create(OpCodes.Pop);
il.Emit(OpCodes.Ldstr, "ULTRAKILL_MAC_DATA_PATH");
il.Emit(OpCodes.Call, StringMethod("System", "Environment", "GetEnvironmentVariable", 1));
il.Emit(OpCodes.Dup);
il.Emit(OpCodes.Brfalse, fallback);
il.Emit(OpCodes.Ret);
il.Append(fallback);
il.Emit(OpCodes.Call, originalGetter);
il.Emit(OpCodes.Call, StringMethod("System.IO", "Path", "GetDirectoryName", 1));
il.Emit(OpCodes.Call, StringMethod("System.IO", "Path", "GetDirectoryName", 1));
il.Emit(OpCodes.Ldstr, "user-data/ULTRAKILL_Data");
il.Emit(OpCodes.Call, StringMethod("System.IO", "Path", "Combine", 2));
il.Emit(OpCodes.Ret);
foreach (var (method, instruction) in calls)
{
    instruction.Operand = getter;
    Console.WriteLine($"Isolated data path: {method.FullName}");
}
// EngineInterop describes Windows C++ object layouts, not the Mac player ABI.
// particleCount already calls SyncJobs(false), so its native fence poke is redundant.
var portalParticles = module.Types.Single(type => type.FullName == "ULTRAKILL.Portal.PortalParticles");
var schedule = portalParticles.Methods.Single(method => method.Name == "ScheduleJobs");
var fence = schedule.Body.Instructions.Single(instruction => instruction.Operand is FieldReference field &&
    field.DeclaringType.FullName == "Interop.ParticleSystem" && field.Name == "m_UpdateFence");
var fencePointer = fence.Previous.Previous;
if (fencePointer.Operand is not MethodReference fp || fp.Name != "GetCachedPtr") throw new InvalidOperationException("Unexpected particle fence IL.");
fencePointer.OpCode = OpCodes.Pop;
fencePointer.Operand = null;
foreach (var instruction in new[] { fencePointer.Next, fence, fence.Next })
{
    instruction.OpCode = OpCodes.Nop;
    instruction.Operand = null;
}
// Keep particle collision/portal jobs, but omit the native trail-memory rewrite.
var trailStore = schedule.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld &&
    instruction.Operand is FieldReference field && field.DeclaringType.FullName == "ULTRAKILL.Portal.ParticleResponseJob" && field.Name == "trails");
var trailStart = trailStore;
while (trailStart.OpCode != OpCodes.Ldloca_S && trailStart.OpCode != OpCodes.Ldloca) trailStart = trailStart.Previous;
for (var instruction = trailStart; instruction != trailStore.Next; instruction = instruction.Next)
{
    instruction.OpCode = OpCodes.Nop;
    instruction.Operand = null;
}
var response = module.Types.Single(type => type.FullName == "ULTRAKILL.Portal.ParticleResponseJob").Methods.Single(method => method.Name == "Execute");
var trailRead = response.Body.Instructions.First(instruction => instruction.Operand is FieldReference field &&
    field.DeclaringType.FullName == "ULTRAKILL.Portal.ParticleResponseJob" && field.Name == "trails");
var trailBlockStart = trailRead.Previous;
// The native trail loop ends at its Span.Length comparison and back edge.
var spanLength = response.Body.Instructions.Skip(response.Body.Instructions.IndexOf(trailRead)).First(instruction =>
    instruction.Operand is MethodReference reference && reference.Name == "get_Length" && reference.DeclaringType.FullName.StartsWith("System.Span`1"));
var afterTrailBlock = spanLength.Next.Next;
trailBlockStart.OpCode = OpCodes.Br;
trailBlockStart.Operand = afterTrailBlock;
// Mono validates unreachable instructions too. Remove the old pointer loads
// and loop body rather than leaving an invalid stack in that dead block.
for (var instruction = trailBlockStart.Next; instruction != afterTrailBlock; instruction = instruction.Next)
{
    instruction.OpCode = OpCodes.Nop;
    instruction.Operand = null;
}
// Component reordering also writes through a Windows GameObject layout. Keep
// the current component order and let the audio manager exit its reorder loop.
foreach (var method in module.Types.Single(type => type.Name == "ComponentExtensions").Methods.Where(method =>
    method.Name is "MoveComponentUp" or "MoveComponentDown"))
{
    method.Body = new MethodBody(method);
    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
}
Console.WriteLine("Replaced Windows particle fence access; omitted native portal trail rewriting and component reordering.");
// A particle-count query synchronizes the native system. Reuse one count
// throughout this scheduling pass, and do not create jobs for empty systems.
var countsField = new FieldDefinition("macParticleCounts", FieldAttributes.Private, new ArrayType(module.TypeSystem.Int32));
portalParticles.Fields.Add(countsField);
VariableDefinition Local(MethodDefinition method, Instruction instruction) => instruction.Operand is VariableDefinition variable ? variable :
    method.Body.Variables[instruction.OpCode.Code switch {
        Code.Ldloc_0 or Code.Stloc_0 => 0, Code.Ldloc_1 or Code.Stloc_1 => 1,
        Code.Ldloc_2 or Code.Stloc_2 => 2, Code.Ldloc_3 or Code.Stloc_3 => 3,
        _ => throw new InvalidOperationException("Unexpected particle local.") }];
var particleReads = schedule.Body.Instructions.Where(instruction => instruction.Operand is MethodReference reference && reference.Name == "get_particleCount").ToArray();
if (particleReads.Length != 4) throw new InvalidOperationException("Expected four particle count queries.");
var scheduleIl = schedule.Body.GetILProcessor();
var systemsCountCall = schedule.Body.Instructions.First(instruction => instruction.Operand is MethodReference reference && reference.Name == "get_Count");
var systemCount = Local(schedule, systemsCountCall.Next);
var firstLoop = systemsCountCall.Next.Next;
var countsReady = Instruction.Create(OpCodes.Nop);
var allocateCounts = Instruction.Create(OpCodes.Ldarg_0);
foreach (var instruction in new[] {
    Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, countsField), Instruction.Create(OpCodes.Brfalse, allocateCounts),
    Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, countsField), Instruction.Create(OpCodes.Ldlen), Instruction.Create(OpCodes.Conv_I4),
    Instruction.Create(OpCodes.Ldloc, systemCount), Instruction.Create(OpCodes.Bge, countsReady), allocateCounts,
    Instruction.Create(OpCodes.Ldloc, systemCount), Instruction.Create(OpCodes.Ldc_I4, 32), Instruction.Create(OpCodes.Add),
    Instruction.Create(OpCodes.Newarr, module.TypeSystem.Int32), Instruction.Create(OpCodes.Stfld, countsField), countsReady
}) scheduleIl.InsertBefore(firstLoop, instruction);
// The first query was originally repeated after the now-removed Windows
// fence access. Keep the second one and cache its result.
for (var instruction = particleReads[0].Previous.Previous; instruction != particleReads[0].Next.Next; instruction = instruction.Next)
{ instruction.OpCode = OpCodes.Nop; instruction.Operand = null; }
Instruction PreviousSystemLookup(Instruction before)
{
    var instruction = before;
    while (instruction.Operand is not MethodReference reference || reference.Name != "get_Item" || !reference.DeclaringType.FullName.Contains("PortalAwareParticleSystem"))
        instruction = instruction.Previous ?? throw new InvalidOperationException("Particle system lookup not found.");
    return instruction;
}
var firstLookup = PreviousSystemLookup(particleReads[1]);
var firstIndex = Local(schedule, firstLookup.Previous);
var firstCount = Local(schedule, particleReads[1].Next);
var afterRead = particleReads[1].Next.Next;
foreach (var instruction in new[] {
    Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, countsField), Instruction.Create(OpCodes.Ldloc, firstIndex),
    Instruction.Create(OpCodes.Ldloc, firstCount), Instruction.Create(OpCodes.Stelem_I4)
}) scheduleIl.InsertBefore(afterRead, instruction);
foreach (var read in particleReads.Skip(2))
{
    var lookup = PreviousSystemLookup(read);
    var loopIndex = Local(schedule, lookup.Previous);
    var loopIncrement = schedule.Body.Instructions.Skip(schedule.Body.Instructions.IndexOf(read)).First(instruction =>
        instruction.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 &&
        Local(schedule, instruction) == loopIndex && instruction.Previous.OpCode == OpCodes.Add).Previous.Previous.Previous;
    var bodyStart = lookup.Next.Next;
    foreach (var instruction in new[] {
        Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, countsField), Instruction.Create(OpCodes.Ldloc, loopIndex),
        Instruction.Create(OpCodes.Ldelem_I4), Instruction.Create(OpCodes.Brfalse, loopIncrement)
    }) scheduleIl.InsertBefore(bodyStart, instruction);
    read.Previous.Previous.OpCode = OpCodes.Nop; read.Previous.Previous.Operand = null;
    read.Previous.OpCode = OpCodes.Nop; read.Previous.Operand = null;
    read.OpCode = OpCodes.Nop; read.Operand = null;
    foreach (var instruction in new[] {
        Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, countsField), Instruction.Create(OpCodes.Ldloc, loopIndex), Instruction.Create(OpCodes.Ldelem_I4)
    }) scheduleIl.InsertBefore(read, instruction);
}
Console.WriteLine("Cached particle counts and omitted zero-particle command/response jobs.");
// Static lighting uses CPU-written ComputeBuffers, with no compute dispatch.
// The shipped stationary shader has no NO_COMPUTE variant, so skipping these
// buffers leaves its vertex shader reading unbound GPU memory on Metal.
var staticStart = module.Types.Single(type => type.Name == "StaticSceneOptimizer").Methods.Single(method => method.Name == "Start");
var staticGraphicsSetting = staticStart.Body.Instructions.Single(instruction =>
    instruction.OpCode == OpCodes.Ldsfld && instruction.Operand is FieldReference reference && reference.Name == "disabledComputeShaders");
staticGraphicsSetting.OpCode = OpCodes.Ldc_I4_0;
staticGraphicsSetting.Operand = null;
Console.WriteLine("Enabled CPU-populated static lighting buffers independently of compute shader settings.");
// Keep the verified CPU decal shader, but cache geometry and upload only
// changed stain slots. The original fallback rebuilt all accumulated stains.
var bloodManager = module.Types.Single(type => type.Name == "BloodsplatterManager");
var dirtyBegin = new FieldDefinition("macDirtyBegin", FieldAttributes.Private, module.TypeSystem.Int32);
var dirtyEnd = new FieldDefinition("macDirtyEnd", FieldAttributes.Private, module.TypeSystem.Int32);
bloodManager.Fields.Add(dirtyBegin); bloodManager.Fields.Add(dirtyEnd);
var markBlood = new MethodDefinition("MacMarkStainDirty", MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void) { HasThis = true };
markBlood.Parameters.Add(new ParameterDefinition("index", ParameterAttributes.None, module.TypeSystem.Int32));
bloodManager.Methods.Add(markBlood);
var markIl = markBlood.Body.GetILProcessor();
var afterBegin = Instruction.Create(OpCodes.Nop);
var markReturn = Instruction.Create(OpCodes.Ret);
markIl.Emit(OpCodes.Ldarg_1); markIl.Emit(OpCodes.Ldarg_0); markIl.Emit(OpCodes.Ldfld, dirtyBegin); markIl.Emit(OpCodes.Bge, afterBegin);
markIl.Emit(OpCodes.Ldarg_0); markIl.Emit(OpCodes.Ldarg_1); markIl.Emit(OpCodes.Stfld, dirtyBegin);
markIl.Append(afterBegin);
markIl.Emit(OpCodes.Ldarg_1); markIl.Emit(OpCodes.Ldc_I4_1); markIl.Emit(OpCodes.Add); markIl.Emit(OpCodes.Ldarg_0); markIl.Emit(OpCodes.Ldfld, dirtyEnd); markIl.Emit(OpCodes.Ble, markReturn);
markIl.Emit(OpCodes.Ldarg_0); markIl.Emit(OpCodes.Ldarg_1); markIl.Emit(OpCodes.Ldc_I4_1); markIl.Emit(OpCodes.Add); markIl.Emit(OpCodes.Stfld, dirtyEnd); markIl.Append(markReturn);
foreach (var mutation in bloodManager.Methods.Where(method => method.Name == "CreateBloodstain" && method.IsPublic || method.Name == "DeleteBloodstain"))
{
    var ret = mutation.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
    var index = mutation.Name == "CreateBloodstain" ? ret.Previous : null;
    var il2 = mutation.Body.GetILProcessor();
    ret.OpCode = OpCodes.Ldarg_0;
    var load = mutation.Name == "CreateBloodstain" ? Instruction.Create(OpCodes.Nop) : Instruction.Create(OpCodes.Ldarg_1);
    if (index != null) { load.OpCode=index.OpCode; load.Operand=index.Operand; }
    il2.InsertAfter(ret,load);
    var call=Instruction.Create(OpCodes.Call,markBlood); il2.InsertAfter(load,call); il2.InsertAfter(call,Instruction.Create(OpCodes.Ret));
}
var bloodStart = bloodManager.Methods.Single(method => method.Name == "Start");
var firstBloodStart = bloodStart.Body.Instructions[0];
var bloodStartIl = bloodStart.Body.GetILProcessor();
bloodStartIl.InsertBefore(firstBloodStart, Instruction.Create(OpCodes.Ldarg_0));
bloodStartIl.InsertBefore(firstBloodStart, Instruction.Create(OpCodes.Ldc_I4, int.MaxValue));
bloodStartIl.InsertBefore(firstBloodStart, Instruction.Create(OpCodes.Stfld, dirtyBegin));
var loadBlood = bloodManager.Methods.Single(method => method.Name == "LoadBloodstains");
var loadIl=loadBlood.Body.GetILProcessor(); var loadFirst=loadBlood.Body.Instructions[0];
foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldc_I4_0),Instruction.Create(OpCodes.Stfld,dirtyBegin),Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldc_I4,int.MaxValue),Instruction.Create(OpCodes.Stfld,dirtyEnd) }) loadIl.InsertBefore(loadFirst,instruction);
var rendererAssembly=new AssemblyNameReference("MacBloodRenderer",new Version(1,0,0,0)); module.AssemblyReferences.Add(rendererAssembly);
var rendererType=new TypeReference("ULTRAKILL.MacPort","BloodRenderer",module,rendererAssembly);
var rendererUpdate=new MethodReference("Update",module.TypeSystem.Void,rendererType) { HasThis=false };
rendererUpdate.Parameters.Add(new ParameterDefinition(bloodManager));
for (int i=0;i<3;i++) rendererUpdate.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
var rebuild=bloodManager.Methods.Single(method=>method.Name=="RebuildMesh");
rebuild.Body=new MethodBody(rebuild); var rebuildIl=rebuild.Body.GetILProcessor();
rebuildIl.Emit(OpCodes.Ldarg_0);
foreach (var field in new[] { bloodManager.Fields.Single(f=>f.Name=="currentBloodCount"),dirtyBegin,dirtyEnd }) { rebuildIl.Emit(OpCodes.Ldarg_0);rebuildIl.Emit(OpCodes.Ldfld,field); }
rebuildIl.Emit(OpCodes.Call,rendererUpdate);
rebuildIl.Emit(OpCodes.Ldarg_0); rebuildIl.Emit(OpCodes.Ldc_I4,int.MaxValue); rebuildIl.Emit(OpCodes.Stfld,dirtyBegin);
rebuildIl.Emit(OpCodes.Ldarg_0); rebuildIl.Emit(OpCodes.Ldc_I4_0); rebuildIl.Emit(OpCodes.Stfld,dirtyEnd); rebuildIl.Emit(OpCodes.Ret);
var rendererRelease=new MethodReference("Release",module.TypeSystem.Void,rendererType) { HasThis=false };rendererRelease.Parameters.Add(new ParameterDefinition(bloodManager));
var bloodDestroy=bloodManager.Methods.Single(m=>m.Name=="OnDestroy");var destroyIl=bloodDestroy.Body.GetILProcessor();var destroyFirst=bloodDestroy.Body.Instructions[0];destroyIl.InsertBefore(destroyFirst,Instruction.Create(OpCodes.Ldarg_0));destroyIl.InsertBefore(destroyFirst,Instruction.Create(OpCodes.Call,rendererRelease));
Console.WriteLine("Cached CPU blood geometry and uploaded changed slots only.");
// Compile the small math helper first. Copy its IL into this assembly so the
// deployed player needs no additional DLL or cross-assembly job dependency.
var mathHelperPath = Path.Combine(AppContext.BaseDirectory, "../../../../BloodMeshFix/bin/Release/netstandard2.1/BloodMeshFix.dll");
using var mathHelper = AssemblyDefinition.ReadAssembly(Path.GetFullPath(mathHelperPath), new ReaderParameters { AssemblyResolver = resolver });
var helperSource = mathHelper.MainModule.Types.Single(type => type.Name == "BloodMesh").Methods.Single(method => method.Name == "Transform");
var helperType = new TypeDefinition("ULTRAKILL.MacPort", "BloodMesh", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
module.Types.Add(helperType);
var helperMethod = new MethodDefinition("Transform", MethodAttributes.Public | MethodAttributes.Static, module.ImportReference(helperSource.ReturnType));
helperType.Methods.Add(helperMethod);
foreach (var parameter in helperSource.Parameters)
    helperMethod.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, module.ImportReference(parameter.ParameterType)));
helperMethod.Body.InitLocals = helperSource.Body.InitLocals;
helperMethod.Body.MaxStackSize = helperSource.Body.MaxStackSize;
foreach (var variable in helperSource.Body.Variables)
    helperMethod.Body.Variables.Add(new VariableDefinition(module.ImportReference(variable.VariableType)));
var helperInstructions = helperSource.Body.Instructions.ToDictionary(instruction => instruction, instruction => Instruction.Create(OpCodes.Nop));
foreach (var instruction in helperSource.Body.Instructions)
{
    var copy = helperInstructions[instruction];
    copy.OpCode = instruction.OpCode;
    copy.Operand = instruction.Operand switch
    {
        Instruction target => helperInstructions[target],
        Instruction[] targets => targets.Select(target => helperInstructions[target]).ToArray(),
        VariableDefinition variable => helperMethod.Body.Variables[variable.Index],
        ParameterDefinition parameter => helperMethod.Parameters[parameter.Index],
        MethodReference reference => module.ImportReference(reference),
        FieldReference reference => module.ImportReference(reference),
        TypeReference reference => module.ImportReference(reference),
        _ => instruction.Operand
    };
    helperMethod.Body.Instructions.Add(copy);
}
var bloodExecute = module.Types.Single(type => type.Name == "GenerateBloodMeshJob").Methods.Single(method => method.Name == "Execute");
var basisCall = bloodExecute.Body.Instructions.Single(instruction => instruction.Operand is MethodReference reference && reference.Name == "orthonormal_basis");
var transformCall = bloodExecute.Body.Instructions.Single(instruction => instruction.Operand is MethodReference reference && reference.Name == "TRS");
var transformStart = basisCall.Previous.Previous.Previous;
var matrixStore = transformCall.Next;
if (matrixStore.OpCode != OpCodes.Dup) throw new InvalidOperationException("Unexpected blood transform consumer.");
var bloodNormLoad = transformStart.Previous;
// The two local assignments immediately preceding the basis call are pos and
// norm; verify their IL rather than depending on generated local indices.
if (bloodNormLoad.OpCode.Code is not (Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3))
    throw new InvalidOperationException("Unexpected blood normal assignment.");
VariableDefinition StoredLocal(Instruction instruction) => instruction.Operand is VariableDefinition variable ? variable :
    bloodExecute.Body.Variables[instruction.OpCode.Code switch { Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3, _ => throw new InvalidOperationException("Unexpected local store.") }];
var normLocal = StoredLocal(bloodNormLoad);
var posStore = bloodNormLoad.Previous;
while (posStore.OpCode.Code is not (Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3)) posStore = posStore.Previous;
var posLocal = StoredLocal(posStore);
for (var instruction = transformStart; instruction != matrixStore; instruction = instruction.Next)
{
    instruction.OpCode = OpCodes.Nop;
    instruction.Operand = null;
}
var bloodIl = bloodExecute.Body.GetILProcessor();
bloodIl.InsertBefore(matrixStore, Instruction.Create(OpCodes.Ldloc, posLocal));
bloodIl.InsertBefore(matrixStore, Instruction.Create(OpCodes.Ldloc, normLocal));
bloodIl.InsertBefore(matrixStore, Instruction.Create(OpCodes.Ldarg_1));
bloodIl.InsertBefore(matrixStore, Instruction.Create(OpCodes.Call, helperMethod));
Console.WriteLine("Collapsed deleted blood stain quads before orientation math; normalized valid stain normals.");
// Some shipped enemy ground checks omit capsule. Its cached instance ID is
// unused, so leave it at zero when absent and preserve all ground-check logic.
var groundStart = module.Types.Single(type => type.Name == "GroundCheck").Methods.Single(method => method.Name == "Start");
var capsuleField = groundStart.DeclaringType.Fields.Single(field => field.Name == "capsule");
var idCall = groundStart.Body.Instructions.Single(instruction => instruction.Operand is MethodReference reference && reference.Name == "GetInstanceID");
var assignmentStart = idCall.Previous.Previous.Previous;
if (assignmentStart.OpCode != OpCodes.Ldarg_0 || idCall.Previous.OpCode != OpCodes.Ldfld || idCall.Next.OpCode != OpCodes.Stfld)
    throw new InvalidOperationException("Unexpected GroundCheck capsule ID assignment.");
var groundIl = groundStart.Body.GetILProcessor();
groundIl.InsertBefore(assignmentStart, Instruction.Create(OpCodes.Ldarg_0));
groundIl.InsertBefore(assignmentStart, Instruction.Create(OpCodes.Ldfld, capsuleField));
groundIl.InsertBefore(assignmentStart, Instruction.Create(OpCodes.Brfalse, idCall.Next.Next));
Console.WriteLine("Guarded optional GroundCheck capsule instance ID.");
// Optional scene selection for port verification. Normal launches keep the game's flow.
var bootstrap = module.Types.Single(type => type.Name == "Bootstrap").Methods.Single(method => method.Name == "Start");
var loadScene = module.Types.Single(type => type.Name == "SceneHelper").Methods.Single(method =>
    method.Name == "LoadScene" && method.Parameters.Count == 2 && method.Parameters[0].ParameterType.FullName == "System.String");
var start = bootstrap.Body.Instructions[0];
var bootIl = bootstrap.Body.GetILProcessor();
var probeAssembly = new AssemblyNameReference("PortProbe", new Version(1, 0, 0, 0));
module.AssemblyReferences.Add(probeAssembly);
var installProbe = new MethodReference("Install", module.TypeSystem.Void,
    new TypeReference("ULTRAKILL.MacPort", "Probe", module, probeAssembly)) { HasThis = false };
bootIl.InsertBefore(start, Instruction.Create(OpCodes.Call, installProbe));
// Reuse the cached wall contact for concave colliders. Unity's ClosestPoint rejects them.
var slide = module.Types.Single(type => type.Name == "NewMovement").Methods.Single(method => method.Name == "CreateSlideScrape");
var slideQuery = slide.Body.Instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference mr && mr.DeclaringType.FullName == "UnityEngine.Collider" && mr.Name == "ClosestPoint");
if (slideQuery != null)
{
var colliderRead = slide.Body.Instructions.Take(slide.Body.Instructions.IndexOf(slideQuery)).Last(instruction => instruction.Operand is FieldReference fr && fr.DeclaringType.Name == "WallCheck" && fr.Name == "currentCollider");
if (colliderRead.Previous.Operand is not FieldReference wallField || wallField.Name != "wc") throw new InvalidOperationException("Unexpected slide wall contact IL.");
colliderRead.OpCode = OpCodes.Nop; colliderRead.Operand = null;
var slideHelper = new MethodReference("Find", slideQuery.Operand is MethodReference originalQuery ? originalQuery.ReturnType : throw new InvalidOperationException(), new TypeReference("ULTRAKILL.MacPort", "SlideContact", module, probeAssembly)) { HasThis = false };
slideHelper.Parameters.Add(new ParameterDefinition(wallField.FieldType));
slideHelper.Parameters.Add(new ParameterDefinition(((MethodReference)slideQuery.Operand).Parameters[0].ParameterType));
slideQuery.OpCode = OpCodes.Call; slideQuery.Operand = slideHelper;
Console.WriteLine("Reused cached concave wall contact for slide effects.");
}
else Console.WriteLine("Source slide effects already use the wall contact; no ClosestPoint patch needed.");
var graphics = Types(module.Types).Single(type => type.FullName == "SettingsMenu.Components.Pages.GraphicsSettings");
foreach (var methodName in new[] { "SetFrameRateLimit", "SetVSync" })
{
    var method = graphics.Methods.Single(m => m.Name == methodName);
    var hook = new MethodReference(methodName == "SetFrameRateLimit" ? "FrameLimitChanged" : "VSyncChanged", module.TypeSystem.Void, new TypeReference("ULTRAKILL.MacPort", "FramePacing", module, probeAssembly)) { HasThis = false };
    foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
    {
        ret.OpCode = OpCodes.Call; ret.Operand = hook;
        method.Body.GetILProcessor().InsertAfter(ret, Instruction.Create(OpCodes.Ret));
    }
}

var tickProbe = new MethodReference("Tick", module.TypeSystem.Void, installProbe.DeclaringType) { HasThis = false };
var optionsUpdate = module.Types.Single(type => type.Name == "OptionsManager").Methods.Single(method => method.Name == "Update");
optionsUpdate.Body.GetILProcessor().InsertBefore(optionsUpdate.Body.Instructions[0], Instruction.Create(OpCodes.Call, tickProbe));
var normalStart = Instruction.Create(OpCodes.Pop);
var sceneInstructions = new[] {
    Instruction.Create(OpCodes.Ldstr, "ULTRAKILL_MAC_START_SCENE"),
    Instruction.Create(OpCodes.Call, StringMethod("System", "Environment", "GetEnvironmentVariable", 1)),
    Instruction.Create(OpCodes.Dup),
    Instruction.Create(OpCodes.Brfalse, normalStart),
    Instruction.Create(OpCodes.Ldc_I4_1),
    Instruction.Create(OpCodes.Call, loadScene),
    Instruction.Create(OpCodes.Ret),
    normalStart,
};
foreach (var instruction in sceneInstructions) bootIl.InsertBefore(start, instruction);
// Time the rendering callbacks seen in the live CPU sample, without changing their behavior.
var timedMethods = new List<MethodDefinition>();
foreach (var spec in new[] { ("ULTRAKILL.Portal.PortalRenderV2", "Render", "BeginPortal"), ("PostProcessV2_Handler", "OnPreRenderCallback", "BeginPostProcess"), ("WallCheck", "CheckForCols", "BeginWallCheck") })
{
    var method = Types(module.Types).Single(t => t.FullName == spec.Item1).Methods.Single(m => m.Name == spec.Item2);
    var scope = new TypeReference("ULTRAKILL.MacPort", "FrameScopes", module, probeAssembly);
    var begin = new MethodReference(spec.Item3, module.TypeSystem.Void, scope) { HasThis = false };
    var end = new MethodReference("End" + spec.Item3[5..], module.TypeSystem.Void, scope) { HasThis = false };
    var processor = method.Body.GetILProcessor();
    processor.InsertBefore(method.Body.Instructions[0], Instruction.Create(OpCodes.Call, begin));
    foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
    {
        ret.OpCode = OpCodes.Call; ret.Operand = end;
        processor.InsertAfter(ret, Instruction.Create(OpCodes.Ret));
    }
    timedMethods.Add(method);
}
// Cecil does not expand short branch offsets after inserting larger blocks.
var branchOpcodes = typeof(OpCodes).GetFields().Select(field => field.GetValue(null)).OfType<OpCode>().ToDictionary(opcode => opcode.Name);
foreach (var method in bloodManager.Methods.Concat(new[] { schedule, slide }).Concat(graphics.Methods).Concat(timedMethods).Where(method => method.HasBody))
    foreach (var instruction in method.Body.Instructions)
        if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            instruction.OpCode = branchOpcodes[instruction.OpCode.Name[..^2]];
var temporary = output + ".pending";
assembly.Write(temporary);
File.Move(temporary, output, true);
Console.WriteLine($"Patched {calls.Length} data-path calls in copied assembly.");
