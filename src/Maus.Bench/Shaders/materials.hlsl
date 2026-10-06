// Benchmark MAUS, test « Matériaux » : un cabinet de curiosités dans une salle de bal. Objets scannés de Poly Haven
// (licence CC0 : buste en marbre, statue de cheval, vases, service à thé, lanterne, appareil photo, réveil, éléphant
// sculpté, katana) sur une table en bois de rose vernie qui les reflète, éclairés par un vrai ciel HDR (salle de bal,
// CC0) : éclairage par l'environnement (harmoniques sphériques pour le diffus, environnement préfiltré GGX pour les
// reflets), lumière d'une fenêtre avec ombres, flamme de bougie dans la lanterne, rayons de lumière dans la poussière.
// Chaque pixel lit plusieurs textures haute définition avec filtrage anisotrope : c'est le filtrage et le débit des
// textures qui saturent.
#include "common.hlsli"

cbuffer SceneConstants : register(b1)
{
    float4x4 World;
    float4x4 ShadowViewProj;
    float4 BaseColor;           // facteur de couleur (rgb), opacité (a)
    float4 Surface;             // métal (x), rugosité (y), occlusion dans le rouge de l'image « arm » (z), genre (w)
    float4 KeyLight;            // direction vers la fenêtre (xyz), intensité (w)
    float4 KeyColor;            // couleur (rgb), taille d'un texel d'ombre (w)
    float4 Candle;              // position de la flamme (xyz), intensité (w)
    float4 Mirror;              // hauteur de la table (x), passe du reflet (y), taille de l'image du reflet (zw)
    float4 Extra;               // précalculs : face du cube (x), rugosité (y) ; mise au point en mètres (z), ouverture (w)
    float4 Atmosphere;          // densité de la poussière éclairée (x)
    float4 SH[9];               // éclairage diffus du ciel (harmoniques sphériques déjà convoluées)
};

Texture2D<float4> BaseMap : register(t0);
Texture2D<float4> NormalMap : register(t1);
Texture2D<float4> SurfaceMap : register(t2);
Texture2D<float> ShadowMap : register(t3);
TextureCube<float4> Environment : register(t4);
Texture2D<float2> BrdfLut : register(t5);
Texture2D<float4> Reflection : register(t6);
Texture2D<float4> Sky : register(t7);
Texture2D<float> SceneDepth : register(t8);
Texture2D<float4> HeightMap : register(t9);
Texture2D<float4> FloorReflection : register(t10);

static const float KindObject = 0.0;
static const float KindGlass = 1.0;
static const float KindTable = 2.0;
static const float KindFloor = 3.0;
static const float KindPlinth = 4.0;
static const float EnvironmentMips = 7.0;

// ---------------------------------------------------------------------------------------------------------------
// Ciel : image équirectangulaire (mêmes conventions que ImageAssets.IrradianceSh : y vers le haut).
// ---------------------------------------------------------------------------------------------------------------

float2 EquirectUv(float3 d)
{
    float phi = atan2(d.z, d.x);
    return float2(frac(phi / TAU + 1.0), acos(clamp(d.y, -1.0, 1.0)) / PI);
}

float3 SkyColor(float3 d, float blur)
{
    return Sky.SampleLevel(LinearWrap, EquirectUv(d), blur).rgb;
}

float3 Irradiance(float3 n)
{
    float3 c = SH[0].rgb * 0.282095
        + SH[1].rgb * 0.488603 * n.y + SH[2].rgb * 0.488603 * n.z + SH[3].rgb * 0.488603 * n.x
        + SH[4].rgb * 1.092548 * n.x * n.y + SH[5].rgb * 1.092548 * n.y * n.z
        + SH[6].rgb * 0.315392 * (3.0 * n.z * n.z - 1.0) + SH[7].rgb * 1.092548 * n.x * n.z
        + SH[8].rgb * 0.546274 * (n.x * n.x - n.y * n.y);
    return max(c, 0.0);
}

// Reflets de l'environnement, découpage en deux intégrales (B. Karis, « Real Shading in Unreal Engine 4 », 2013).
float3 EnvironmentSpecular(float3 n, float3 v, float3 f0, float roughness)
{
    float3 r = reflect(-v, n);
    float3 prefiltered = Environment.SampleLevel(LinearClamp, r, roughness * (EnvironmentMips - 1.0)).rgb;
    float2 brdf = BrdfLut.SampleLevel(LinearClamp, float2(saturate(dot(n, v)), roughness), 0.0);
    return prefiltered * (f0 * brdf.x + brdf.y);
}

