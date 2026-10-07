using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using ULTRAKILL.Portal;

namespace ULTRAKILL.MacPort.Fixtures
{
    // Only invoked explicitly by the isolated Fraud benchmark.
    public static class Replay
    {
        static bool WorldRoot(string name) => name=="Pre-Space" || name=="Space" || name=="SpaceCheckpoints" || name=="Interhall" || name=="Interhal -> 11" || name=="10 -> Interhall" || name=="OOB Stuff" || name=="Navigation";
        public static Snapshot Apply(string path)
        {
            var snapshot=JsonUtility.FromJson<Snapshot>(File.ReadAllText(path));
            if(snapshot.schema!=1 || SceneHelper.CurrentScene!=snapshot.scene) throw new InvalidDataException("Fixture scene/version mismatch");
            var roots=new Dictionary<string,Transform>();
            foreach(var root in SceneManager.GetActiveScene().GetRootGameObjects()) if(!roots.ContainsKey(root.name)) roots[root.name]=root.transform;
            var resolved=new Dictionary<string,Transform>();
            var states=new List<(Transform transform,TransformState state)>();
            var missing=new List<string>();
            int clones=0;
            using(var reader=new BinaryReader(File.OpenRead(Path.ChangeExtension(path,"transforms.bin"))))
            {
                if(reader.ReadInt32()!=1) throw new InvalidDataException("Unknown hierarchy version");
                int count=reader.ReadInt32();
                for(int row=0;row<count;row++)
                {
                    int depth=reader.ReadInt32();
                    if(depth<1 || depth>256) throw new InvalidDataException("Invalid hierarchy depth");
                    var state=new TransformState {indices=new int[depth],names=new string[depth]};
                    for(int i=0;i<depth;i++) {state.indices[i]=reader.ReadInt32();state.names[i]=reader.ReadString();}
                    state.position=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                    state.rotation=new Quaternion(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                    state.scale=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());state.active=reader.ReadBoolean();
                    if(!WorldRoot(state.names[0])) continue;
                    string key="";Transform target=null;
                    for(int i=0;i<depth;i++)
                    {
                        key+=(i==0 ? "" : "/")+state.indices[i]+":"+state.names[i];
                        if(resolved.TryGetValue(key,out var cached)) {target=cached;continue;}
                        if(i==0) roots.TryGetValue(state.names[0],out target);
                        else if(target)
                        {
                            Transform parent=target;target=null;int index=state.indices[i];string name=state.names[i];
                            if(index>=0 && index<parent.childCount && parent.GetChild(index).name==name) target=parent.GetChild(index);
                            if(!target) for(int child=0;child<parent.childCount;child++) if(parent.GetChild(child).name==name) {target=parent.GetChild(child);break;}
                            if(!target && name.EndsWith("(Clone)",StringComparison.Ordinal))
                            {
                                string original=name.Substring(0,name.Length-7);
                                for(int child=0;child<parent.childCount;child++) if(parent.GetChild(child).name==original)
                                {
                                    target=UnityEngine.Object.Instantiate(parent.GetChild(child).gameObject,parent,false).transform;target.name=name;clones++;break;
                                }
                            }
                        }
                        resolved[key]=target;
                    }
                    if(target) states.Add((target,state));else missing.Add(string.Join("/",state.names));
                }
            }
            // Restore local geometry before enabling rooms, so OnEnable sees
            // the captured portal transforms rather than the initial layout.
            foreach(var pair in states) {pair.transform.localPosition=pair.state.position;pair.transform.localRotation=pair.state.rotation;pair.transform.localScale=pair.state.scale;}
            foreach(var pair in states) pair.transform.gameObject.SetActive(pair.state.active);
            int enemies=0;
            foreach(var enemy in UnityEngine.Object.FindObjectsOfType<EnemyIdentifier>(true)) {enemy.gameObject.SetActive(false);enemies++;}
            foreach(var flipper in UnityEngine.Object.FindObjectsOfType<PortalFlipper>(true)) flipper.enabled=false;
            var player=MonoSingleton<NewMovement>.Instance;player.enabled=false;player.transform.SetPositionAndRotation(snapshot.playerPosition,snapshot.playerRotation);
            var controller=MonoSingleton<CameraController>.Instance;controller.enabled=false;
            controller.rotationX=snapshot.cameraPitch;controller.rotationY=snapshot.cameraYaw;controller.gravityVec=snapshot.gravity;
            var camera=controller.cam;camera.transform.SetPositionAndRotation(snapshot.cameraPosition,snapshot.cameraRotation);
            camera.fieldOfView=snapshot.fov;camera.aspect=snapshot.aspect;camera.nearClipPlane=snapshot.nearClip;camera.farClipPlane=snapshot.farClip;
            camera.projectionMatrix=snapshot.projection;camera.ResetCullingMatrix();camera.ResetWorldToCameraMatrix();
            RenderSettings.fog=snapshot.fog;RenderSettings.fogColor=snapshot.fogColor;RenderSettings.fogStartDistance=snapshot.fogStart;RenderSettings.fogEndDistance=snapshot.fogEnd;
            var manager=MonoSingleton<PortalManagerV2>.Instance;manager.maxRecursions=snapshot.maxRecursions;manager.UpdateTraveller(player);
            manager.Scene.Sync(new List<ULTRAKILL.Portal.Portal>(UnityEngine.Object.FindObjectsOfType<ULTRAKILL.Portal.Portal>()));
            Debug.Log("[MacFraudFixture] Restored scene="+snapshot.scene+" transforms="+states.Count+" clones="+clones+" missing="+missing.Count+" enemies_disabled="+enemies+" portals="+manager.portalCount+" camera="+camera.transform.position);
            File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("ULTRAKILL_MAC_DIAGNOSTICS_PATH") ?? Path.GetTempPath(),"fraud-fixture-replay-missing.txt"),missing);
            return snapshot;
        }
    }
}
