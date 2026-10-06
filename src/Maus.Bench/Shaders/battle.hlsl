// Benchmark MAUS, test « Effets » : un champ de bataille au coucher du soleil. Vingt-quatre chars explosent l'un après
// l'autre : éclair, boule de feu, tourelle projetée en l'air, milliers d'éclats qui rebondissent, gerbes d'étincelles,
// colonnes de fumée qui s'accumulent. Les trajectoires sont calculées par la carte à chaque image (balistique avec
// frottement de l'air, rebonds sur le sol, poussée de la fumée chaude) ; la fumée et le feu superposent des millions de
// pixels transparents.
#include "common.hlsli"

static const uint Tanks = 24;
static const uint DebrisPerTank = 320;
static const uint FirePerTank = 6000;
static const uint SmokePerTank = 2200;
static const uint SparksPerTank = 5000;
static const float Gravity = 9.81;

cbuffer BattleConstants : register(b1)
{
    float4x4 ShadowViewProj;
    float4 Battle;              // x = temps, y = premier dessin (décalage d'instance), z = genre de maillage (0 caisse, 1 tourelle, 2 éclat), w = taille d'un texel d'ombre
    float4 LightPosition[8];    // lumières des explosions : position (xyz), intensité (w)
    float4 LightColor[8];
};

struct TankData
{
    float4 PositionHeading;     // position (xyz), cap (w, radians)
    float4 Explosion;           // instant de l'explosion (x), teinte du camouflage (y), graine (z)
};

StructuredBuffer<TankData> TankList : register(t0);
Texture2D<float> ShadowMap : register(t1);

float Rand(uint seed, uint salt)
{
    return HashU(seed * 0x9E3779B9u ^ HashU(salt + 0x632BE5ABu)) * (1.0 / 4294967296.0);
}

float3 RandomDirection(uint seed, uint salt)
{
    float z = Rand(seed, salt) * 2.0 - 1.0;
    float a = Rand(seed, salt + 1) * TAU;
    float r = sqrt(saturate(1.0 - z * z));
    return float3(r * cos(a), z, r * sin(a));
}

float3 RotateY(float3 v, float angle)
{
    float c = cos(angle), s = sin(angle);
    return float3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
}

// Les chars avancent en colonne sur une vingtaine de mètres, puis tiennent leur position en tirant ; la tourelle
// balaie l'horizon jusqu'à l'explosion.
static const float TankSpeed = 1.6;
static const float TankTravel = 20.0;

float3 TankForward(float heading)
{
    return float3(cos(heading), 0.0, -sin(heading));
}

float3 TankPosition(TankData d, float time)
{
    return d.PositionHeading.xyz + TankForward(d.PositionHeading.w) * min(TankSpeed * clamp(time, 0.0, d.Explosion.x), TankTravel);
}

float TurretYaw(TankData d, float time)
{
    float t = min(time, d.Explosion.x);
    return 0.45 * sin(0.23 * t + d.Explosion.z * 1.7) + 0.15 * sin(0.61 * t + d.Explosion.z);
}

float3 RotateAxis(float3 v, float3 axis, float angle)
{
    float c = cos(angle), s = sin(angle);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}

// Vol balistique avec rebonds (jusqu'à quatre) : position et instant d'immobilisation, en formules fermées.
float3 Ballistic(float3 start, float3 velocity, float t, out float settled)
{
    float3 p = start;
    float3 v = velocity;
    float remaining = t;
    settled = 1e9;
    float elapsed = 0.0;
    [unroll]
    for (int bounce = 0; bounce < 4; bounce++)
    {
        // Instant où l'objet touche le sol (y = 0).
        float disc = v.y * v.y + 2.0 * Gravity * max(p.y, 0.0);
        float hit = (v.y + sqrt(disc)) / Gravity;
        if (remaining < hit)
        {
            return p + v * remaining + float3(0, -0.5 * Gravity * remaining * remaining, 0);
        }

        p += v * hit + float3(0, -0.5 * Gravity * hit * hit, 0);
        p.y = 0.0;
        remaining -= hit;
        elapsed += hit;
        float impact = v.y - Gravity * hit;
        v = float3(v.x * 0.45, -impact * 0.32, v.z * 0.45);
        if (abs(v.y) < 0.6)
        {
            settled = elapsed;
            // Glissade qui s'arrête par frottement.
            float slide = min(remaining, 0.6);
            return p + float3(v.x, 0, v.z) * slide * (1.0 - slide / 1.2);
        }
    }

    settled = elapsed;
    return p;
}

