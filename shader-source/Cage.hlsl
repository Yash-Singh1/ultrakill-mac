// Reconstructed subset of ULTRAKILL/Master, from its compiled Windows programs.
// Vertex variant 5083: TRANSPARENCY, VERTEX_LIGHTING, _FOG_ON.
// Fragment variant 9483: TRANSPARENCY, _FOG_ON.
// This is readable recovered behavior, not the original authored shader source.

cbuffer Globals : register(b0)
{
    float _ForceOutlineBehind : packoffset(c3.z);
    float _VertexColors : packoffset(c3.w);
    float _Opacity : packoffset(c9.x);
};
cbuffer UnityPerCamera : register(b1)
{
    float3 _WorldSpaceCameraPos : packoffset(c4);
};
cbuffer UnityLighting : register(b2)
{
    float4 unity_LightColor[8] : packoffset(c7);
    float4 unity_LightPosition[8] : packoffset(c15);
    float4 unity_LightAtten[8] : packoffset(c23);
    float4 unity_SpotDirection[8] : packoffset(c31);
};
cbuffer UnityPerDraw : register(b3)
{
    column_major float4x4 unity_ObjectToWorld : packoffset(c0);
    column_major float4x4 unity_WorldToObject : packoffset(c4);
};
cbuffer UnityPerFrame : register(b4)
{
    float4 glstate_lightmodel_ambient : packoffset(c0);
    column_major float4x4 unity_MatrixV : packoffset(c9);
    column_major float4x4 unity_MatrixVP : packoffset(c17);
};
cbuffer UnityFog : register(b5)
{
    float4 unity_FogColor : packoffset(c0);
};
cbuffer StandardProperties : register(b6)
{
    float4 _MainTex_ST : packoffset(c0);
    float4 _Color : packoffset(c1);
    float _TextureWarping : packoffset(c2.x);
    float _HeightFog : packoffset(c2.w);
    float unity_FogStart : packoffset(c3.x);
    float unity_FogEnd : packoffset(c3.y);
};

Texture2D<float4> _MainTex : register(t0);
// Original texture settings are nearest filtering and repeat addressing.
// Unity recognizes this inline sampler name on Windows and Metal.
SamplerState sampler_point_repeat : register(s0);

struct CageVertex
{
    float4 position : POSITION;
    float4 color : COLOR0;
    float2 uv : TEXCOORD0;
    float3 normal : NORMAL;
};
struct CageVaryings
{
    float4 position : SV_POSITION;
    float4 litColor : COLOR0;
    float4 fog : COLOR2;
    float4 warpedUV : TEXCOORD0;
    float4 worldAndWarp : TEXCOORD1;
    float3 worldNormal : TEXCOORD2;
};

float4 matrixColumn(float4x4 basis, uint index)
{
    return float4(basis[0][index], basis[1][index], basis[2][index], basis[3][index]);
}

// Keep the original multiply/add ordering and fused operations. A generic mul()
// can produce slightly different coordinates at nearest-texture boundaries.
float4 transformPoint(float4x4 basis, float3 coordinates)
{
    float4 result = matrixColumn(basis, 1) * coordinates.y;
    result = mad(matrixColumn(basis, 0), coordinates.x, result);
    result = mad(matrixColumn(basis, 2), coordinates.z, result);
    return result + matrixColumn(basis, 3);
}

CageVaryings vert(CageVertex input)
{
    CageVaryings output;
    float3 world = matrixColumn(unity_ObjectToWorld, 1).xyz * input.position.y;
    world = mad(matrixColumn(unity_ObjectToWorld, 0).xyz, input.position.x, world);
    world = mad(matrixColumn(unity_ObjectToWorld, 2).xyz, input.position.z, world);
    world = mad(matrixColumn(unity_ObjectToWorld, 3).xyz, input.position.w, world);
    output.position = transformPoint(unity_MatrixVP, world);
    float warp = mad(_TextureWarping, max(output.position.w, 0.02) - 0.5, 0.5);
    float3 view = transformPoint(unity_MatrixV, world).xyz;
    float3 normal = normalize(float3(dot(input.normal, matrixColumn(unity_WorldToObject, 0).xyz),
                                   dot(input.normal, matrixColumn(unity_WorldToObject, 1).xyz),
                                   dot(input.normal, matrixColumn(unity_WorldToObject, 2).xyz)));
    float3 viewNormal = matrixColumn(unity_MatrixV, 1).xyz * normal.y;
    viewNormal = mad(matrixColumn(unity_MatrixV, 0).xyz, normal.x, viewNormal);
    viewNormal = mad(matrixColumn(unity_MatrixV, 2).xyz, normal.z, viewNormal);
    float3 lighting = glstate_lightmodel_ambient.rgb * 2;
    [unroll] for (int i = 0; i < 8; i++)
    {
        float3 toLight = mad(-view, unity_LightPosition[i].w, unity_LightPosition[i].xyz);
        float distanceSquared = max(dot(toLight, toLight), 1e-6);
        float3 direction = toLight / sqrt(distanceSquared);
        float attenuation = rcp(mad(distanceSquared, unity_LightAtten[i].z, 1));
        float spot = saturate((max(dot(direction, unity_SpotDirection[i].xyz), 0) - unity_LightAtten[i].x) * unity_LightAtten[i].y);
        float diffuse = max(dot(viewNormal, direction), 0);
        lighting = mad(unity_LightColor[i].rgb, attenuation * spot * diffuse, lighting);
    }
    float4 tint = _VertexColors == 1 ? input.color * _Color : _Color;
    output.litColor = float4(lighting * tint.rgb, tint.a);
    float distanceToCamera = length(_WorldSpaceCameraPos - world);
    float fogDistance = mad(_HeightFog, world.y - _WorldSpaceCameraPos.y - distanceToCamera, distanceToCamera);
    output.fog = float4(unity_FogColor.rgb, saturate((unity_FogEnd - fogDistance) / (unity_FogEnd - unity_FogStart)));
    output.warpedUV = float4(mad(input.uv, _MainTex_ST.xy, _MainTex_ST.zw) * warp, 0, 0);
    output.worldAndWarp = float4(world, warp);
    output.worldNormal = normal;
    return output;
}

struct CageTargets
{
    float4 color : SV_Target0;
    float2 outline : SV_Target1;
};
CageTargets frag(CageVaryings input)
{
    CageTargets output;
    float4 texel = _MainTex.SampleLevel(sampler_point_repeat, input.warpedUV.xy / input.worldAndWarp.ww, 0);
    float coverage = saturate(texel.a * input.litColor.a);
    float3 shaded = mad(texel.rgb, input.litColor.rgb, -input.fog.rgb);
    output.color = float4(mad(input.fog.www, shaded, input.fog.rgb), coverage * _Opacity);
    // Preserve the original ceil threshold; do not replace it with alpha clip.
    output.outline = float2(0, ceil(mad(coverage, _Opacity, -0.0001)) * _ForceOutlineBehind);
    return output;
}
