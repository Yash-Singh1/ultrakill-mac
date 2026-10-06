using UnityEngine;
using ULTRAKILL.MacPort;
using System.Reflection;
using System.Text.Json;
int n=0;void Check(bool v,string reason){n++;if(!v)throw new Exception(reason);}
Application.targetFrameRate=120;FramePacing.Install(new GameObject());
Application.targetFrameRate=240;FramePacing.FrameLimitChanged();Check(FramePacing.RequestedRate==240 && Application.targetFrameRate==240,"VSync cap changed");
QualitySettings.vSyncCount=0;FramePacing.VSyncChanged();Check(Application.targetFrameRate==-1 && FramePacing.RequestedRate==240,"Precise limiter lost target");
var instance=(FramePacing)typeof(FramePacing).GetField("instance",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
void Toggle(){Input.press=true;typeof(FramePacing).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(instance,null);}
Toggle();Check(!FramePacing.Precise && Application.targetFrameRate==240,"F9 failed to restore Unity cap");
Application.targetFrameRate=144;FramePacing.FrameLimitChanged();Check(FramePacing.RequestedRate==144 && Application.targetFrameRate==144,"Changing cap in Unity mode failed");
Toggle();Check(FramePacing.Precise && Application.targetFrameRate==-1,"F9 failed to restore precise cap");
QualitySettings.vSyncCount=1;FramePacing.VSyncChanged();Check(Application.targetFrameRate==144,"Enabling VSync lost selected cap");
QualitySettings.vSyncCount=0;FramePacing.VSyncChanged();Application.targetFrameRate=-1;FramePacing.FrameLimitChanged();Check(FramePacing.RequestedRate==-1,"Unlimited did not disable limiter");
Toggle();Check(Application.targetFrameRate==-1,"Unlimited lost on toggle");
var wall=new WallCheck{currentCollider=new MeshCollider{convex=false},cached=new Vector3{value=77}};
var contact=SlideContact.Find(wall,new Vector3{value=9});Check(contact.value==77 && wall.calls==1 && wall.currentCollider.calls==0,"Concave query was not cached");
wall.currentCollider=new MeshCollider{convex=true};contact=SlideContact.Find(wall,new Vector3{value=9});Check(contact.value==9 && wall.currentCollider.calls==1 && wall.calls==1,"Convex query changed");
wall.currentCollider=new Collider();contact=SlideContact.Find(wall,new Vector3{value=19});Check(contact.value==19 && wall.currentCollider.calls==1,"Primitive query changed");
Console.WriteLine(JsonSerializer.Serialize(new{passed=true,assertions=n,scope="Graphics settings and F9 state transitions; cached concave contact; convex/primitive query behavior. Uses Unity API stubs, not the running player."}));