float3 Sky(float3 d)
{
    float up = saturate(d.y);
    float3 horizon = float3(1.0, 0.55, 0.3);
    float3 zenith = float3(0.12, 0.2, 0.42);
    float3 color = lerp(horizon, zenith, pow(up, 0.45)) * 0.9;
    float sun = saturate(dot(d, SunDir));
    color += SunColor * (pow(sun, 2000.0) * 80.0 + pow(sun, 60.0) * 0.8 + pow(sun, 6.0) * 0.25);
    return color;
}

// ---------------------------------------------------------------------------------------------------------------
// Objets solides : caisse du char, tourelle (projetée à l'explosion), éclats.
// ---------------------------------------------------------------------------------------------------------------

struct MeshVertex
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float Material : TEXCOORD0;     // 0 peinture, 1 chenille, 2 galet, 3 acier, 4 caoutchouc
};

struct SolidPixel
{
    float4 Position : SV_Position;
    float3 World : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float3 Local : TEXCOORD2;
    nointerpolation float4 Info : TEXCOORD3;   // teinte, brûlé (0-1), braise (0-1), genre
    nointerpolation float Material : TEXCOORD4;
};

float3 SolidWorld(MeshVertex v, uint instance, out float3 normal, out float4 info)
{
    uint kind = (uint)Battle.z;
    float time = Battle.x;
    uint index = instance + (uint)Battle.y;
    if (kind == 2)
    {
        // Éclat : index = char × éclats + numéro.
        uint tank = index / DebrisPerTank;
        uint piece = index % DebrisPerTank;
        TankData data = TankList[tank];
        float t = time - data.Explosion.x;
        uint seed = index * 7u + 3u;
        float size = 0.12 + 0.55 * pow(Rand(seed, 1), 3.0);
        if (t < 0.0)
        {
            normal = 0;
            info = 0;
            return float3(0, -100, 0);
        }

        float3 direction = RandomDirection(seed, 2);
        direction.y = abs(direction.y) * 1.4 + 0.2;
        float speed = 6.0 + 22.0 * Rand(seed, 4) * (1.0 - size);
        float3 start = TankPosition(data, data.Explosion.x) + float3(0, 1.4, 0) + RandomDirection(seed, 5) * 1.2;
        float settled;
        float3 center = Ballistic(start, normalize(direction) * speed, t, settled);
        float3 axis = RandomDirection(seed, 7);
        float spin = (2.0 + 10.0 * Rand(seed, 8)) * min(t, settled + 0.3);
        float3 local = v.Position * float3(size, size * (0.4 + 0.6 * Rand(seed, 9)), size * (0.5 + Rand(seed, 10)));
        normal = RotateAxis(v.Normal, axis, spin);
        info = float4(data.Explosion.y, 1.0, saturate(1.0 - t / 9.0), 2.0);
        return RotateAxis(local, axis, spin) + center + float3(0, size * 0.3, 0);
    }

    TankData data = TankList[index];
    float t = time - data.Explosion.x;
    float burnt = saturate(t * 4.0);
    float3 local = v.Position;
    float3 n = v.Normal;
    float heading = data.PositionHeading.w;
    if (kind == 1)
    {
        // Tourelle : pivote autour de son axe (x = -0,25 m).
        float yaw = TurretYaw(data, time);
        local = RotateY(local - float3(-0.25, 0, 0), yaw) + float3(-0.25, 0, 0);
        n = RotateY(n, yaw);
    }

    if (kind == 1 && t > 0.0)
    {
        // La tourelle est arrachée et projetée en tournoyant, puis retombe.
        uint seed = index * 13u + 1u;
        float3 launch = float3(Rand(seed, 1) - 0.5, 0, Rand(seed, 2) - 0.5) * 7.0 + float3(0, 17.0 + 6.0 * Rand(seed, 3), 0);
        float settled;
        float3 offset = Ballistic(float3(0, 2.1, 0), launch, t, settled) - float3(0, 2.1, 0);
        float3 axis = normalize(float3(Rand(seed, 4) - 0.5, 0.3, Rand(seed, 5) - 0.5));
        float spin = 3.5 * min(t, settled + 0.2);
        local = RotateAxis(local - float3(0, 2.1, 0), axis, spin) + float3(0, 2.1, 0) + RotateY(offset, -heading);
        n = RotateAxis(n, axis, spin);
    }

    normal = RotateY(n, heading);
    info = float4(data.Explosion.y, burnt, t > 0.0 ? saturate(1.0 - t / 25.0) : 0.0, (float)kind);
    return RotateY(local, heading) + TankPosition(data, time);
}