static const float GoldenAngle = 2.39996323;

// Ombre de la fenêtre. Mode standard : ombres douces dont la pénombre s'élargit avec la distance à l'objet qui fait
// ombre (R. Fernando, « Percentage-Closer Soft Shadows », NVIDIA, 2005) : 16 lectures pour trouver les obstacles, 48 pour
// filtrer (8 et 16 en mode léger).
float WindowShadow(float3 world, float3 normal)
{
    float4 shadow = mul(float4(world + normal * 0.004, 1.0), ShadowViewProj);
    float3 uvz = shadow.xyz / shadow.w;
    float2 uv = uvz.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0))
    {
        return 1.0;
    }

    float texel = KeyColor.w;
    const int search = Light ? 8 : 16;
    const int filter = Light ? 16 : 48;
    float rotation = InterleavedNoise(uv * 4096.0) * TAU;
    float blockers = 0.0;
    float count = 0.0;
    [loop]
    for (int i = 0; i < search; i++)
    {
        float r = sqrt((i + 0.5) / search) * 10.0 * texel;
        float a = i * GoldenAngle + rotation;
        float depth = ShadowMap.SampleLevel(PointClamp, uv + float2(cos(a), sin(a)) * r, 0.0);
        if (depth < uvz.z - 0.0004)
        {
            blockers += depth;
            count += 1.0;
        }
    }

    if (count < 0.5)
    {
        return 1.0;
    }

    // Pénombre en texels : distance à l'obstacle (profondeur de 11,5 m) × largeur apparente de la fenêtre (environ 2°).
    float penumbra = clamp((uvz.z - blockers / count) * 11.5 * 0.035 / 0.00166, 1.2, 22.0);
    float sum = 0.0;
    [loop]
    for (int j = 0; j < filter; j++)
    {
        float r = sqrt((j + 0.5) / filter) * penumbra * texel;
        float a = j * GoldenAngle + rotation;
        sum += ShadowMap.SampleCmpLevelZero(ShadowCompare, uv + float2(cos(a), sin(a)) * r, uvz.z - 0.0004);
    }

    return sum / filter;
}

// Flamme de la bougie : lumière ponctuelle chaude qui vacille.
float3 CandleLight(float3 world, float3 n, float3 v, float3 albedo, float metallic, float roughness)
{
    float3 toLight = Candle.xyz - world;
    float d2 = dot(toLight, toLight);
    float3 l = toLight * rsqrt(d2 + 1e-6);
    float flicker = 0.85 + 0.15 * sin(Time * 13.0) * sin(Time * 7.3 + 1.2);
    float3 color = float3(1.0, 0.55, 0.22) * Candle.w * flicker / (d2 + 0.004);
    return ShadeDirect(albedo, metallic, roughness, n, v, l, color);
}

// ---------------------------------------------------------------------------------------------------------------
// Objets, table, sol et socles.
// ---------------------------------------------------------------------------------------------------------------

struct ObjectVertex
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float2 Uv : TEXCOORD0;
};

struct ObjectPixel
{
    float4 Position : SV_Position;
    float3 World : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float4 Tangent : TEXCOORD2;
    float2 Uv : TEXCOORD3;
};

ObjectPixel ObjectVS(ObjectVertex v)
{
    ObjectPixel o;
    float4 world = mul(float4(v.Position, 1.0), World);
    o.Position = mul(world, ViewProj);
    o.World = world.xyz;
    o.Normal = mul(float4(v.Normal, 0.0), World).xyz;
    o.Tangent = float4(mul(float4(v.Tangent.xyz, 0.0), World).xyz, v.Tangent.w);
    o.Uv = v.Uv;
    return o;
}

float4 ShadowVS(ObjectVertex v) : SV_Position
{
    return mul(mul(float4(v.Position, 1.0), World), ShadowViewProj);
}

struct SceneOut
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
};

struct Material
{
    float3 Albedo;
    float3 Normal;
    float Metallic;
    float Roughness;
    float Occlusion;
};

// Carte de normales au format OpenGL (vert vers le haut de l'image, c'est-à-dire vers les v décroissants).
float3 NormalFromMap(float3 n, float4 tangent, float2 uv)
{
    float3 t = normalize(tangent.xyz - n * dot(n, tangent.xyz));
    float3 b = cross(n, t) * tangent.w;
    float3 m = NormalMap.Sample(AnisoWrap, uv).xyz * 2.0 - 1.0;
    return normalize(t * m.x - b * m.y + n * max(m.z, 0.05));
}

