using System.Reflection;
using System.Runtime.Loader;
var compiler = Path.GetFullPath(args[0]);
var context = new CompilerContext(Path.GetDirectoryName(compiler)!);
var loaded = context.LoadFromAssemblyPath(compiler);
var entry = loaded.EntryPoint ?? loaded.GetType("Burst.Bcl.Program")!.GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
try { var result = entry.Invoke(null, new object[] { args.Skip(1).ToArray() }); return result is int code ? code : Environment.ExitCode; }
catch (TargetInvocationException e) { Console.Error.WriteLine(e.InnerException); return 1; }
sealed class CompilerContext(string folder) : AssemblyLoadContext
{
 protected override Assembly? Load(AssemblyName name) {
   var path = Path.Combine(folder, name.Name + ".dll");
   return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
 }
 protected override IntPtr LoadUnmanagedDll(string name) {
   foreach(var file in new[]{name,"lib"+name+".dylib",name+".dylib"}) {
     var path=Path.Combine(folder,file); if(File.Exists(path))return LoadUnmanagedDllFromPath(path);
   }
   return IntPtr.Zero;
 }
}