SolidPixel SolidVS(MeshVertex v, uint instance : SV_InstanceID)
{
    SolidPixel o;
    float3 normal;
    float4 info;
    float3 world = SolidWorld(v, instance, normal, info);
    o.Position = mul(float4(world, 1.0), ViewProj);
    o.World = world;
    o.Normal = normal;
    o.Local = v.Position;
    o.Info = info;
    o.Material = v.Material;
    return o;
}

float4 SolidShadowVS(MeshVertex v, uint instance : SV_InstanceID) : SV_Position
{
    float3 normal;
    float4 info;
    float3 world = SolidWorld(v, instance, normal, info);
    return mul(float4(world, 1.0), ShadowViewProj);
}

float SunShadow(float3 world, float3 normal)
{
    float4 shadow = mul(float4(world + normal * 0.08, 1.0), ShadowViewProj);
    float3 uvz = shadow.xyz / shadow.w;
    float2 uv = uvz.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0))
    {
        return 1.0;
    }

    float sum = 0.0;
    [unroll]
    for (int y = -1; y <= 2; y++)
    {
        [unroll]
        for (int x = -1; x <= 2; x++)
        {
            sum += ShadowMap.SampleCmpLevelZero(ShadowCompare, uv + (float2(x, y) - 0.5) * Battle.w, uvz.z - 0.0008);
        }
    }

    return sum / 16.0;
}

// Lumière des explosions : sources ponctuelles, décroissance en carré de la distance.
float3 ExplosionLight(float3 world, float3 n, float3 albedo)
{
    float3 sum = 0.0;
    [unroll]
    for (int i = 0; i < 8; i++)
    {
        float3 toLight = LightPosition[i].xyz - world;
        float d2 = dot(toLight, toLight);
        float3 l = toLight * rsqrt(d2 + 1e-4);
        sum += albedo * LightColor[i].rgb * LightPosition[i].w * saturate(dot(n, l) * 0.8 + 0.2) / (d2 + 4.0);
    }

    return sum;
}

struct SceneOut
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
};