Material ObjectMaterial(ObjectPixel input, float3 n)
{
    Material m;
    float4 base = BaseMap.Sample(AnisoWrap, input.Uv) * BaseColor;
    float4 surface = SurfaceMap.Sample(AnisoWrap, input.Uv);
    m.Albedo = base.rgb;
    m.Normal = NormalFromMap(n, input.Tangent, input.Uv);
    m.Roughness = clamp(surface.g * Surface.y, 0.04, 1.0);
    m.Metallic = saturate(surface.b * Surface.x);
    m.Occlusion = Surface.z > 0.5 ? surface.r : 1.0;
    return m;
}

// Relief par occlusion de parallaxe : marche dans la carte de hauteur le long du regard, en espace tangent, avec le
// filtrage anisotrope d'origine (Z. Brawley et N. Tatarchuk, « Parallax Occlusion Mapping », ShaderX3, 2004). Repère
// d'un plan horizontal : u le long de x, v le long de z.
float2 Parallax(float2 uv, float3 view, float depth, out float2 dx, out float2 dy)
{
    dx = ddx(uv);
    dy = ddy(uv);
    float3 tangentView = normalize(float3(view.x, view.z, view.y));
    const int most = Light ? 40 : 96;
    int steps = (int)lerp((float)most, most * 0.25, saturate(tangentView.z));
    float layer = 1.0 / steps;
    float2 delta = tangentView.xy / max(tangentView.z, 0.12) * depth * layer;
    float2 current = uv;
    float level = 0.0;
    float height = 1.0 - HeightMap.SampleGrad(AnisoWrap, current, dx, dy).r;
    [loop]
    for (int i = 0; i < steps && level < height; i++)
    {
        current -= delta;
        height = 1.0 - HeightMap.SampleGrad(AnisoWrap, current, dx, dy).r;
        level += layer;
    }

    float2 previous = current + delta;
    float after = height - level;
    float before = (1.0 - HeightMap.SampleGrad(AnisoWrap, previous, dx, dy).r) - level + layer;
    float w = after / min(after - before, -1e-5);
    return lerp(current, previous, saturate(w));
}

// Bois de la table : plaqué de bois de rose (Poly Haven, CC0), projeté d'en haut, sous un vernis brillant.
Material TableMaterial(float3 world, float3 n, float3 v)
{
    Material m;
    float2 dx, dy;
    float2 uv = n.y > 0.9 ? Parallax(world.xz * 0.62 + 0.5, v, 0.006, dx, dy) : world.xz * 0.62 + 0.5;
    dx = ddx(world.xz * 0.62);
    dy = ddy(world.xz * 0.62);
    float4 base = BaseMap.SampleGrad(AnisoWrap, uv, dx, dy);
    float4 surface = SurfaceMap.SampleGrad(AnisoWrap, uv, dx, dy);
    float3 t = float3(1.0, 0.0, 0.0);
    float3 b = float3(0.0, 0.0, 1.0);
    float3 mapped = NormalMap.SampleGrad(AnisoWrap, uv, dx, dy).xyz * 2.0 - 1.0;
    // Sur le dessus seulement ; le chant garde sa normale géométrique.
    m.Normal = n.y > 0.9 ? normalize(t * mapped.x - b * mapped.y + n * max(mapped.z, 0.05)) : n;
    m.Albedo = base.rgb;
    m.Roughness = clamp(surface.g, 0.2, 1.0);
    m.Metallic = 0.0;
    m.Occlusion = surface.r;
    return m;
}

// Parquet en point de Hongrie (Poly Haven, CC0), avec le relief des lames par parallaxe.
Material FloorMaterial(float3 world, float3 v)
{
    Material m;
    float2 dx, dy;
    float2 uv = Parallax(world.xz * 0.55, v, 0.012, dx, dy);
    float4 base = BaseMap.SampleGrad(AnisoWrap, uv, dx, dy);
    float4 surface = SurfaceMap.SampleGrad(AnisoWrap, uv, dx, dy);
    float3 mapped = NormalMap.SampleGrad(AnisoWrap, uv, dx, dy).xyz * 2.0 - 1.0;
    m.Normal = normalize(float3(mapped.x, max(mapped.z, 0.05), -mapped.y));
    m.Albedo = base.rgb;
    m.Roughness = clamp(surface.g * 0.9, 0.15, 1.0);
    m.Metallic = 0.0;
    m.Occlusion = surface.r;
    return m;
}

