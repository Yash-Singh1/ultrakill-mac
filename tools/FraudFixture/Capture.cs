using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using ULTRAKILL.Portal;

namespace ULTRAKILL.MacPort.Fixtures
{
    [Serializable] public sealed class TransformState
    {
        public int[] indices;
        public string[] names;
        public Vector3 position, scale;
        public Quaternion rotation;
        public bool active;
    }
    [Serializable] public sealed class Snapshot
    {
        public int schema=1;
        public string scene, unityScene, capturedUtc;
        public Vector3 playerPosition, cameraPosition, gravity;
        public Quaternion playerRotation, cameraRotation;
        public float cameraPitch, cameraYaw, fov, aspect, nearClip, farClip;
        public Matrix4x4 projection;
        public bool fog;
        public Color fogColor;
        public float fogStart, fogEnd;
        public int portalCount, portalCandidates, portalPasses, maxRecursions;
        public int width, height;
        [NonSerialized] public List<TransformState> transforms=new List<TransformState>();
    }
    public static class Capture
    {
        public static void Install()
        {
            var root=new GameObject("One-shot Fraud view capture");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.AddComponent<OneShotCapture>();
        }
        public static Snapshot Read()
        {
            var player=MonoSingleton<NewMovement>.Instance;
            var controller=MonoSingleton<CameraController>.Instance;
            var cam=controller.cam;
            var manager=MonoSingleton<PortalManagerV2>.Instance;
            var scene=SceneManager.GetActiveScene();
            var result=new Snapshot {
                scene=SceneHelper.CurrentScene,unityScene=scene.name,capturedUtc=DateTime.UtcNow.ToString("O"),
                playerPosition=player.transform.position,playerRotation=player.transform.rotation,
                cameraPosition=cam.transform.position,cameraRotation=cam.transform.rotation,
                cameraPitch=controller.rotationX,cameraYaw=controller.rotationY,gravity=controller.gravityVec,
                fov=cam.fieldOfView,aspect=cam.aspect,nearClip=cam.nearClipPlane,farClip=cam.farClipPlane,projection=cam.projectionMatrix,
                fog=RenderSettings.fog,fogColor=RenderSettings.fogColor,fogStart=RenderSettings.fogStartDistance,fogEnd=RenderSettings.fogEndDistance,
                portalCount=manager.portalCount,portalCandidates=manager.render.renderDatas.Length,maxRecursions=manager.maxRecursions,
                width=Screen.width,height=Screen.height
            };
            var helper=Type.GetType("ULTRAKILL.MacPort.FrameScopes, PortProbe");
            var counter=helper?.GetProperty("PortalCameraPasses");
            if(counter!=null) result.portalPasses=(int)counter.GetValue(null);
            foreach(var root in scene.GetRootGameObjects()) Walk(root.transform,result.transforms,new List<int>(),new List<string>());
            return result;
        }
        static void Walk(Transform transform,List<TransformState> states,List<int> indices,List<string> names)
        {
            indices.Add(transform.GetSiblingIndex());names.Add(transform.name);
            states.Add(new TransformState {indices=indices.ToArray(),names=names.ToArray(),position=transform.localPosition,rotation=transform.localRotation,scale=transform.localScale,active=transform.gameObject.activeSelf});
            for(int i=0;i<transform.childCount;i++) Walk(transform.GetChild(i),states,indices,names);
            indices.RemoveAt(indices.Count-1);names.RemoveAt(names.Count-1);
        }
    }
    [DefaultExecutionOrder(32000)]
    public sealed class OneShotCapture : MonoBehaviour
    {
        bool saved;
        void LateUpdate()
        {
            if(saved) return;
            // The smoke process is allowed to reach its scene normally first.
            if(string.IsNullOrEmpty(SceneHelper.CurrentScene) || !SceneHelper.CurrentScene.StartsWith("Level 8-")) return;
            saved=true;
            string path=Environment.GetEnvironmentVariable("ULTRAKILL_MAC_FRAUD_SNAPSHOT") ?? Path.Combine(Directory.GetCurrentDirectory(),"reports/fraud-8-3-fixture.json");
            try
            {
                var snapshot=Capture.Read();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path+".tmp",JsonUtility.ToJson(snapshot,true));
                if(File.Exists(path)) File.Delete(path);
                File.Move(path+".tmp",path);
                // Unity's serializer omits custom lists in an assembly loaded
                // after startup. Store the hierarchy explicitly in a binary
                // sidecar instead of silently losing the room state.
                using(var writer=new BinaryWriter(File.Create(Path.ChangeExtension(path,"transforms.bin"))))
                {
                    writer.Write(1);writer.Write(snapshot.transforms.Count);
                    foreach(var state in snapshot.transforms)
                    {
                        writer.Write(state.indices.Length);
                        for(int i=0;i<state.indices.Length;i++) {writer.Write(state.indices[i]);writer.Write(state.names[i]);}
                        writer.Write(state.position.x);writer.Write(state.position.y);writer.Write(state.position.z);
                        writer.Write(state.rotation.x);writer.Write(state.rotation.y);writer.Write(state.rotation.z);writer.Write(state.rotation.w);
                        writer.Write(state.scale.x);writer.Write(state.scale.y);writer.Write(state.scale.z);writer.Write(state.active);
                    }
                }
                // MainTex is cleared during the late portal prepass. Capture
                // images after an explicit render in Replay, not at this point.
                Debug.Log("[MacFraudFixture] Saved "+path+"; scene="+snapshot.scene+" transforms="+snapshot.transforms.Count+" portal_passes="+snapshot.portalPasses);
            }
            catch(Exception e) {Debug.LogException(e);File.WriteAllText(path+".error.txt",e.ToString());}
            finally {Destroy(gameObject);}
        }
    }
}
