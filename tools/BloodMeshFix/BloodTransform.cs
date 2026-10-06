using Unity.Mathematics;

namespace ULTRAKILL.MacPort
{
    public static class BloodMesh
    {
        public static float4x4 Transform(float3 pos, float3 normal, int index)
        {
            // Deleted stain slots contain a zero normal. Collapse their quad
            // before LookRotation can normalize zero and contaminate the mesh.
            float lengthSquared = math.lengthsq(normal);
            if (!math.all(math.isfinite(pos)) || !math.all(math.isfinite(normal)) ||
                !(lengthSquared > 1e-12f) || !math.isfinite(lengthSquared))
                return default(float4x4);
            normal *= math.rsqrt(lengthSquared);
            math.orthonormal_basis(normal, out var basis, out var unused);
            return float4x4.TRS(pos, math.mul(quaternion.LookRotation(normal, basis),
                quaternion.RotateZ(index % 359)), new float3(1.28f, 1.28f, 1f));
        }
    }
}