// Socles de musée : pierre claire polie, veinée (calculée).
Material PlinthMaterial(float3 world, float3 n)
{
    Material m;
    float vein = Fbm(world * 2.2, Light ? 2 : 4);
    float marble = pow(saturate(1.0 - abs(sin(world.x * 9.0 + world.y * 5.0 + world.z * 7.0 + vein * 9.0))), 18.0);
    float clouds = Fbm(world * 6.0 + 3.0, Light ? 1 : 2);
    m.Albedo = lerp(float3(0.82, 0.8, 0.77), float3(0.36, 0.35, 0.36), marble * 0.8) * (0.96 + 0.06 * clouds);
    m.Normal = n;
    m.Roughness = 0.22 + 0.1 * vein;
    m.Metallic = 0.0;
    m.Occlusion = 1.0;
    return m;
}

float3 Shade(Material m, float3 world, float3 v, float shadow)
{
    float3 f0 = lerp(0.04.xxx, m.Albedo, m.Metallic);
    float3 diffuse = m.Albedo * (1.0 - m.Metallic) * Irradiance(m.Normal);
    float3 specular = EnvironmentSpecular(m.Normal, v, f0, m.Roughness);
    float3 color = (diffuse + specular) * m.Occlusion;
    color += ShadeDirect(m.Albedo, m.Metallic, m.Roughness, m.Normal, v, KeyLight.xyz, KeyColor.rgb * KeyLight.w * shadow);
    color += CandleLight(world, m.Normal, v, m.Albedo, m.Metallic, m.Roughness) * m.Occlusion;
    return color;
}

SceneOut ObjectPS(ObjectPixel input)
{
    SceneOut o;
    // Passe du reflet : rien sous le plateau de la table.
    if (Mirror.y > 0.5)
    {
        clip(input.World.y - Mirror.x + 0.002);
    }

    float3 n = normalize(input.Normal);
    float3 v = normalize(CameraPos - input.World);
    n = dot(n, v) < 0.0 ? -n : n;
    Material m;
    float kind = Surface.w;
    if (kind == KindTable)
    {
        m = TableMaterial(input.World, n, v);
    }
    else if (kind == KindFloor)
    {
        m = FloorMaterial(input.World, v);
    }
    else if (kind == KindPlinth)
    {
        m = PlinthMaterial(input.World, n);
    }
    else
    {
        m = ObjectMaterial(input, n);
    }

    float shadow = WindowShadow(input.World, n);
    float3 color = Shade(m, input.World, v, shadow);

    if (kind == KindTable && n.y > 0.9)
    {
        // Vernis : un second reflet, net, par-dessus le bois. Les objets posés se reflètent (image du reflet), la salle aussi.
        float fresnel = 0.04 + 0.96 * pow(1.0 - saturate(dot(n, v)), 5.0);
        float2 screen = input.Position.xy * InvResolution + m.Normal.xz * 0.012;
        float4 mirrored = Reflection.SampleLevel(LinearClamp, screen, 0.0);
        float3 room = Environment.SampleLevel(LinearClamp, reflect(-v, n), 0.6).rgb;
        float3 coat = lerp(room, mirrored.rgb, mirrored.a);
        color = color * (1.0 - fresnel) + coat * fresnel * 1.05;
    }

    if (kind == KindFloor && Mirror.y < 0.5)
    {
        // Vernis du parquet : reflet de la table, des socles et des objets, flouté selon la rugosité (cinq lectures).
        float fresnel = 0.04 + 0.96 * pow(1.0 - saturate(dot(m.Normal, v)), 5.0);
        float2 screen = input.Position.xy * InvResolution + m.Normal.xz * 0.02;
        float spread = 0.004 + m.Roughness * 0.012;
        float4 mirrored = FloorReflection.SampleLevel(LinearClamp, screen, 0.0) * 0.4
            + FloorReflection.SampleLevel(LinearClamp, screen + float2(spread, 0.0), 0.0) * 0.15
            + FloorReflection.SampleLevel(LinearClamp, screen - float2(spread, 0.0), 0.0) * 0.15
            + FloorReflection.SampleLevel(LinearClamp, screen + float2(0.0, spread), 0.0) * 0.15
            + FloorReflection.SampleLevel(LinearClamp, screen - float2(0.0, spread), 0.0) * 0.15;
        float3 room = Environment.SampleLevel(LinearClamp, reflect(-v, m.Normal), 2.0).rgb;
        float gloss = saturate(1.0 - m.Roughness * 1.4);
        color = color * (1.0 - fresnel * gloss) + lerp(room, mirrored.rgb, mirrored.a) * fresnel * gloss;
    }

    if (kind == KindFloor)
    {
        // Le parquet se fond au loin dans la salle du ciel HDR.
        float distance = length(input.World.xz);
        color = lerp(color, SkyColor(normalize(input.World - CameraPos), 2.0), smoothstep(5.0, 9.0, distance));
    }

    o.Color = float4(color, 1.0);
    o.Velocity = ScreenVelocity(input.World);
    return o;
}

