using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ULTRAKILL.MacPort
{
    // Diagnostic only. Never enabled in the shipping helper.
    public sealed class ShockwaveCheck : MonoBehaviour
    {
        float readyAt=-1;
        int errors;
        void OnEnable() { Application.logMessageReceived+=Log; }
        void OnDisable() { Application.logMessageReceived-=Log; }
        void Log(string message,string stack,LogType type)
        {
            if(message.Contains("CollisionMeshData") || message.Contains("Failed to create") || message.Contains("non-convex MeshCollider")) errors++;
        }
        void LateUpdate()
        {
            if(SceneHelper.CurrentScene!="Level 8-3" || SceneManager.GetActiveScene().name=="Bootstrap") return;
            if(readyAt<0)readyAt=Time.realtimeSinceStartup+12;
            if(Time.realtimeSinceStartup<readyAt)return;
            enabled=false;
            Application.logMessageReceived+=Log;
            try
            {
                var meshes=new HashSet<int>(); int tested=0;
                foreach(var original in Resources.FindObjectsOfTypeAll<MeshCollider>())
                {
                    var mesh=original.sharedMesh;
                    if(!mesh || (mesh.name!="Visual" && !original.GetComponentInParent<PhysicalShockwave>()) || !meshes.Add(mesh.GetInstanceID()))continue;
                    Debug.Log("[MacShockwave] SOURCE name="+mesh.name+" vertices="+mesh.vertexCount+" readable="+mesh.isReadable+" convex="+original.convex+" trigger="+original.isTrigger+" cooking="+original.cookingOptions);
                    for(int order=0;order<2;order++)
                    {
                        var go=new GameObject("Collision diagnostic");go.transform.position=new Vector3(100000,100000,100000);
                        var collider=go.AddComponent<MeshCollider>();int before=errors;
                        if(order==0) {collider.sharedMesh=mesh;collider.convex=original.convex;collider.isTrigger=original.isTrigger;}
                        else {collider.convex=original.convex;collider.isTrigger=original.isTrigger;collider.sharedMesh=mesh;}
                        Physics.SyncTransforms();int hits=Hits(collider);
                        Debug.Log("[MacShockwave] RESULT mesh="+mesh.name+" order="+(order==0?"original":"flags-first")+" errors="+(errors-before)+" hits="+hits+" bounds="+collider.bounds.size);
                        DestroyImmediate(go);
                    }
                    if(++tested>=12)break;
                }
                // A synthetic discarded-CPU mesh shows whether the assignment
                // ordering itself causes an error independent of depot assets.
                var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);
                var synthetic=Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
                Physics.BakeMesh(synthetic.GetInstanceID(),true);synthetic.UploadMeshData(true);
                for(int order=0;order<2;order++)
                {
                    var go=new GameObject("Synthetic collision diagnostic");go.transform.position=new Vector3(100000,100000,100000);
                    var collider=go.AddComponent<MeshCollider>();int before=errors;
                    if(order==0){collider.sharedMesh=synthetic;collider.convex=true;}
                    else {collider.convex=true;collider.sharedMesh=synthetic;}
                    Physics.SyncTransforms();Debug.Log("[MacShockwave] SYNTHETIC order="+order+" errors="+(errors-before)+" hits="+Hits(collider));DestroyImmediate(go);
                }
                DestroyImmediate(cube);DestroyImmediate(synthetic);
                Debug.Log("[MacShockwave] Complete meshes="+tested);
            }
            catch(Exception error){Debug.LogError("[MacShockwave] FAIL "+error);}
            Application.logMessageReceived-=Log;Application.Quit();
        }
        static int Hits(MeshCollider collider)
        {
            var bounds=collider.bounds;float distance=Math.Max(1,bounds.extents.magnitude*3);int hits=0;
            for(int y=0;y<3;y++)for(int angle=0;angle<16;angle++)
            {
                var center=bounds.center+Vector3.up*bounds.extents.y*(y-1)*.5f;
                var direction=new Vector3(Mathf.Cos(angle*Mathf.PI/8),0,Mathf.Sin(angle*Mathf.PI/8));
                if(collider.Raycast(new Ray(center+direction*distance,-direction),out var hit,distance*2))hits++;
            }
            return hits;
        }
    }
}
