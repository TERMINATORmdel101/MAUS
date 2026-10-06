// Benchmark MAUS : déclarations communes à tous les shaders (Direct3D 11 et 12, modèle 5.0).
// Tout le code HLSL du benchmark est original (écrit pour MAUS, licence GPL-3.0) ; les formules classiques
// (bruit de gradient, GGX, ACES approché) sont citées là où elles sont utilisées.
#ifndef MAUS_COMMON_HLSLI
#define MAUS_COMMON_HLSLI

// Échantillonneurs fixes, identiques dans les deux moteurs (D3D11CommandList.CreateSamplers, D3D12RootSignature).
SamplerState LinearWrap : register(s0);
SamplerState LinearClamp : register(s1);
SamplerState PointClamp : register(s2);
SamplerState AnisoWrap : register(s3);
SamplerComparisonState ShadowCompare : register(s4);
SamplerState PointWrap : register(s5);

// Constantes de l'image (FrameConstants en C#, même ordre, matrices en lignes : mul(vecteur, matrice)).
cbuffer FrameConstants : register(b0)
{
    float4x4 View;
    float4x4 Proj;
    float4x4 ViewProj;          // avec le décalage sous-pixel de l'anticrénelage temporel
    float4x4 InvViewProj;
    float4x4 ViewProjNoJitter;
    float4x4 PrevViewProjNoJitter;
    float4x4 InvView;
    float3 CameraPos;     float Time;
    float2 Resolution;    float2 InvResolution;
    float2 Jitter;        float FrameIndex;    float DeltaTime;
    float3 SunDir;        float Exposure;
    float3 SunColor;      float SceneTime;
    float4 Params0;
    float4 Params1;
    float4 Params2;
    float4 Params3;
};

static const float PI = 3.14159265358979;
static const float TAU = 6.28318530717959;

struct FullscreenOut
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

// Un triangle qui couvre tout l'écran, sans tampon de sommets.
FullscreenOut FullscreenVS(uint id : SV_VertexID)
{
    FullscreenOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.Position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.UV = uv;
    return o;
}

// Rayon qui passe par un pixel (coordonnées de texture 0-1), décalage sous-pixel compris.
float3 RayDirection(float2 uv)
{
    float2 ndc = uv * float2(2.0, -2.0) + float2(-1.0, 1.0);
    float4 farPoint = mul(float4(ndc, 1.0, 1.0), InvViewProj);
    return normalize(farPoint.xyz / farPoint.w - CameraPos);
}

// Mouvement à l'écran d'un point du monde entre l'image précédente et celle-ci (en coordonnées de texture).
float2 ScreenVelocity(float3 worldPos)
{
    float4 current = mul(float4(worldPos, 1.0), ViewProjNoJitter);
    float4 previous = mul(float4(worldPos, 1.0), PrevViewProjNoJitter);
    float2 a = current.xy / current.w;
    float2 b = previous.xy / previous.w;
    return (a - b) * float2(0.5, -0.5);
}

// Mouvement d'une direction à l'infini (ciel, étoiles).
float2 DirectionVelocity(float3 direction)
{
    float4 current = mul(float4(direction, 0.0), ViewProjNoJitter);
    float4 previous = mul(float4(direction, 0.0), PrevViewProjNoJitter);
    float2 a = current.xy / max(current.w, 1e-4);
    float2 b = previous.xy / max(previous.w, 1e-4);
    return (a - b) * float2(0.5, -0.5);
}

// ---------------------------------------------------------------------------------------------------------------
// Hasards et bruits (écrits pour MAUS).
// ---------------------------------------------------------------------------------------------------------------

uint HashU(uint x)
{
    // Mélange d'entiers de type « PCG » (O'Neill 2014), une seule étape.
    x = x * 747796405u + 2891336453u;
    uint w = ((x >> ((x >> 28u) + 4u)) ^ x) * 277803737u;
    return (w >> 22u) ^ w;
}

float Hash11(float p)
{
    return HashU(asuint(p) ^ 0x9E3779B9u) * (1.0 / 4294967296.0);
}

float Hash21(float2 p)
{
    uint2 q = asuint(int2(floor(p)));
    return HashU(q.x * 1597334677u ^ q.y * 3812015801u ^ HashU(q.y)) * (1.0 / 4294967296.0);
}

float Hash31(float3 p)
{
    uint3 q = asuint(int3(floor(p)));
    return HashU(q.x * 1597334677u ^ HashU(q.y * 3812015801u ^ HashU(q.z * 2798796415u))) * (1.0 / 4294967296.0);
}