// Verre (lanterne, cadran du réveil, objectif) : fin et clair ; reflet de la salle selon l'angle (Fresnel).
float4 GlassPS(ObjectPixel input) : SV_Target
{
    if (Mirror.y > 0.5)
    {
        clip(input.World.y - Mirror.x + 0.002);
    }

    float3 n = normalize(input.Normal);
    float3 v = normalize(CameraPos - input.World);
    n = dot(n, v) < 0.0 ? -n : n;
    float fresnel = 0.04 + 0.96 * pow(1.0 - saturate(dot(n, v)), 5.0);
    float3 reflection = Environment.SampleLevel(LinearClamp, reflect(-v, n), 0.15).rgb;
    float3 glint = ShadeDirect(1.0.xxx, 0.0, 0.05, n, v, KeyLight.xyz, KeyColor.rgb * KeyLight.w);
    float alpha = saturate(fresnel + 0.04);
    return float4(reflection * fresnel + glint * 0.04, alpha);
}

// ---------------------------------------------------------------------------------------------------------------
// Fond : la salle de bal du ciel HDR, un peu floue (profondeur de champ d'un objectif proche).
// ---------------------------------------------------------------------------------------------------------------

SceneOut SkyPS(FullscreenOut input)
{
    SceneOut o;
    float3 d = RayDirection(input.UV);
    o.Color = float4(SkyColor(d, Light ? 1.0 : 1.3), 1.0);
    o.Velocity = DirectionVelocity(d);
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// Rayons de lumière dans la poussière : marche le long du rayon de la caméra, ombre de la fenêtre à chaque pas.
// ---------------------------------------------------------------------------------------------------------------

float4 DustPS(FullscreenOut input) : SV_Target
{
    float depth = SceneDepth.SampleLevel(PointClamp, input.UV, 0.0);
    float3 d = RayDirection(input.UV);
    float far = 12.0;
    if (depth < 1.0)
    {
        float4 world = mul(float4(input.UV * float2(2.0, -2.0) + float2(-1.0, 1.0), depth, 1.0), InvViewProj);
        far = min(length(world.xyz / world.w - CameraPos), far);
    }

    const int steps = Light ? 64 : 192;
    float step = far / steps;
    float offset = InterleavedNoise(input.Position.xy + FrameIndex * 5.588238);
    float phase = 0.3 + 0.7 * pow(saturate(dot(d, KeyLight.xyz)), 8.0);
    float3 light = 0.0;
    [loop]
    for (int i = 0; i < steps; i++)
    {
        float3 p = CameraPos + d * ((i + offset) * step);
        float4 shadow = mul(float4(p, 1.0), ShadowViewProj);
        float3 uvz = shadow.xyz / shadow.w;
        float2 uv = uvz.xy * float2(0.5, -0.5) + 0.5;
        float lit = 1.0;
        if (all(uv > 0.0) && all(uv < 1.0))
        {
            lit = 0.0;
            const int taps = Light ? 1 : 3;
            [unroll]
            for (int ty = 0; ty < taps; ty++)
            {
                [unroll]
                for (int tx = 0; tx < taps; tx++)
                {
                    lit += ShadowMap.SampleCmpLevelZero(ShadowCompare, uv + (float2(tx, ty) - (taps - 1) * 0.5) * KeyColor.w * 2.0, uvz.z - 0.001);
                }
            }

            lit /= taps * taps;
        }

        float dust = 0.6 + 0.4 * ValueNoise(p * 3.0 + float3(0.0, Time * 0.04, Time * 0.02));
        light += lit * dust;
    }

    float3 scattered = KeyColor.rgb * KeyLight.w * Atmosphere.x * phase * light * step;
    return float4(scattered, 0.0);
}

// Position dans le monde d'un pixel de l'image, d'après sa profondeur.
float3 WorldAt(float2 uv)
{
    float depth = SceneDepth.SampleLevel(PointClamp, uv, 0.0);
    float4 world = mul(float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), depth, 1.0), InvViewProj);
    return world.xyz / world.w;
}

