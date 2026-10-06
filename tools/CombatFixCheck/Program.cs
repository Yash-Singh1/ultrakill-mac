using ULTRAKILL.MacPort;
using UnityEngine;
using ULTRAKILL.Enemy;
using System.Text.Json;
int checks=0;void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
Check(!CombatFixes.TargetIsAlive(null),"CLR null accepted");
Check(CombatFixes.TargetIsAlive(new OtherTarget()),"Non-Unity target rejected");
var target=new UnityTarget();Check(CombatFixes.TargetIsAlive(target),"Live target rejected");target.alive=false;
ITarget stale=target;Check(stale!=null&&!CombatFixes.TargetIsAlive(stale),"Destroyed Unity target accepted via interface");
for(int i=1;i<=100;i++){
 var origin=new Vector3{value=i};
 var simple=new Collider();Check(CombatFixes.EnvironmentContact(simple,origin).value==i&&simple.closestQueries==1,"Simple collider behavior changed");
 var convex=new MeshCollider{convex=true};Check(CombatFixes.EnvironmentContact(convex,origin).value==i&&convex.closestQueries==1,"Convex mesh behavior changed");
 var mesh=new MeshCollider{hit=true,bounds=new(){center=new(){value=200},extents=new(){value=10}},point=new(){value=150}};
 Check(CombatFixes.EnvironmentContact(mesh,origin).value==150&&mesh.closestQueries==0&&mesh.rays==1&&mesh.boundsQueries==0,"Concave ray hit not used");
 mesh.hit=false;Check(CombatFixes.EnvironmentContact(mesh,origin).value==150&&mesh.closestQueries==0&&mesh.boundsQueries==1,"Concave fallback failed");
}
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,assertions=checks,scope="Destroyed Unity targets behind interface references; convex and primitive queries; concave debris ray hit and bounds fallback. Uses Unity API stubs."}));
class OtherTarget:ITarget {}
class UnityTarget:UnityEngine.Object,ITarget {}
