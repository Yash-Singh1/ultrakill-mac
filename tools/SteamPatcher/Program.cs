using Mono.Cecil;
using System.Text.Json;

if(args.Length < 2) throw new ArgumentException("SteamPatcher inspect ASSEMBLY | patch SOURCE OUTPUT PACKING_JSON REPORT_JSON");
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) {
    foreach(var type in types) { yield return type; foreach(var nested in Types(type.NestedTypes)) yield return nested; }
}
using var assembly=AssemblyDefinition.ReadAssembly(args[1]);
var types=Types(assembly.MainModule.Types).ToArray();
if(args[0]=="inspect") {
    Console.WriteLine(JsonSerializer.Serialize(new {
        assembly=assembly.Name.FullName,
        structures=types.Where(t=>t.IsValueType && t.PackingSize>0).Select(t=>new {name=t.FullName,pack=t.PackingSize,layout=t.IsExplicitLayout?"explicit":"sequential",callback=t.Interfaces.Any(i=>i.InterfaceType.Name=="ICallbackData")}),
        nativeImports=types.SelectMany(t=>t.Methods).Where(m=>m.HasPInvokeInfo).Select(m=>new {method=m.FullName,library=m.PInvokeInfo.Module.Name,entry=m.PInvokeInfo.EntryPoint}),
        mocCalls=types.SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions.Where(i=>i.Operand is MethodReference r && r.DeclaringType.FullName.Contains("MaskedOcclusionCulling")).Select(i=>new {caller=m.FullName,target=i.Operand.ToString()}))
    })); return;
}
if(args.Length!=5 || args[0]!="patch")throw new ArgumentException("Invalid patch arguments");
var output=Path.GetFullPath(args[2]);
if(output==Path.GetFullPath(args[1]) || !output.Contains("/mac/"))throw new ArgumentException("Patch only copied assemblies under mac/");
var mapping=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[3]));
var changed=new List<string>();var unknown=new List<string>();
foreach(var type in types.Where(t=>t.IsValueType && t.PackingSize==8)) {
    if((mapping.TryGetValue(type.Name,out var pack) && pack=="Platform.StructPlatformPackSize") || type.Interfaces.Any(i=>i.InterfaceType.Name=="ICallbackData")) {type.PackingSize=4;changed.Add(type.FullName);}
    // Steam networking explicitly uses pack 8 on every platform. Motion data
    // has only float32 fields, so pack 8 and pack 4 have identical layouts.
    else if(type.FullName is "Steamworks.Data.ConnectionLaneStatus" or "Steamworks.Data.ConnectionStatus") { }
    else if(type.FullName=="Steamworks.Data.InputMotionDataV2_t" && type.Fields.Where(f=>!f.IsStatic).All(f=>f.FieldType.MetadataType==MetadataType.Single)) { }
    else if(!type.IsExplicitLayout)unknown.Add(type.FullName);
}
if(unknown.Count>0)throw new InvalidOperationException("Unverified Windows struct layouts: "+string.Join(", ",unknown));
var imports=types.SelectMany(t=>t.Methods).Where(m=>m.HasPInvokeInfo).ToArray();
var wrong=imports.Where(m=>m.PInvokeInfo.Module.Name!="steam_api64").ToArray();
if(wrong.Length>0)throw new InvalidOperationException("Unexpected native dependency: "+wrong[0].FullName);
const string macLibrary="@executable_path/../Frameworks/libsteam_api.dylib";
foreach(var reference in assembly.MainModule.ModuleReferences.Where(m=>m.Name=="steam_api64"))reference.Name=macLibrary;
var platform=types.Single(t=>t.FullName=="Steamworks.Platform");
platform.Fields.Single(f=>f.Name=="StructPlatformPackSize").Constant=4;
platform.Fields.Single(f=>f.Name=="LibraryName").Constant=macLibrary;
assembly.Write(output);
File.WriteAllText(args[4],JsonSerializer.Serialize(new {source=Path.GetFullPath(args[1]),output,identity=assembly.Name.FullName,nativeImportCount=imports.Length,nativeEntryPoints=imports.Select(m=>m.PInvokeInfo.EntryPoint).Distinct().Order().ToArray(),posixPackingTypes=changed,explicitLayoutsPreserved=types.Where(t=>t.IsExplicitLayout && t.PackingSize==8).Select(t=>t.FullName).ToArray()},new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine($"Mapped {imports.Length} Steam imports to macOS and repaired {changed.Count} platform struct layouts.");