float3 Hash33(float3 p)
{
    uint3 q = asuint(int3(floor(p)));
    uint h = HashU(q.x * 1597334677u ^ HashU(q.y * 3812015801u ^ HashU(q.z * 2798796415u)));
    return float3(h, HashU(h), HashU(h ^ 0x68E31DA4u)) * (1.0 / 4294967296.0);
}

// Bruit de pixel pour casser les dégradés (tramage), différent à chaque image.
float InterleavedNoise(float2 pixel)
{
    float2 p = pixel + FrameIndex * float2(5.588238, 3.23443);
    return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
}

// Bruit de valeur 3D à interpolation quintique.
float ValueNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = Hash31(i);
    float b = Hash31(i + float3(1, 0, 0));
    float c = Hash31(i + float3(0, 1, 0));
    float d = Hash31(i + float3(1, 1, 0));
    float e = Hash31(i + float3(0, 0, 1));
    float g = Hash31(i + float3(1, 0, 1));
    float h = Hash31(i + float3(0, 1, 1));
    float k = Hash31(i + float3(1, 1, 1));
    return lerp(lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y), lerp(lerp(e, g, u.x), lerp(h, k, u.x), u.y), u.z);
}

// Bruit de gradient 3D (principe de Perlin 1985/2002 : gradients pseudo-aléatoires aux sommets, interpolation quintique).
float3 Gradient(float3 cell)
{
    float3 h = Hash33(cell) * 2.0 - 1.0;
    return normalize(h + 1e-5);
}

float GradientNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float n000 = dot(Gradient(i), f);
    float n100 = dot(Gradient(i + float3(1, 0, 0)), f - float3(1, 0, 0));
    float n010 = dot(Gradient(i + float3(0, 1, 0)), f - float3(0, 1, 0));
    float n110 = dot(Gradient(i + float3(1, 1, 0)), f - float3(1, 1, 0));
    float n001 = dot(Gradient(i + float3(0, 0, 1)), f - float3(0, 0, 1));
    float n101 = dot(Gradient(i + float3(1, 0, 1)), f - float3(1, 0, 1));
    float n011 = dot(Gradient(i + float3(0, 1, 1)), f - float3(0, 1, 1));
    float n111 = dot(Gradient(i + float3(1, 1, 1)), f - float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y), lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}

// Somme d'octaves (mouvement brownien fractionnaire), avec rotation entre octaves pour éviter les alignements.
static const float3x3 OctaveRotation = float3x3(0.00, 0.80, 0.60, -0.80, 0.36, -0.48, -0.60, -0.48, 0.64);

float Fbm(float3 p, int octaves)
{
    float sum = 0.0;
    float amplitude = 0.5;
    [loop]
    for (int i = 0; i < octaves; i++)
    {
        sum += amplitude * GradientNoise(p);
        p = mul(p, OctaveRotation) * 2.03;
        amplitude *= 0.5;
    }
    return sum;
}

// ---------------------------------------------------------------------------------------------------------------
// Lumière physique : GGX (Walter et al. 2007), Smith-Schlick, Fresnel de Schlick.
// ---------------------------------------------------------------------------------------------------------------

float DistributionGGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d + 1e-7);
}

float VisibilitySmith(float NdotV, float NdotL, float roughness)
{
    float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float gv = NdotV / (NdotV * (1.0 - k) + k);
    float gl = NdotL / (NdotL * (1.0 - k) + k);
    return gv * gl / max(4.0 * NdotV * NdotL, 1e-4);
}

float3 FresnelSchlick(float cosTheta, float3 f0)
{
    return f0 + (1.0 - f0) * pow(saturate(1.0 - cosTheta), 5.0);
}

// Éclairage direct d'une lumière (direction L, couleur déjà multipliée par l'intensité).
float3 ShadeDirect(float3 albedo, float metallic, float roughness, float3 n, float3 v, float3 l, float3 lightColor)
{
    float3 h = normalize(v + l);
    float NdotL = saturate(dot(n, l));
    float NdotV = max(dot(n, v), 1e-3);
    float NdotH = saturate(dot(n, h));
    float VdotH = saturate(dot(v, h));
    float3 f0 = lerp(0.04.xxx, albedo, metallic);
    float3 F = FresnelSchlick(VdotH, f0);
    float3 specular = DistributionGGX(NdotH, roughness) * VisibilitySmith(NdotV, NdotL, roughness) * F;
    float3 diffuse = (1.0 - F) * (1.0 - metallic) * albedo / PI;
    return (diffuse + specular) * lightColor * NdotL;
}

float Luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

#endif
