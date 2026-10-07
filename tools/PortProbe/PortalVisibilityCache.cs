using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using ULTRAKILL.Portal;
using ULTRAKILL.Portal.Native;

namespace ULTRAKILL.MacPort
{
    // Cache visibility, never room images or dynamic effects.
    // Every changed input falls back to the original current-frame GPU readback.
    public static unsafe class PortalVisibilityCache
    {
        public static bool Enabled;
        public static int Hits, Misses;
        static PortalRenderV2 owner, currentOwner;
        static Mesh occluder, currentOccluder;
        static RenderTexture colorTarget, depthTarget, currentColorTarget, currentDepthTarget;
        static Bounds meshBounds, currentMeshBounds;
        static int vertexCount, currentVertexCount;
        static Matrix4x4 projection, currentProjection;
        static int width, height, currentWidth, currentHeight, crc, currentCrc;
        static bool valid, same, inputsAvailable;
        static ulong savedBitset;
        static byte[] prepassBytes, portalBytes, renderBytes, onscreenBytes;
        static void* prepass, portals, renders;
        static int prepassLength, portalLength, renderLength;
        static bool EqualsBytes(void* source, int count, byte[] saved)
        {
            if (saved == null || saved.Length != count) return false;
            if (count == 0) return true;
            fixed(byte* target=saved) return UnsafeUtility.MemCmp(source,target,count)==0;
        }
        static void Store(void* source, int count, ref byte[] saved)
        {
            if (saved == null || saved.Length != count) saved=new byte[count];
            if (count != 0) fixed(byte* target=saved) UnsafeUtility.MemCpy(target,source,count);
        }
        static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            for(int i=0;i<16;i++) if(a[i]!=b[i]) return false;
            return true;
        }
        public static bool CanReuse => Enabled && same;
        public static void Invalidate() { valid=false; same=false; inputsAvailable=false; }
        public static void Begin(PortalRenderV2 renderer, Camera camera, Mesh mesh, IntPtr data, int bytes)
        {
            same=false; inputsAvailable=false;
            if(!Enabled) { valid=false; return; }
            if(renderer.scene==null || !renderer.scene.nativeScene.valid || !renderer.scene.nativeScene.renderPortals.IsCreated || !renderer.renderDatas.IsCreated) { valid=false; return; }
            currentOwner=renderer; currentOccluder=mesh; currentProjection=camera.projectionMatrix;
            var post=MonoSingleton<PostProcessV2_Handler>.Instance;
            currentColorTarget=post.mainTex; currentDepthTarget=post.depthBuffer;
            currentWidth=post.mainTex.width; currentHeight=post.mainTex.height;
            currentVertexCount=mesh ? mesh.vertexCount : 0; currentMeshBounds=mesh ? mesh.bounds : default;
            currentCrc=renderer.portalCompositeMaterial.ComputeCRC();
            prepass=data.ToPointer(); prepassLength=bytes;
            var native=renderer.scene.nativeScene.renderPortals;
            portals=native.GetUnsafeReadOnlyPtr(); portalLength=native.Length*UnsafeUtility.SizeOf<NativePortal>();
            renders=renderer.renderDatas.GetUnsafeReadOnlyPtr(); renderLength=renderer.renderDatas.Length*UnsafeUtility.SizeOf<PortalRenderV2.RenderData>();
            inputsAvailable=true;
            same=valid && owner==currentOwner && occluder==currentOccluder && colorTarget==currentColorTarget && depthTarget==currentDepthTarget
                && vertexCount==currentVertexCount && meshBounds.Equals(currentMeshBounds) && width==currentWidth && height==currentHeight && crc==currentCrc
                && SameMatrix(projection,currentProjection) && EqualsBytes(prepass,prepassLength,prepassBytes)
                && EqualsBytes(portals,portalLength,portalBytes) && EqualsBytes(renders,renderLength,renderBytes);
        }
        public static ulong Resolve(ref NativeList<PortalRenderV2.OnscreenPortalData> data, ulong original, ref AsyncGPUReadbackRequest request)
        {
            if(!Enabled) return original;
            void* pointer=data.GetUnsafeReadOnlyPtr(); int size=data.Length*UnsafeUtility.SizeOf<PortalRenderV2.OnscreenPortalData>();
            if(same && EqualsBytes(pointer,size,onscreenBytes)) { Hits++; return savedBitset; }
            if(!request.done)
            {
                long start=Stopwatch.GetTimestamp(); request.WaitForCompletion();
                JobTuning.PortalWaitTicks+=Stopwatch.GetTimestamp()-start; JobTuning.PortalWaitCalls++;
            }
            Misses++;
            if(request.hasError) { valid=false; return original; }
            ulong result=request.GetData<ulong>()[0];
            if(!inputsAvailable) { valid=false; return result; }
            Store(prepass,prepassLength,ref prepassBytes); Store(portals,portalLength,ref portalBytes);
            Store(renders,renderLength,ref renderBytes); Store(pointer,size,ref onscreenBytes);
            owner=currentOwner; occluder=currentOccluder; projection=currentProjection;
            colorTarget=currentColorTarget; depthTarget=currentDepthTarget;
            vertexCount=currentVertexCount; meshBounds=currentMeshBounds;
            width=currentWidth; height=currentHeight; crc=currentCrc; savedBitset=result; valid=true;
            return result;
        }
    }
}