SceneOut SolidPS(SolidPixel input)
{
    SceneOut o;
    float3 n = normalize(input.Normal);
    float3 v = normalize(CameraPos - input.World);
    // Les pièces sont dessinées des deux côtés : la normale est retournée vers la caméra.
    n = dot(n, v) < 0.0 ? -n : n;
    float hue = input.Info.x;
    float burnt = input.Info.y;
    float ember = input.Info.z;

    // Camouflage : trois schémas selon le char (désert, forêt, gris urbain), taches nettes découpées dans un bruit.
    float pattern = Fbm(input.Local * 0.55 + hue * 13.0, 3);
    float scheme = floor(hue * 3.0);
    float3 c1, c2, c3;
    if (scheme < 0.5)
    {
        c1 = float3(0.46, 0.38, 0.24); c2 = float3(0.33, 0.27, 0.16); c3 = float3(0.18, 0.15, 0.10);
    }
    else if (scheme < 1.5)
    {
        c1 = float3(0.19, 0.22, 0.12); c2 = float3(0.27, 0.22, 0.14); c3 = float3(0.06, 0.06, 0.05);
    }
    else
    {
        c1 = float3(0.36, 0.37, 0.36); c2 = float3(0.24, 0.25, 0.26); c3 = float3(0.12, 0.12, 0.13);
    }
    float3 paint = pattern > 0.1 ? c1 : (pattern < -0.12 ? c3 : c2);

    // Panneaux de blindage (joints fins), poussière qui monte depuis le sol, arêtes usées plus claires.
    float2 panel = abs(frac(input.Local.xz * float2(0.9, 1.4)) - 0.5);
    float seam = smoothstep(0.49, 0.5, max(panel.x, panel.y));
    float dust = saturate(1.2 - input.Local.y * 0.75) * (0.55 + 0.45 * ValueNoise(input.Local * 4.0));
    float wear = smoothstep(0.55, 0.8, ValueNoise(input.Local * 9.0)) * saturate(n.y + 0.3);
    float3 dustColor = float3(0.55, 0.45, 0.32);

    float material = input.Material;
    float3 albedo;
    float metallic;
    float roughness;
    if (material < 0.5 || (material > 1.5 && material < 2.5))
    {
        albedo = paint * (1.0 - seam * 0.45);
        albedo = lerp(albedo, dustColor, dust * 0.45);
        albedo = lerp(albedo, albedo * 1.6 + 0.05, wear * 0.5);
        metallic = 0.1;
        roughness = 0.6;
        if (material > 1.5)
        {
            // Galets : bandage de caoutchouc sur le pourtour (faces dont la normale est dans le plan de la roue).
            float rim = 1.0 - abs(normalize(input.Normal).z);
            albedo = lerp(albedo, float3(0.03, 0.03, 0.03), step(0.6, rim));
        }
    }
    else if (material < 1.5)
    {
        // Chenilles : acier sombre, crampons polis par le sol, rouille et terre dans les creux.
        float shine = saturate(n.y * -1.0 + 0.2);
        albedo = lerp(float3(0.07, 0.065, 0.06), float3(0.2, 0.12, 0.07), ValueNoise(input.World * 6.0) * 0.6);
        albedo = lerp(albedo, dustColor * 0.7, dust * 0.6);
        metallic = 0.6 * (1.0 - dust);
        roughness = lerp(0.35, 0.8, dust) - shine * 0.1;
    }
    else
    {
        albedo = material < 3.5 ? float3(0.09, 0.09, 0.085) : float3(0.03, 0.03, 0.03);
        metallic = material < 3.5 ? 0.7 : 0.0;
        roughness = material < 3.5 ? 0.4 : 0.9;
    }

    albedo = lerp(albedo, float3(0.05, 0.045, 0.04) * (0.7 + 0.6 * saturate(pattern + 0.5)), burnt);
    metallic *= 1.0 - burnt;
    roughness = lerp(roughness, 0.95, burnt);

    float shadow = SunShadow(input.World, n);
    float3 color = ShadeDirect(albedo, metallic, roughness, n, v, SunDir, SunColor * 4.0 * shadow);
    color += albedo * lerp(float3(0.10, 0.12, 0.18), float3(0.18, 0.12, 0.08), n.y * 0.5 + 0.5) * 0.6;
    color += ExplosionLight(input.World, n, albedo);

    // Reflet du ciel du soir (Fresnel) : le métal et la peinture satinée renvoient la lumière de l'horizon.
    float3 reflected = reflect(-v, n);
    float3 f0 = lerp(0.04.xxx, albedo, metallic);
    float3 fresnel = FresnelSchlick(saturate(dot(n, v)), f0);
    color += fresnel * Sky(reflected) * (1.0 - roughness) * (reflected.y > 0.0 ? 0.7 : 0.15) * shadow;

    // Braises : lueur rouge dans les fissures du métal brûlé, qui s'éteint peu à peu.
    float crack = pow(saturate(Fbm(input.Local * 3.0 + 7.0, 3) * 2.0 + 0.2), 6.0);
    color += float3(1.6, 0.45, 0.1) * crack * ember * burnt;

    o.Color = float4(color, 1.0);
    o.Velocity = ScreenVelocity(input.World);
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// Sol du désert et ciel du soir (une passe plein écran, profondeur écrite pour les objets).
// ---------------------------------------------------------------------------------------------------------------

struct GroundOut
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
    float Depth : SV_Depth;
};