// Occlusion ambiante à l'écran : 32 points autour du pixel, à 12 cm au plus ; plus il y a de surfaces qui dépassent
// au-dessus du plan du pixel, plus le coin est sombre. Le résultat multiplie l'image.
float4 OcclusionPS(FullscreenOut input) : SV_Target
{
    float depth = SceneDepth.SampleLevel(PointClamp, input.UV, 0.0);
    if (depth >= 1.0)
    {
        return 1.0;
    }

    float3 p = WorldAt(input.UV);
    float3 n = normalize(cross(ddy(p), ddx(p)));
    n = dot(n, CameraPos - p) < 0.0 ? -n : n;
    float distance = length(CameraPos - p);
    float radius = 0.12;
    float pixels = radius / (distance * 0.6) * Resolution.y * 0.5;
    float rotation = InterleavedNoise(input.Position.xy + FrameIndex * 3.7) * TAU;
    const int samples = Light ? 16 : 32;
    float occlusion = 0.0;
    [loop]
    for (int i = 0; i < samples; i++)
    {
        float r = sqrt((i + 0.5) / samples) * pixels;
        float a = i * GoldenAngle + rotation;
        float3 q = WorldAt(input.UV + float2(cos(a), sin(a)) * r * InvResolution);
        float3 d = q - p;
        float len = length(d);
        occlusion += saturate(dot(n, d) / (len + 1e-4) - 0.1) * saturate(1.0 - len / (radius * 2.0));
    }

    float ao = 1.0 - 0.75 * occlusion / samples;
    return float4(ao.xxx, 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
// Flamme de la bougie (dessinée après l'anticrénelage) et profondeur de champ (mise au point sur l'objet regardé).
// ---------------------------------------------------------------------------------------------------------------

struct FlamePixel
{
    float4 Position : SV_Position;
    float2 Corner : TEXCOORD0;
};

FlamePixel FlameVS(uint vertex : SV_VertexID)
{
    static const float2 corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };
    FlamePixel o;
    float4 view = mul(float4(Candle.xyz, 1.0), View);
    float2 c = corners[vertex];
    view.xy += c * float2(0.012, 0.024);
    o.Position = mul(view, Proj);
    o.Corner = c;
    return o;
}

float4 FlamePS(FlamePixel input) : SV_Target
{
    // Goutte : large en bas, effilée en haut, qui ondule.
    float2 p = input.Corner;
    p.x += sin(Time * 9.0 + p.y * 3.0) * 0.08 * (p.y + 1.0);
    float width = lerp(0.85, 0.15, saturate(p.y * 0.5 + 0.5));
    float shape = saturate(1.0 - length(float2(p.x / width, (p.y + 0.35) * 0.75)));
    float core = pow(shape, 1.5);
    float3 color = lerp(float3(1.0, 0.35, 0.05), float3(1.0, 0.9, 0.6), core) * core * 9.0;
    return float4(color, 0.0);
}

// Profondeur de champ : rayon du cercle de flou d'après la distance (objectif mince), disque de 48 échantillons.
float4 FocusPS(FullscreenOut input) : SV_Target
{
    float depth = SceneDepth.SampleLevel(PointClamp, input.UV, 0.0);
    float4 world = mul(float4(input.UV * float2(2.0, -2.0) + float2(-1.0, 1.0), depth, 1.0), InvViewProj);
    float distance = depth >= 1.0 ? 50.0 : length(world.xyz / world.w - CameraPos);
    float coc = saturate(abs(1.0 / Extra.z - 1.0 / distance) * Extra.w);
    float3 sharp = BaseMap.SampleLevel(PointClamp, input.UV, 0.0).rgb;
    if (coc < 0.02)
    {
        return float4(sharp, 1.0);
    }

    const int samples = Light ? 48 : 128;
    float radius = coc * 14.0;
    float3 sum = sharp;
    float total = 1.0;
    [loop]
    for (int i = 0; i < samples; i++)
    {
        // Spirale de l'angle d'or : échantillons répartis uniformément dans le disque.
        float r = sqrt((i + 0.5) / samples) * radius;
        float a = i * 2.39996323;
        float2 uv = input.UV + float2(cos(a), sin(a)) * r * InvResolution;
        sum += BaseMap.SampleLevel(LinearClamp, uv, 0.0).rgb;
        total += 1.0;
    }

    return float4(sum / total, 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
// Précalculs au chargement : environnement préfiltré (GGX, échantillonnage préférentiel) et table de la BRDF.
// ---------------------------------------------------------------------------------------------------------------

float2 Hammersley(uint i, uint count)
{
    uint bits = reversebits(i);
    return float2((float)i / count, bits * 2.3283064365386963e-10);
}

float3 ImportanceGgx(float2 xi, float3 n, float roughness)
{
    float a = roughness * roughness;
    float phi = TAU * xi.x;
    float cosTheta = sqrt((1.0 - xi.y) / (1.0 + (a * a - 1.0) * xi.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    float3 h = float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
    float3 up = abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 tx = normalize(cross(up, n));
    float3 ty = cross(n, tx);
    return normalize(tx * h.x + ty * h.y + n * h.z);
}

float3 CubeDirection(uint face, float2 uv)
{
    float2 p = uv * 2.0 - 1.0;
    p.y = -p.y;
    switch (face)
    {
        case 0: return normalize(float3(1.0, p.y, -p.x));
        case 1: return normalize(float3(-1.0, p.y, p.x));
        case 2: return normalize(float3(p.x, 1.0, -p.y));
        case 3: return normalize(float3(p.x, -1.0, p.y));
        case 4: return normalize(float3(p.x, p.y, 1.0));
        default: return normalize(float3(-p.x, p.y, -1.0));
    }
}

float4 PrefilterPS(FullscreenOut input) : SV_Target
{
    float3 n = CubeDirection((uint)Extra.x, input.UV);
    float roughness = Extra.y;
    if (roughness < 0.01)
    {
        return float4(SkyColor(n, 0.0), 1.0);
    }

    // Échantillonnage préférentiel filtré : chaque échantillon lit un niveau de détail du ciel en rapport avec sa
    // probabilité (M. Colbert et J. Křivánek, « GPU-Based Importance Sampling », GPU Gems 3, 2007).
    const uint count = 192;
    float3 sum = 0.0;
    float weight = 0.0;
    float texel = 4.0 * PI / (2048.0 * 1024.0);
    for (uint i = 0; i < count; i++)
    {
        float3 h = ImportanceGgx(Hammersley(i, count), n, roughness);
        float3 l = normalize(2.0 * dot(n, h) * h - n);
        float nl = dot(n, l);
        if (nl > 0.0)
        {
            float nh = saturate(dot(n, h));
            float a2 = pow(roughness, 4.0);
            float dd = nh * nh * (a2 - 1.0) + 1.0;
            float pdf = a2 / (PI * dd * dd) * 0.25 + 1e-5;
            float solid = 1.0 / (count * pdf);
            float lod = max(0.5 * log2(solid / texel) + 1.0, 0.0);
            sum += SkyColor(l, lod) * nl;
            weight += nl;
        }
    }

    return float4(sum / max(weight, 1e-4), 1.0);
}

float2 BrdfLutPS(FullscreenOut input) : SV_Target
{
    float nv = max(input.UV.x, 1e-3);
    float roughness = max(input.UV.y, 0.02);
    float3 v = float3(sqrt(1.0 - nv * nv), 0.0, nv);
    float a = 0.0;
    float b = 0.0;
    const uint count = 512;
    for (uint i = 0; i < count; i++)
    {
        float3 h = ImportanceGgx(Hammersley(i, count), float3(0, 0, 1), roughness);
        float3 l = normalize(2.0 * dot(v, h) * h - v);
        float nl = saturate(l.z);
        float nh = saturate(h.z);
        float vh = saturate(dot(v, h));
        if (nl > 0.0)
        {
            float k = roughness * roughness / 2.0;
            float g = (nv / (nv * (1.0 - k) + k)) * (nl / (nl * (1.0 - k) + k));
            float gv = g * vh / (nh * nv);
            float fc = pow(1.0 - vh, 5.0);
            a += (1.0 - fc) * gv;
            b += fc * gv;
        }
    }

    return float2(a, b) / count;
}
