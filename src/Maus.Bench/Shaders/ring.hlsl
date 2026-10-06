// Benchmark MAUS, test « Géométrie » : vol dans l'anneau d'une planète géante. Des centaines de milliers de rochers
// (des milliers de triangles chacun près de la caméra) sont dessinés deux fois par image : vus de la caméra, puis vus
// du soleil pour les ombres. Plus de 200 millions de triangles par image : c'est la rastérisation qui sature.
#include "common.hlsli"

struct Rock
{
    float4 PositionScale;   // centre (xyz), taille (w)
    float4 AxisSpin;        // axe de rotation (xyz), vitesse de rotation (w)
    float4 Color;           // albédo (rgb), glace (a : 0 roche, 1 glace)
};

cbuffer RingConstants : register(b1)
{
    float4x4 ShadowViewProj;
    float4 RingParams;      // x = premier rocher de ce dessin, y = temps, z = rayon de la planète, w = taille d'un texel d'ombre
    float4 PlanetCenter;    // xyz, w = inclinaison des bandes
};

StructuredBuffer<Rock> Rocks : register(t0);
Texture2D<float> ShadowMap : register(t1);

struct RockVertex
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
};

struct RockPixel
{
    float4 Position : SV_Position;
    float3 World : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float3 Local : TEXCOORD2;
    nointerpolation float4 Color : COLOR0;
};

// Rotation de Rodrigues autour d'un axe unitaire.
float3 Rotate(float3 v, float3 axis, float angle)
{
    float c = cos(angle), s = sin(angle);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}

float3 RockWorld(RockVertex v, Rock rock, out float3 normal)
{
    float angle = rock.AxisSpin.w * RingParams.y;
    normal = Rotate(v.Normal, rock.AxisSpin.xyz, angle);
    return Rotate(v.Position, rock.AxisSpin.xyz, angle) * rock.PositionScale.w + rock.PositionScale.xyz;
}

RockPixel RockVS(RockVertex v, uint instance : SV_InstanceID)
{
    RockPixel o;
    Rock rock = Rocks[instance + (uint)RingParams.x];
    float3 normal;
    float3 world = RockWorld(v, rock, normal);
    o.Position = mul(float4(world, 1.0), ViewProj);
    o.World = world;
    o.Normal = normal;
    o.Local = v.Position * 3.0 + rock.PositionScale.xyz;
    o.Color = rock.Color;
    return o;
}

float4 ShadowVS(RockVertex v, uint instance : SV_InstanceID) : SV_Position
{
    Rock rock = Rocks[instance + (uint)RingParams.x];
    float3 normal;
    float3 world = RockWorld(v, rock, normal);
    return mul(float4(world, 1.0), ShadowViewProj);
}

float SunShadow(float3 world, float3 normal)
{
    float4 shadow = mul(float4(world + normal * 0.05, 1.0), ShadowViewProj);
    float3 uvz = shadow.xyz / shadow.w;
    float2 uv = uvz.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0))
    {
        return 1.0;
    }

    // Filtre 4×4 sur la carte d'ombre (comparaison matérielle, bords doux).
    float sum = 0.0;
    [unroll]
    for (int y = -1; y <= 2; y++)
    {
        [unroll]
        for (int x = -1; x <= 2; x++)
        {
            sum += ShadowMap.SampleCmpLevelZero(ShadowCompare, uv + (float2(x, y) - 0.5) * RingParams.w, uvz.z - 0.0015);
        }
    }
    return sum / 16.0;
}

struct SceneOut
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
};

SceneOut RockPS(RockPixel input)
{
    SceneOut o;
    float3 n = normalize(input.Normal);
    float3 v = normalize(CameraPos - input.World);
    float ice = input.Color.a;

    // Détail de surface : bruit en trois dimensions sur la position du rocher (cratères, veines claires).
    float detail = Fbm(input.Local * 2.0, 4);
    float3 albedo = input.Color.rgb * (0.75 + 0.5 * detail);
    albedo = lerp(albedo, float3(0.85, 0.9, 0.95), ice * saturate(detail * 2.0 + 0.5));
    float roughness = lerp(0.85, 0.25, ice);
    float metallic = 0.0;

    float shadow = SunShadow(input.World, n);
    float3 color = ShadeDirect(albedo, metallic, roughness, n, v, SunDir, SunColor * 5.0 * shadow);

    // Lumière renvoyée par la planète (orange) et ciel étoilé très faible.
    float3 toPlanet = normalize(PlanetCenter.xyz - input.World);
    float planetLit = saturate(dot(-toPlanet, SunDir) * 0.5 + 0.5);
    color += albedo * float3(1.0, 0.6, 0.35) * 0.6 * planetLit * saturate(dot(n, toPlanet) * 0.6 + 0.4);
    color += albedo * float3(0.05, 0.06, 0.09);

    o.Color = float4(color, 1.0);
    o.Velocity = ScreenVelocity(input.World);
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// Fond : étoiles et planète géante gazeuse (bandes nuageuses turbulentes, atmosphère, ombre de l'anneau).
// ---------------------------------------------------------------------------------------------------------------

struct BackgroundOut
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
    float Depth : SV_Depth;
};