GroundOut GroundPS(FullscreenOut input)
{
    GroundOut o;
    float3 d = RayDirection(input.UV);
    if (d.y >= -1e-4)
    {
        o.Color = float4(Sky(d), 1.0);
        o.Velocity = DirectionVelocity(d);
        o.Depth = 1.0;
        return o;
    }

    float t = -CameraPos.y / d.y;
    float3 p = CameraPos + d * t;

    // Dunes : relief simulé par le gradient d'un bruit (la surface reste plane, l'éclairage ondule).
    float e = 0.15;
    float h0 = Fbm(float3(p.xz * 0.02, 0.0), 5);
    float hx = Fbm(float3((p.xz + float2(e, 0)) * 0.02, 0.0), 5);
    float hz = Fbm(float3((p.xz + float2(0, e)) * 0.02, 0.0), 5);
    float3 n = normalize(float3(-(hx - h0) * 9.0, 1.0, -(hz - h0) * 9.0));
    float ripples = sin(p.x * 1.7 + Fbm(float3(p.xz * 0.3, 1.0), 3) * 6.0) * 0.5 + 0.5;
    n = normalize(n + float3(ripples * 0.06, 0, 0));

    float3 albedo = lerp(float3(0.5, 0.36, 0.23), float3(0.64, 0.48, 0.31), saturate(h0 * 2.0 + 0.5)) * (0.9 + 0.1 * ripples);
    // Graviers et cailloux (bruit cellulaire), plaques de terre plus sombre, traînées laissées par le vent.
    float2 pebbles = Cellular(float3(p.xz * 1.6, 0.5));
    float stone = smoothstep(0.32, 0.18, pebbles.x) * step(0.55, Hash31(floor(float3(p.xz * 1.6, 0.5))));
    albedo = lerp(albedo, float3(0.32, 0.27, 0.22) * (0.7 + 0.6 * Hash31(floor(float3(p.xz * 1.6, 3.0)))), stone * 0.8);
    albedo *= 0.85 + 0.15 * smoothstep(-0.3, 0.3, Fbm(float3(p.xz * 0.06, 9.0), 3));
    albedo *= 0.94 + 0.06 * sin(p.z * 0.9 + Fbm(float3(p.xz * 0.15, 4.0), 2) * 5.0);
    n = normalize(n + float3(0, 0, 0) + stone * normalize(float3(p.x - floor(p.x * 1.6) / 1.6, 3.0, p.z - floor(p.z * 1.6) / 1.6)) * 0.3);

    // Cratères et traces de brûlure autour de chaque char qui a explosé, onde de choc dans le sable.
    float3 glow = 0.0;
    [loop]
    for (uint i = 0; i < Tanks; i++)
    {
        TankData data = TankList[i];
        float since = Battle.x - data.Explosion.x;
        if (since < 0.0)
        {
            continue;
        }

        float r = length(p.xz - TankPosition(data, data.Explosion.x).xz);
        float wave = since * 45.0;
        if (r > 12.0 && abs(r - wave) > 8.0)
        {
            continue;
        }

        float scorch = saturate(1.0 - r / (6.0 + 2.0 * Fbm(float3(p.xz * 0.4, i), 2))) * saturate(since * 3.0);
        albedo = lerp(albedo, float3(0.05, 0.045, 0.04), scorch * 0.9);
        float ring = exp(-pow((r - wave) * 0.5, 2.0)) * saturate(1.0 - since * 0.8);
        glow += float3(1.0, 0.75, 0.5) * ring * 0.12;
        glow += float3(0.9, 0.25, 0.05) * pow(scorch, 4.0) * saturate(1.0 - since / 20.0) * (0.6 + 0.4 * Fbm(float3(p.xz * 2.0, Battle.x * 2.0), 2));
    }

    float shadow = SunShadow(p, n);
    float3 v = -d;
    float3 color = ShadeDirect(albedo, 0.0, 0.85, n, v, SunDir, SunColor * 4.0 * shadow);
    color += albedo * float3(0.10, 0.12, 0.18) * 0.7;
    color += ExplosionLight(p, n, albedo) + glow;

    // Brume au loin : le sable se fond dans la lumière du soir.
    float haze = 1.0 - exp(-t * 0.0045);
    color = lerp(color, Sky(normalize(float3(d.x, 0.02, d.z))), haze);

    // Mirage : au ras de l'horizon, l'air brûlant au-dessus du sable renvoie le ciel comme un miroir qui tremble.
    float grazing = smoothstep(-0.07, -0.004, d.y);
    float shimmer = Fbm(float3(p.xz * 0.05, Time * 1.5), 3) * 0.025;
    float3 mirrored = Sky(normalize(float3(d.x, -d.y + shimmer, d.z)));
    color = lerp(color, mirrored, grazing * 0.75);

    float4 clip = mul(float4(p, 1.0), ViewProjNoJitter);
    o.Color = float4(color, 1.0);
    o.Velocity = ScreenVelocity(p);
    o.Depth = clip.z / clip.w;
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// Feu, fumée et étincelles : chaque particule est une fonction du temps écoulé depuis l'explosion de son char.
// ---------------------------------------------------------------------------------------------------------------

struct ParticlePixel
{
    float4 Position : SV_Position;
    float2 Corner : TEXCOORD0;
    float4 Color : COLOR0;      // couleur (rgb), opacité (a)
    float Kind : TEXCOORD1;
};

static const float2 QuadCorners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };

