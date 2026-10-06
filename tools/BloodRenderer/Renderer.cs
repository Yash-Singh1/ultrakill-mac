using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
namespace ULTRAKILL.MacPort
{
    public static class BloodRenderer
    {
        sealed class State
        {
            public NativeArray<GenerateBloodMeshJob.VertexData> vertices;
            public int capacity;
            public Bounds bounds;
            public bool hasBounds;
        }
        static readonly Dictionary<BloodsplatterManager, State> states = new Dictionary<BloodsplatterManager, State>();
        static readonly VertexAttributeDescriptor[] layout = {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 3)
        };
        const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
        public static void Update(BloodsplatterManager b, int count, int begin, int end)
        {
            if (!states.TryGetValue(b, out var state)) { state = new State(); states.Add(b, state); }
            var mesh = b.totalStainMesh;
            if (count > state.capacity)
            {
                int capacity = Math.Min(b.props.Length, Math.Max(256, Mathf.NextPowerOfTwo(count)));
                if (state.vertices.IsCreated) state.vertices.Dispose();
                state.vertices = new NativeArray<GenerateBloodMeshJob.VertexData>(capacity * 4, Allocator.Persistent);
                state.capacity = capacity;
                mesh.Clear(); mesh.SetVertexBufferParams(capacity * 4, layout);
                // Indices depend only on the slot, so upload them once per allocation.
                var indices = new uint[capacity * 6];
                for (int i = 0; i < capacity; i++) { int at=i*6; uint v=(uint)(i*4); indices[at]=v; indices[at+1]=v+1; indices[at+2]=v+2; indices[at+3]=v; indices[at+4]=v+2; indices[at+5]=v+3; }
                mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
                mesh.SetIndexBufferData(indices, 0, 0, indices.Length, flags);
                // Initialize unused slots too, so no undefined vertex data reaches Unity.
                mesh.SetVertexBufferData(state.vertices, 0, 0, state.vertices.Length, 0, flags);
                begin=0; end=count;
            }
            if (begin == int.MaxValue || begin < 0 || end <= begin) { begin=0; end=count; }
            begin=Math.Min(begin,count); end=Math.Min(end,count);
            for (int i = begin; i < end; i++)
            {
                var p=b.props[i]; float3 normal=p.norm; float length=math.lengthsq(normal);
                bool valid=math.all(math.isfinite(p.pos)) && math.all(math.isfinite(normal)) && math.isfinite(length) && length>1e-12f;
                if (!valid) { for (int j=0;j<4;j++) state.vertices[i*4+j]=default; continue; }
                normal*=math.rsqrt(length); math.orthonormal_basis(normal,out var basis,out var unused);
                var matrix=float4x4.TRS(p.pos,math.mul(quaternion.LookRotation(normal,basis),quaternion.RotateZ(i%359)),new float3(1.28f,1.28f,1));
                for (int j=0;j<4;j++)
                {
                    var vertex=new GenerateBloodMeshJob.VertexData { position=math.mul(matrix,new float4(j==0||j==3?-1:1,j<2?1:-1,0,1)).xyz, normal_Offset=(half4)new float4(p.norm,i), uv=new half2((half)(j==1||j==2?1:0),(half)(j>=2?1:0)),center=p.pos };
                    state.vertices[i*4+j]=vertex;
                    Vector3 point=vertex.position;
                    if (!state.hasBounds) { state.bounds=new Bounds(point,Vector3.zero); state.hasBounds=true; } else state.bounds.Encapsulate(point);
                }
            }
            if (end>begin) mesh.SetVertexBufferData(state.vertices,begin*4,begin*4,(end-begin)*4,0,flags);
            mesh.subMeshCount=1;
            mesh.SetSubMesh(0,new SubMeshDescriptor(0,count*6) { bounds=state.bounds,vertexCount=count*4 },flags);
            mesh.bounds=state.hasBounds?state.bounds:new Bounds(Vector3.zero,Vector3.zero);
            b.meshDirty=false;
        }
        public static void Release(BloodsplatterManager b)
        {
            if (states.TryGetValue(b,out var s)) { if (s.vertices.IsCreated) s.vertices.Dispose(); states.Remove(b); }
        }
    }
}
