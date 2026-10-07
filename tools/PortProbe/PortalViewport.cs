using UnityEngine;
using UnityEngine.Rendering;
using ULTRAKILL.Portal;

namespace ULTRAKILL.MacPort
{
    // Kept behind the isolated benchmark switch until raster comparisons pass.
    public static class PortalViewport
    {
        public static bool Enabled;
        static CommandBuffer begin, end;
        static Camera attached;
        public static void Render(Camera camera, ref PortalRenderV2.RenderData data)
        {
            if (!Enabled) { camera.Render(); return; }
            var manager = MonoSingleton<PortalManagerV2>.Instance;
            var post = MonoSingleton<PostProcessV2_Handler>.Instance;
            var portal = manager.Scene.nativeScene.renderPortals[data.handleIndex];
            Matrix4x4 projection = camera.projectionMatrix;
            if (portal.renderData.mirror) projection = Matrix4x4.Scale(new Vector3(-1,1,1)) * projection;
            Matrix4x4 matrix = projection * data.enterViewMatrix;
            float left=1, right=-1, bottom=1, top=-1;
            bool safe=true;
            for(int i=0;i<4;i++)
            {
                var vertex=portal.vertices[i];
                Vector4 clip=matrix*new Vector4(vertex.x,vertex.y,vertex.z,1);
                if (clip.w < .001f) { safe=false; break; }
                float x=clip.x/clip.w, y=clip.y/clip.w;
                left=Mathf.Min(left,x); right=Mathf.Max(right,x);
                bottom=Mathf.Min(bottom,y); top=Mathf.Max(top,y);
            }
            if (!safe) { camera.Render(); return; }
            // Cover both render-texture Y orientations conservatively. A later
            // raster check can establish which convention the camera uses.
            float low=Mathf.Min(bottom,-top), high=Mathf.Max(top,-bottom);
            int width=post.mainTex.width, height=post.mainTex.height;
            float x0=Mathf.Clamp(Mathf.Floor((left+1)*.5f*width)-4,0,width);
            float x1=Mathf.Clamp(Mathf.Ceil((right+1)*.5f*width)+4,0,width);
            float y0=Mathf.Clamp(Mathf.Floor((low+1)*.5f*height)-4,0,height);
            float y1=Mathf.Clamp(Mathf.Ceil((high+1)*.5f*height)+4,0,height);
            if (x1<=x0 || y1<=y0) { camera.Render(); return; }
            if (begin==null)
            {
                begin=new CommandBuffer { name="Portal pixel bounds" };
                end=new CommandBuffer { name="Restore portal pixel bounds" };
                end.DisableScissorRect();
            }
            if (attached)
            {
                attached.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,begin);
                attached.RemoveCommandBuffer(CameraEvent.AfterEverything,end);
            }
            attached=camera; begin.Clear(); begin.EnableScissorRect(new Rect(x0,y0,x1-x0,y1-y0));
            camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,begin);
            camera.AddCommandBuffer(CameraEvent.AfterEverything,end);
            try { camera.Render(); }
            finally
            {
                camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,begin);
                camera.RemoveCommandBuffer(CameraEvent.AfterEverything,end);
                Graphics.ExecuteCommandBuffer(end);
            }
        }
    }
}