// Trajet freiné par l'air : vitesse × (1 − e^(−k t)) / k, plus une poussée verticale (fumée chaude).
float3 Dragged(float3 velocity, float k, float t, float lift)
{
    return velocity * (1.0 - exp(-k * t)) / k + float3(0, lift * t * t * 0.5, 0);
}

static const uint MuzzlePerTank = 48;

// Tirs : chaque char tire toutes les 5 à 8 secondes jusqu'à sa destruction (flamme de bouche, fumée, traceur).
bool Shot(uint tank, float time, out float shotTime, out float3 muzzle, out float3 direction)
{
    TankData data = TankList[tank];
    float period = 5.5 + 3.0 * Rand(tank, 31);
    float phase = 1.0 + 4.0 * Rand(tank, 32);
    float since = time - phase;
    shotTime = since - floor(since / period) * period;
    float shotStart = time - shotTime;
    float yaw = TurretYaw(data, shotStart);
    float heading = data.PositionHeading.w;
    float3 tip = RotateY(float3(5.65, 2.06, 0) - float3(-0.25, 0, 0), yaw) + float3(-0.25, 0, 0);
    muzzle = RotateY(tip, heading) + TankPosition(data, shotStart);
    direction = RotateY(RotateY(float3(1, 0.012, 0), yaw), heading);
    return since >= 0.0 && shotStart < data.Explosion.x;
}

