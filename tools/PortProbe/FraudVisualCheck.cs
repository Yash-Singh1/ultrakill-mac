using System;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ULTRAKILL.MacPort
{
    // Explicit muted test process only. Uses copied profiles and offscreen cameras.
    public sealed class FraudVisualCheck : MonoBehaviour
    {
        float readyAt = -1;
        void LateUpdate()
        {
            if (SceneHelper.CurrentScene != "Level 8-3" || SceneManager.GetActiveScene().name == "Bootstrap") return;
            if (readyAt < 0) readyAt = Time.realtimeSinceStartup + 12;
            if (Time.realtimeSinceStartup < readyAt) return;
            enabled = false;
            try
            {
                Renderer source = null;
                foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
                    if (renderer.name == "MaliciousFace" && renderer.gameObject.scene.IsValid() && renderer.sharedMaterial && renderer.sharedMaterial.shader.name == "ULTRAKILL/Master")
                    { source = renderer; break; }
                if (!source) throw new Exception("No Maurice body renderer found in 8-3");
                Mesh mesh;
                if (source is SkinnedMeshRenderer skinned) { mesh = new Mesh(); skinned.BakeMesh(mesh); }
                else { var filter = source.GetComponent<MeshFilter>(); mesh = filter ? filter.sharedMesh : null; }
                if (!mesh) throw new Exception("Maurice body has no mesh");
                var target = new GameObject("Offscreen Maurice shader fixture"); target.layer = 31;
                target.AddComponent<MeshFilter>().sharedMesh = mesh;
                var rendererCopy = target.AddComponent<MeshRenderer>();
                var material = new Material(source.sharedMaterial);
                material.SetColor("_Color", Color.white);
                rendererCopy.sharedMaterial = material;
                var cameraObject = new GameObject("Offscreen shader fixture camera");
                var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
                camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.magenta; camera.nearClipPlane = .01f; camera.farClipPlane = 1000;
                var texture = new RenderTexture(256,256,24,RenderTextureFormat.ARGB32); texture.Create(); camera.targetTexture = texture;
                var readback = new Texture2D(256,256,TextureFormat.RGBA32,false);
                var bounds = mesh.bounds; float distance = Math.Max(.1f,bounds.extents.magnitude)*3;
                Shader.SetGlobalVector("_PortalClipPlane",Vector4.zero);
                Shader.SetGlobalFloat("_VertexColors",0);
                material.SetFloat("_ClipPlaneCount",0); material.SetVectorArray("_ClipPlanes",new Vector4[4]);
                int differences = 0, planeDifferences = 0, renderedPixels = 0, halfPixels = 0;
                for(int view=0;view<8;view++)
                {
                    float angle=view*Mathf.PI/4;
                    camera.transform.position=bounds.center+new Vector3(Mathf.Sin(angle),.2f,Mathf.Cos(angle))*distance;
                    camera.transform.LookAt(bounds.center);
                    material.DisableKeyword("PORTAL_CLIP_PLANE"); var baseline=Capture(camera,texture,readback);
                    material.EnableKeyword("PORTAL_CLIP_PLANE"); var clipped=Capture(camera,texture,readback);
                    for(int pixel=0;pixel<baseline.Length;pixel++)
                    {
                        if(!baseline[pixel].Equals(clipped[pixel])) differences++;
                        if(baseline[pixel].r!=255 || baseline[pixel].g!=0 || baseline[pixel].b!=255) renderedPixels++;
                    }
                    var planes = new Vector4[4];
                    for(int i=0;i<4;i++) planes[i]=new Vector4(1,0,0,-1000000);
                    material.SetVectorArray("_ClipPlanes",planes);
                    for(int count=1;count<=4;count++)
                    {
                        material.SetFloat("_ClipPlaneCount",count);
                        planeDifferences+=Differences(baseline,Capture(camera,texture,readback));
                    }
                    // The correctly translated global plane is an independent
                    // raster reference for each of the four per-object planes.
                    var halfPlane=new Vector4(1,0,0,-bounds.center.x);
                    material.SetFloat("_ClipPlaneCount",0); Shader.SetGlobalVector("_PortalClipPlane",halfPlane);
                    var half=Capture(camera,texture,readback);
                    for(int pixel=0;pixel<half.Length;pixel++)
                        if(half[pixel].r!=255 || half[pixel].g!=0 || half[pixel].b!=255) halfPixels++;
                    Shader.SetGlobalVector("_PortalClipPlane",Vector4.zero);
                    for(int plane=0;plane<4;plane++)
                    {
                        planes[plane]=halfPlane;
                        material.SetVectorArray("_ClipPlanes",planes); material.SetFloat("_ClipPlaneCount",plane+1);
                        planeDifferences+=Differences(half,Capture(camera,texture,readback));
                        planes[plane]=new Vector4(1,0,0,-1000000);
                    }
                    material.SetFloat("_ClipPlaneCount",0);
                }
                Debug.Log("[MacFraudVisual] Maurice="+source.name+" vertices="+mesh.vertexCount+" angles=8 clip_planes=0..4 differing_pixels="+differences+" plane_differing_pixels="+planeDifferences+" visible_pixels="+renderedPixels+" half_pixels="+halfPixels);
                if(differences!=0 || planeDifferences!=0 || renderedPixels==0 || halfPixels==0 || halfPixels>=renderedPixels) throw new Exception("Maurice portal-plane raster comparison failed");
                Debug.Log("[MacFraudVisual] PASS: real 8-3 Maurice mesh matches at eight angles. Each object clipping plane matches the global plane raster reference; disabled planes preserve the whole body.");
                Destroy(target); Destroy(cameraObject); Destroy(texture); Destroy(readback); Destroy(material);
            }
            catch(Exception error) { Debug.LogError("[MacFraudVisual] FAIL "+error); }
            Application.Quit();
        }
        static Color32[] Capture(Camera camera,RenderTexture texture,Texture2D readback)
        {
            camera.Render(); var previous=RenderTexture.active; RenderTexture.active=texture;
            readback.ReadPixels(new Rect(0,0,256,256),0,0);readback.Apply();RenderTexture.active=previous;
            return readback.GetPixels32();
        }
        static int Differences(Color32[] a,Color32[] b)
        {
            int count=0;for(int i=0;i<a.Length;i++)if(!a[i].Equals(b[i]))count++;return count;
        }
    }
}
