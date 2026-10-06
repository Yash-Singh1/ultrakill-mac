using ULTRAKILL.MacPort;
using System.Text.Json;
void Check(bool yes,string message) { if(!yes)throw new Exception(message); }
int assertions=0;
foreach (var fps in new[]{30,60,120,144,240,288})
{
    var s=new FrameDeadline();double now=10,start=10;
    for(int i=0;i<10000;i++)
    {
        double t=s.Advance(now,fps);
        Check(Math.Abs(t-(start+i/(double)fps))<1e-8,"Deadlines drifted");
        now=t+.001;assertions++;
    }
    now+=.1;Check(s.Advance(now,fps)==now,"Stall caused catch-up frames");assertions++;
    now+=.003;Check(s.Advance(now,fps/2)==now,"FPS change retained old deadline");assertions++;
    now+=.001;Check(s.Advance(now,-1)==now,"Unlimited retained limiter");assertions++;
    Check(s.Advance(now,120)==now,"Reenable retained old deadline");assertions++;
    s.Reset();Check(s.Advance(now,120)==now,"Reset retained old deadline");assertions++;
}
string macRoot=Path.GetFullPath("mac");
foreach (var dataPath in new[]{"ULTRAKILL.app/Contents","ULTRAKILL.app/Contents/Resources/Data"})
{
    Check(PortPaths.Diagnostics(Path.Combine(macRoot,dataPath))==Path.Combine(macRoot,"diagnostics"),"Diagnostics escaped mac directory");assertions++;
}
try { PortPaths.Diagnostics(Path.Combine(macRoot,"ULTRAKILL_Data"));throw new Exception("Non-app path accepted"); } catch(InvalidOperationException){assertions++;}
Console.WriteLine(JsonSerializer.Serialize(new { passed=true,assertions, tested_fps=new[]{30,60,120,144,240,288}, scenarios="Steady deadlines, long stalls, target changes, unlimited, reset. This is not an in-game benchmark."}));
