using UnityEngine;
using ULTRAKILL.Portal;

namespace ULTRAKILL.MacPort
{
    public static class PortalMeshCache
    {
        public static bool Enabled;
        public static int Hits, Misses;
        static PortalRenderV2 owner;
        static Mesh mesh;
        static Vector3[] positions;
        static int length=-1;
        public static bool RequiresRebuild(PortalRenderV2 renderer)
        {
            if (!Enabled) return true;
            var portals=renderer.scene.nativeScene.renderPortals;
            int count=portals.Length*4;
            bool changed=owner!=renderer || mesh!=renderer.totalPortalMesh || length!=count || renderer.totalPortalMesh.vertexCount!=count;
            owner=renderer; mesh=renderer.totalPortalMesh; length=count;
            if(positions==null || positions.Length<count) { positions=new Vector3[count]; changed=true; }
            for(int i=0;i<portals.Length;i++)
            {
                var portal=portals[i]; var settings=portal.renderData.renderSettings;
                bool hidden=settings==PortalSideFlags.None || settings==PortalSideFlags.Exit && portal.handle.side==PortalSide.Enter || settings==PortalSideFlags.Enter && portal.handle.side==PortalSide.Exit;
                for(int vertex=0;vertex<4;vertex++)
                {
                    var native=portal.vertices[vertex];
                    Vector3 position=hidden?Vector3.zero:new Vector3(native.x,native.y,native.z);
                    int index=i*4+vertex; var previous=positions[index];
                    changed |= position.x!=previous.x || position.y!=previous.y || position.z!=previous.z;
                    positions[index]=position;
                }
            }
            if(changed) Misses++; else Hits++;
            return changed;
        }
    }
}