float3 PlanetBands(float3 p)
{
    float latitude = p.y;
    // Les bandes glissent les unes sur les autres (cisaillement selon la latitude) et se déchirent en tourbillons.
    float shear = sin(latitude * 23.0) * 0.35;
    float3 q = float3(p.x * cos(shear) - p.z * sin(shear), p.y, p.x * sin(shear) + p.z * cos(shear));
    float turbulence = Fbm(float3(q.x * 2.5, q.y * 11.0, q.z * 2.5) + Time * 0.008, 7);
    float fine = Fbm(float3(q.x * 9.0, q.y * 40.0, q.z * 9.0), 4);
    float band = latitude * 7.5 + turbulence * 1.4 + fine * 0.25;
    float3 cream = float3(0.96, 0.88, 0.72);
    float3 rust = float3(0.76, 0.44, 0.24);
    float3 brown = float3(0.40, 0.25, 0.16);
    float3 color = lerp(cream, rust, 0.5 + 0.5 * sin(band * 2.1));
    color = lerp(color, brown, saturate(sin(band * 0.7 + 1.0) * 0.8));
    color *= 0.9 + 0.2 * fine;
    // Pôles plus sombres et bleutés.
    color = lerp(color, float3(0.35, 0.38, 0.45), smoothstep(0.75, 0.98, abs(latitude)) * 0.7);
    // Une tempête ovale.
    float2 storm = float2(atan2(p.z, p.x) - 0.9, (latitude + 0.32) * 3.5);
    color = lerp(color, float3(0.85, 0.35, 0.2), smoothstep(0.35, 0.1, length(storm)) * 0.8);
    return color;
}

BackgroundOut BackgroundPS(FullscreenOut input)
{
    BackgroundOut o;
    float3 direction = RayDirection(input.UV);
    float3 color = float3(0.001, 0.0015, 0.003);
    float3 cell = floor(direction * 500.0);
    color += Hash31(cell) > 0.9987 ? (0.3 + 2.5 * Hash31(cell + 3.0)) * float3(0.85, 0.9, 1.0) : 0.0;
    color += float3(0.02, 0.012, 0.03) * pow(saturate(Fbm(direction * 2.5, 4) + 0.3), 3.0);

    float depth = 1.0;
    float3 hitPoint = CameraPos + direction * 5000.0;
    float3 toCenter = PlanetCenter.xyz - CameraPos;
    float along = dot(toCenter, direction);
    float radius = RingParams.z;
    float closest2 = dot(toCenter, toCenter) - along * along;
    if (along > 0.0 && closest2 < radius * radius)
    {
        float t = along - sqrt(radius * radius - closest2);
        hitPoint = CameraPos + direction * t;
        float3 n = normalize(hitPoint - PlanetCenter.xyz);
        float3 local = n;
        float3 albedo = PlanetBands(local);
        // Terminateur adouci par l'atmosphère, bord assombri.
        float diffuse = smoothstep(-0.08, 0.35, dot(n, SunDir));
        float rim = pow(1.0 - saturate(dot(n, -direction)), 3.0);
        color = albedo * SunColor * 1.6 * diffuse + float3(0.4, 0.55, 0.9) * rim * diffuse * 0.6;
        // Ombre portée de l'anneau sur la planète : bande sombre là où la lumière traverse le plan de l'anneau.
        float tPlane = -hitPoint.y / SunDir.y;
        float3 crossing = hitPoint + SunDir * tPlane;
        float ringRadius = length(crossing.xz);
        float ringShadow = tPlane > 0.0 ? smoothstep(55.0, 60.0, ringRadius) * smoothstep(118.0, 108.0, ringRadius) : 0.0;
        color *= 1.0 - 0.75 * ringShadow;
        float4 clip = mul(float4(hitPoint, 1.0), ViewProjNoJitter);
        depth = clip.z / clip.w;
    }
    else
    {
        // Halo de l'atmosphère autour de la planète.
        float glow = exp(-max(sqrt(max(closest2, 0.0)) - radius, 0.0) * 0.12);
        color += float3(0.35, 0.5, 0.9) * glow * 0.25 * saturate(dot(normalize(direction), SunDir) * 0.5 + 0.6) * (along > 0.0 ? 1.0 : 0.0);
    }

    o.Color = float4(color, 1.0);
    o.Velocity = depth < 1.0 ? ScreenVelocity(hitPoint) : DirectionVelocity(direction);
    o.Depth = depth;
    return o;
}