ParticlePixel ParticleVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    ParticlePixel o;
    uint kind = (uint)Battle.z;          // 0 fumée, 1 feu, 2 étincelles, 3 tirs
    if (kind == 3)
    {
        uint shooter = instance / MuzzlePerTank;
        uint k = instance % MuzzlePerTank;
        uint shotSeed = instance * 5u + 11u;
        float shotTime;
        float3 muzzle, direction;
        bool firing = Shot(shooter, Battle.x, shotTime, muzzle, direction);
        float muzzleLife = k == 0 ? 0.9 : 0.25 + 0.45 * Rand(shotSeed, 1);
        if (!firing || shotTime > muzzleLife)
        {
            o.Position = float4(0, 0, -1, 1);
            o.Corner = 0;
            o.Color = 0;
            o.Kind = 0;
            return o;
        }

        float3 position;
        float size;
        float4 color;
        if (k == 0)
        {
            // Traceur de l'obus : file à 380 m/s.
            position = muzzle + direction * 380.0 * shotTime;
            size = 0.22;
            color = float4(3.2, 1.9, 0.7, 1.0);
        }
        else
        {
            float a = saturate(shotTime / muzzleLife);
            float3 spread = normalize(direction * (2.0 + 2.0 * Rand(shotSeed, 2)) + RandomDirection(shotSeed, 3) * 0.9);
            position = muzzle + Dragged(spread * (6.0 + 14.0 * Rand(shotSeed, 4)), 6.0, shotTime, 0.6);
            size = 0.2 + 1.2 * sqrt(a);
            color = float4(lerp(float3(1.4, 0.9, 0.35), float3(0.02, 0.018, 0.015), saturate(a * 3.0)), 1.0 - a);
        }

        float4 muzzleView = mul(float4(position, 1.0), View);
        float2 muzzleCorner = QuadCorners[vertex];
        muzzleView.xy += muzzleCorner * size;
        o.Position = mul(muzzleView, Proj);
        o.Corner = muzzleCorner;
        o.Color = color;
        o.Kind = 1.0;
        return o;
    }

    uint perTank = kind == 0 ? SmokePerTank : (kind == 1 ? FirePerTank : SparksPerTank);
    uint tank = instance / perTank;
    uint seed = instance * 3u + kind * 0x51ED27u;
    TankData data = TankList[tank];
    float t = Battle.x - data.Explosion.x;
    float3 origin = TankPosition(data, data.Explosion.x) + float3(0, 1.5, 0);
    float3 position;
    float size;
    float4 color;
    float life;

    if (kind == 1)
    {
        // Boule de feu : jaillit en tous sens puis monte et s'assombrit.
        life = 1.2 + 2.2 * Rand(seed, 1);
        float3 dir = RandomDirection(seed, 2);
        dir.y = abs(dir.y) * 0.9 + 0.15;
        float speed = 10.0 + 22.0 * Rand(seed, 4);
        position = origin + Dragged(normalize(dir) * speed, 2.8, t, 3.5);
        float a = saturate(t / life);
        size = 0.5 + 2.8 * sqrt(a) * (0.6 + Rand(seed, 5));
        // Des milliers de flammèches se superposent : chacune ne porte qu'une petite part de la lumière.
        float3 hot = lerp(float3(0.09, 0.062, 0.028), float3(0.07, 0.02, 0.005), saturate(a * 2.0));
        color = float4(lerp(hot, float3(0.008, 0.002, 0.0005), saturate(a * 1.6 - 0.6)), 1.0 - a);
    }
    else if (kind == 2)
    {
        // Étincelles : rapides, retombent sous la gravité, s'éteignent vite.
        life = 0.8 + 2.4 * Rand(seed, 1);
        float3 dir = RandomDirection(seed, 2);
        dir.y = abs(dir.y) + 0.3;
        float speed = 18.0 + 42.0 * Rand(seed, 4);
        float settled;
        position = Ballistic(origin, normalize(dir) * speed, t * 0.9, settled);
        size = 0.06 + 0.08 * Rand(seed, 5);
        float a = saturate(t / life);
        color = float4(float3(1.6, 0.7, 0.2) * (1.0 - a), 1.0 - a);
    }
    else
    {
        // Fumée : naît un peu après l'éclair, s'élève en colonne, s'étale et dérive avec le vent pendant longtemps.
        // Colonne qui monte vite au début puis plafonne et s'étale, poussée lentement par le vent.
        float delay = 0.2 + 1.5 * Rand(seed, 6);
        float local = t - delay;
        life = 25.0 + 30.0 * Rand(seed, 1);
        float3 dir = RandomDirection(seed, 2);
        dir.y = abs(dir.y);
        float speed = 2.0 + 7.0 * Rand(seed, 4);
        float rise = (8.0 + 30.0 * Rand(seed, 9)) * (1.0 - exp(-max(local, 0.0) / 9.0));
        float3 drift = float3(0.35, 0, 0.12) * local * (0.3 + rise / 30.0);
        float swirl = Rand(seed, 7) * TAU + local * (0.2 + 0.3 * Rand(seed, 8));
        position = origin + Dragged(dir * speed * 0.5, 1.5, max(local, 0.0), 0.0)
            + float3(0, rise, 0) + drift
            + float3(cos(swirl), 0, sin(swirl)) * min(local, 15.0) * (0.08 + rise / 160.0);
        float a = saturate(local / life);
        size = 0.9 + 3.2 * sqrt(a) + rise * 0.07;
        // Fumée noire à la base, plus grise en hauteur.
        float shade = lerp(0.035, 0.16, saturate(rise / 30.0)) * (0.8 + 0.4 * Rand(seed, 10));
        color = float4(shade.xxx, 0.42 * smoothstep(0.0, 0.08, a) * (1.0 - a));
        t = local;
    }

    if (t < 0.0 || t > life)
    {
        o.Position = float4(0, 0, -1, 1);
        o.Corner = 0;
        o.Color = 0;
        o.Kind = 0;
        return o;
    }

    float4 view = mul(float4(position, 1.0), View);
    float2 corner = QuadCorners[vertex];
    view.xy += corner * size;
    o.Position = mul(view, Proj);
    o.Corner = corner;
    o.Color = color;
    o.Kind = (float)kind;
    return o;
}

float4 SmokePS(ParticlePixel input) : SV_Target
{
    // Bouffée en volume : bord effiloché par un bruit, cœur plus épais et plus sombre, côté du soleil couchant plus clair,
    // lueur du feu par-dessous.
    float r2 = dot(input.Corner, input.Corner);
    float seed = input.Color.r * 97.0 + input.Color.a * 13.0;
    float billow = Fbm(float3(input.Corner * 1.8, seed), 3);
    float shape = saturate(1.0 - r2 + billow * 0.6);
    float alpha = input.Color.a * shape * shape;
    float thickness = saturate(1.0 - r2 * 0.8);
    float2 toSun = normalize(float2(-SunDir.x, SunDir.y) + 1e-4);
    float sunSide = saturate(dot(input.Corner, toSun) * 0.7 + 0.5);
    float3 lit = input.Color.rgb * (SunColor * (0.6 + 1.1 * sunSide) * (1.0 - thickness * 0.45) + float3(0.6, 0.24, 0.06) * saturate(-input.Corner.y) * 0.7);
    return float4(lit * alpha, alpha);
}

float4 FirePS(ParticlePixel input) : SV_Target
{
    float r2 = dot(input.Corner, input.Corner);
    float core = exp(-r2 * (input.Kind > 1.5 ? 5.0 : 3.0));
    return float4(input.Color.rgb * core * input.Color.a, 0.0);
}
