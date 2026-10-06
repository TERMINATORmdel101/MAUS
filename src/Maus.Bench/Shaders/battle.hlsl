// Benchmark MAUS, test « Effets » : un champ de bataille vallonné au coucher du soleil, semé de pierres. Vingt-quatre
// chars avancent en suivant le relief, tirent, puis sont touchés l'un après l'autre : l'obus perce le blindage (on voit
// le trou), les munitions explosent, la tourelle est arrachée, retombe et se pose sur le relief, des éclats rebondissent
// sur les pentes, des colonnes de fumée s'accumulent. Le relief est calculé pixel par pixel (marche le long du rayon),
// avec les ombres des collines ; les trajectoires sont recalculées par la carte à chaque image ; la fumée et le feu
// superposent des millions de pixels transparents.
#include "common.hlsli"

static const uint Tanks = 24;
static const float Gravity = 9.81;
static const float TurretPivotX = -0.25;
static const float DeckY = 1.66;
static const float TankSpeed = 1.6;
static const float TankTravel = 20.0;

cbuffer BattleConstants : register(b1)
{
    float4x4 ShadowViewProj;
    float4 Battle;              // x = temps, y = premier dessin (décalage d'instance), z = genre (0 caisse, 1 tourelle, 2 éclat), w = taille d'un texel d'ombre
    float4 Counts;              // par char : éclats (x), flammèches (y), bouffées de fumée (z), étincelles (w) ; moins en mode léger
    float4 Gains;               // lumière d'une flammèche (x), opacité d'une bouffée (y) : le mode léger garde le même éclat d'ensemble
    float4 LightPosition[8];    // lumières des explosions et des tirs : position (xyz), intensité (w)
    float4 LightColor[8];
};

struct TankData
{
    float4 PositionHeading;     // départ (xz), cap (w, radians)
    float4 Explosion;           // explosion des munitions (x), teinte du camouflage (y), graine (z), impact de l'obus (w)
    float4 Hit;                 // point d'impact dans le repère du char (xyz), rayon du trou (w)
    float4 Shell;               // direction de l'obus dans le repère du char (xyz, vers l'intérieur), numéro peint sur les jupes (w)
    float4 Ring;                // couronne de la tourelle, dans le monde, une fois le char arrêté (xyz)
    float4 Impact;              // point d'impact dans le monde (xyz)
    float4 ShellWorld;          // direction de l'obus dans le monde (xyz)
};

// Position et orientation de chaque char à l'image en cours (calculées par BattleScene, mêmes formules que TankFrame).
struct TankPoseData
{
    float4 PositionYaw;         // centre du char au sol (xyz), rotation de la tourelle (w)
    float4 Forward;
    float4 Up;
    float4 Side;
};

struct Stone
{
    float4 PositionScale;       // centre (xyz), taille (w)
    float4 AxisAngle;           // axe (xyz), angle (w)
    float4 Color;               // teinte (rgb), aplatissement (a)
};

StructuredBuffer<TankData> TankList : register(t0);
Texture2D<float> ShadowMap : register(t1);
StructuredBuffer<Stone> Stones : register(t2);
StructuredBuffer<TankPoseData> TankPoses : register(t3);

// Mouvement des objets projetés (tourelles arrachées, éclats), calculé une fois par image par MotionCS au lieu d'une fois
// par sommet : tourelle i = 4 valeurs (position de l'axe, avant, haut, côté), puis éclat j = 2 valeurs (centre et angle,
// axe et taille).
static const uint TurretMotion = Tanks * 4;
StructuredBuffer<float4> Motion : register(t4);
RWStructuredBuffer<float4> MotionOut : register(u0);

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

float3 RotateAxis(float3 v, float3 axis, float angle)
{
    float c = cos(angle), s = sin(angle);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}

// ---------------------------------------------------------------------------------------------------------------
// Relief : collines douces sur le champ de bataille (les chars s'y posent presque à plat), plus hautes vers l'horizon.
// Somme de sinusoïdes : la même formule en C# (BattleTerrain.Height) place les chars, les pierres et la caméra.
// ---------------------------------------------------------------------------------------------------------------

float TerrainHeight(float2 p)
{
    float h = 1.6 * sin(p.x * 0.047 + 0.7) * cos(p.y * 0.041 - 0.4)
        + 0.9 * sin(p.x * 0.083 - p.y * 0.061 + 2.1)
        + 0.45 * cos(p.x * 0.163 + p.y * 0.129 + 0.3)
        + 0.18 * sin(p.x * 0.37 - 1.2) * sin(p.y * 0.29 + 0.8);
    float far = smoothstep(70.0, 190.0, length(p));
    return h + far * (7.0 + 7.0 * sin(p.x * 0.019 + 1.3) * cos(p.y * 0.016 + 0.2) + 3.5 * sin(p.x * 0.051 + p.y * 0.043));
}

float3 TerrainNormal(float2 p)
{
    const float e = 0.15;
    float h = TerrainHeight(p);
    return normalize(float3(h - TerrainHeight(p + float2(e, 0.0)), e, h - TerrainHeight(p + float2(0.0, e))));
}

// Ombre des collines : marche vers le soleil au-dessus du relief, ombre douce (rapport hauteur / distance).
float TerrainShadow(float3 p)
{
    float t = 0.4;
    float result = 1.0;
    [loop]
    for (int i = 0; i < (Light ? 8 : 18); i++)
    {
        float3 q = p + SunDir * t;
        float h = q.y - TerrainHeight(q.xz);
        result = min(result, saturate(0.5 + 6.0 * h / t));
        if (result < 0.01 || t > 160.0)
        {
            break;
        }

        t += clamp(h * 0.6 + t * 0.12, 0.4, 24.0);
    }

    return result;
}

// Rayon de la caméra contre le relief : pas réglés sur la hauteur au-dessus du sol, puis affinage par dichotomie.
bool MarchTerrain(float3 origin, float3 d, out float t)
{
    const float top = 19.0;
    t = 0.0;
    float limit = 2600.0;
    if (d.y >= 0.0)
    {
        if (origin.y > top)
        {
            return false;
        }

        limit = min(limit, (top - origin.y) / max(d.y, 1e-4));
    }
    else if (origin.y > top)
    {
        t = (origin.y - top) / -d.y;
    }

    float last = t;
    [loop]
    for (int i = 0; i < (Light ? 72 : 128); i++)
    {
        float3 p = origin + d * t;
        float h = p.y - TerrainHeight(p.xz);
        if (h < 0.0)
        {
            float a = last;
            float b = t;
            [unroll]
            for (int k = 0; k < 7; k++)
            {
                float m = (a + b) * 0.5;
                float3 q = origin + d * m;
                if (q.y - TerrainHeight(q.xz) < 0.0)
                {
                    b = m;
                }
                else
                {
                    a = m;
                }
            }

            t = (a + b) * 0.5;
            return true;
        }

        last = t;
        t += max(h * 0.55, 0.01 + t * 0.012);
        if (t > limit)
        {
            break;
        }
    }

    return false;
}

// Vol balistique avec rebonds sur le relief : position, premier contact et immobilisation. Formules fermées entre deux
// rebonds ; le point de chute est trouvé en trois essais ; un objet n'est jamais sous le sol.
float3 BallisticTerrain(float3 start, float3 velocity, float t, float radius, float bounciness, out float firstHit, out float settled)
{
    float3 p = start;
    float3 v = velocity;
    float remaining = t;
    float elapsed = 0.0;
    firstHit = 1e9;
    settled = 1e9;
    [unroll]
    for (int bounce = 0; bounce < 4; bounce++)
    {
        float ground = TerrainHeight(p.xz) + radius;
        float hit = 0.0;
        [unroll]
        for (int k = 0; k < 3; k++)
        {
            float dy = max(p.y - ground, 0.0);
            hit = (v.y + sqrt(max(v.y * v.y + 2.0 * Gravity * dy, 0.0))) / Gravity;
            ground = TerrainHeight(p.xz + v.xz * hit) + radius;
        }

        if (remaining < hit)
        {
            float3 q = p + v * remaining + float3(0, -0.5 * Gravity * remaining * remaining, 0);
            q.y = max(q.y, TerrainHeight(q.xz) + radius);
            return q;
        }

        p += v * hit + float3(0, -0.5 * Gravity * hit * hit, 0);
        p.y = TerrainHeight(p.xz) + radius;
        remaining -= hit;
        elapsed += hit;
        if (bounce == 0)
        {
            firstHit = elapsed;
        }

        // Rebond sur la pente : composante normale renvoyée et amortie, glissement freiné par le sable.
        float3 n = TerrainNormal(p.xz);
        float3 incoming = float3(v.x, v.y - Gravity * hit, v.z);
        float vn = dot(incoming, n);
        float3 tangential = incoming - n * vn;
        v = tangential * 0.45 - n * vn * bounciness;
        if (abs(vn * bounciness) < 0.7)
        {
            settled = elapsed;
            float slide = min(remaining, 0.6);
            float3 q = p + tangential * 0.45 * slide * (1.0 - slide / 1.2);
            q.y = TerrainHeight(q.xz) + radius;
            return q;
        }
    }

    settled = elapsed;
    p.y = TerrainHeight(p.xz) + radius;
    return p;
}

// ---------------------------------------------------------------------------------------------------------------
// Les chars : position sur le relief (inclinaison d'après la hauteur sous les quatre coins des chenilles).
// ---------------------------------------------------------------------------------------------------------------

struct TankPose
{
    float3 Position;
    float3 Forward;
    float3 Up;
    float3 Side;
};

float3 TankForwardFlat(float heading)
{
    return float3(cos(heading), 0.0, -sin(heading));
}

// Les chars avancent en colonne sur une vingtaine de mètres, puis tiennent leur position en tirant.
float2 TankGround(TankData d, float time)
{
    return d.PositionHeading.xz + TankForwardFlat(d.PositionHeading.w).xz * min(TankSpeed * clamp(time, 0.0, d.Explosion.x), TankTravel);
}

TankPose TankFrame(TankData d, float time)
{
    float3 f = TankForwardFlat(d.PositionHeading.w);
    float3 s = float3(-f.z, 0.0, f.x);
    float2 c = TankGround(d, time);
    float hf = TerrainHeight(c + f.xz * 2.4);
    float hb = TerrainHeight(c - f.xz * 2.4);
    float hl = TerrainHeight(c + s.xz * 1.9);
    float hr = TerrainHeight(c - s.xz * 1.9);
    TankPose pose;
    pose.Forward = normalize(f * 4.8 + float3(0, hf - hb, 0));
    float3 side = normalize(s * 3.8 + float3(0, hl - hr, 0));
    pose.Up = normalize(cross(side, pose.Forward));
    pose.Side = cross(pose.Forward, pose.Up);
    pose.Position = float3(c.x, (hf + hb + hl + hr) * 0.25, c.y);
    return pose;
}

TankPose PoseOf(uint tank)
{
    TankPoseData data = TankPoses[tank];
    TankPose pose;
    pose.Position = data.PositionYaw.xyz;
    pose.Forward = data.Forward.xyz;
    pose.Up = data.Up.xyz;
    pose.Side = data.Side.xyz;
    return pose;
}

float3 ToWorld(TankPose pose, float3 local)
{
    return pose.Position + pose.Forward * local.x + pose.Up * local.y + pose.Side * local.z;
}

float3 DirectionToWorld(TankPose pose, float3 v)
{
    return pose.Forward * v.x + pose.Up * v.y + pose.Side * v.z;
}

float TurretYaw(TankData d, float time)
{
    float t = min(time, d.Explosion.x);
    return 0.45 * sin(0.23 * t + d.Explosion.z * 1.7) + 0.15 * sin(0.61 * t + d.Explosion.z);
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
// Objets solides : caisse du char, tourelle (arrachée à l'explosion), éclats.
// ---------------------------------------------------------------------------------------------------------------

struct MeshVertex
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float Material : TEXCOORD0;     // 0 peinture, 1 chenille, 2 galet, 3 acier, 4 caoutchouc, 5 grille, 6 bois, 7 verre, 8 phare ; +10 arête usée
};

struct SolidPixel
{
    float4 Position : SV_Position;
    float3 World : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float3 Local : TEXCOORD2;
    nointerpolation float4 Info : TEXCOORD3;   // teinte, temps depuis l'impact, temps depuis l'explosion, genre
    nointerpolation float Material : TEXCOORD4;
    nointerpolation uint Tank : TEXCOORD5;
    float3 LocalNormal : TEXCOORD6;
};

float3 SolidWorld(MeshVertex v, uint instance, out float3 normal, out float4 info, out uint tankIndex)
{
    uint kind = (uint)Battle.z;
    float time = Battle.x;
    uint index = instance + (uint)Battle.y;
    if (kind == 2)
    {
        // Éclat : index = char × éclats + numéro ; projeté depuis la couronne de la tourelle, rebondit sur le relief.
        uint tank = index / (uint)Counts.x;
        tankIndex = tank;
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

        float4 centerSpin = Motion[TurretMotion + index * 2u];
        float3 axis = Motion[TurretMotion + index * 2u + 1u].xyz;
        float3 center = centerSpin.xyz;
        float spin = centerSpin.w;
        float3 local = v.Position * float3(size, size * (0.4 + 0.6 * Rand(seed, 9)), size * (0.5 + Rand(seed, 10)));
        normal = RotateAxis(v.Normal, axis, spin);
        info = float4(data.Explosion.y, t + 1.0, t, 2.0);
        return RotateAxis(local, axis, spin) + center;
    }

    tankIndex = index;
    TankData data = TankList[index];
    TankPose pose = PoseOf(index);
    float yaw = TankPoses[index].PositionYaw.w;
    float sinceHit = time - data.Explosion.w;
    float sinceBlast = time - data.Explosion.x;
    info = float4(data.Explosion.y, sinceHit, sinceBlast, (float)kind);
    if (kind == 0)
    {
        normal = DirectionToWorld(pose, v.Normal);
        return ToWorld(pose, v.Position);
    }

    // Tourelle : coordonnées par rapport à son axe, tournées de son angle de visée.
    float3 pivot = float3(TurretPivotX, 2.1, 0.0);
    float3 q = RotateY(v.Position - pivot, yaw);
    float3 qn = RotateY(v.Normal, yaw);
    if (sinceBlast <= 0.0)
    {
        normal = DirectionToWorld(pose, qn);
        return ToWorld(pose, q + pivot);
    }

    // Arrachée par l'explosion des munitions : repère calculé par MotionCS (vol, rebonds, pose sur le relief).
    float3 at = Motion[index * 4u].xyz;
    float3 forward = Motion[index * 4u + 1u].xyz;
    float3 up = Motion[index * 4u + 2u].xyz;
    float3 side = Motion[index * 4u + 3u].xyz;
    normal = forward * qn.x + up * qn.y + side * qn.z;
    return at + forward * q.x + up * q.y + side * q.z;
}

// Une fois par image : tourelles arrachées et éclats. La tourelle tournoie un nombre entier de tours avant de toucher le
// sol (elle retombe sur sa base), rebondit sur le relief, puis s'y pose en suivant la pente ; les éclats rebondissent sur
// les pentes et s'immobilisent. Jamais à travers le sol.
[numthreads(64, 1, 1)]
void MotionCS(uint3 id : SV_DispatchThreadID)
{
    float time = Battle.x;
    uint shards = Tanks * (uint)Counts.x;
    if (id.x < Tanks)
    {
        uint index = id.x;
        TankData data = TankList[index];
        TankPose pose = PoseOf(index);
        float sinceBlast = time - data.Explosion.x;
        float3 pivot = float3(TurretPivotX, 2.1, 0.0);
        float3 at = ToWorld(pose, pivot);
        float3 forward = pose.Forward;
        float3 up = pose.Up;
        float3 side = pose.Side;
        if (sinceBlast > 0.0)
        {
            uint seed = index * 13u + 1u;
            float3 launch = float3(Rand(seed, 1) - 0.5, 0.0, Rand(seed, 2) - 0.5) * 7.0 + float3(0.0, 15.0 + 6.0 * Rand(seed, 3), 0.0);
            float firstHit, settled;
            at = BallisticTerrain(at, launch, sinceBlast, 2.1 - DeckY, 0.28, firstHit, settled);
            if (sinceBlast < firstHit)
            {
                float turns = max(1.0, round(3.2 * firstHit / TAU));
                float spin = TAU * turns * saturate(sinceBlast / firstHit);
                float3 axis = normalize(float3(Rand(seed, 4) - 0.5, 0.25, Rand(seed, 5) - 0.5));
                forward = RotateAxis(forward, axis, spin);
                up = RotateAxis(up, axis, spin);
                side = RotateAxis(side, axis, spin);
            }
            else
            {
                float settle = smoothstep(0.0, 0.35, sinceBlast - firstHit);
                up = normalize(lerp(pose.Up, TerrainNormal(at.xz), settle));
                forward = normalize(pose.Forward - up * dot(pose.Forward, up));
                side = cross(forward, up);
            }
        }

        MotionOut[index * 4u] = float4(at, 0.0);
        MotionOut[index * 4u + 1u] = float4(forward, 0.0);
        MotionOut[index * 4u + 2u] = float4(up, 0.0);
        MotionOut[index * 4u + 3u] = float4(side, 0.0);
        return;
    }

    uint shard = id.x - Tanks;
    if (shard >= shards)
    {
        return;
    }

    uint tank = shard / (uint)Counts.x;
    TankData data = TankList[tank];
    float t = time - data.Explosion.x;
    uint seed = shard * 7u + 3u;
    if (t < 0.0)
    {
        MotionOut[TurretMotion + shard * 2u] = float4(0, -100, 0, 0);
        MotionOut[TurretMotion + shard * 2u + 1u] = float4(0, 1, 0, 0);
        return;
    }

    float size = 0.12 + 0.55 * pow(Rand(seed, 1), 3.0);
    float3 direction = RandomDirection(seed, 2);
    direction.y = abs(direction.y) * 1.4 + 0.2;
    float speed = 6.0 + 22.0 * Rand(seed, 4) * (1.0 - size);
    float3 start = data.Ring.xyz + float3(0, 0.3, 0) + RandomDirection(seed, 5) * 1.0;
    float firstHit, settled;
    float3 center = BallisticTerrain(start, normalize(direction) * speed, t, size * 0.18, 0.32, firstHit, settled);
    float spin = (2.0 + 10.0 * Rand(seed, 8)) * min(t, settled + 0.3);
    MotionOut[TurretMotion + shard * 2u] = float4(center, spin);
    MotionOut[TurretMotion + shard * 2u + 1u] = float4(RandomDirection(seed, 7), size);
}

SolidPixel SolidVS(MeshVertex v, uint instance : SV_InstanceID)
{
    SolidPixel o;
    float3 normal;
    float4 info;
    uint tank;
    float3 world = SolidWorld(v, instance, normal, info, tank);
    o.Position = mul(float4(world, 1.0), ViewProj);
    o.World = world;
    o.Normal = normal;
    o.Local = v.Position;
    o.Info = info;
    o.Material = v.Material;
    o.Tank = tank;
    o.LocalNormal = v.Normal;
    return o;
}

float4 SolidShadowVS(MeshVertex v, uint instance : SV_InstanceID) : SV_Position
{
    float3 normal;
    float4 info;
    uint tank;
    float3 world = SolidWorld(v, instance, normal, info, tank);
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

    // Mode léger : filtre 2×2.
    const int taps = Light ? 2 : 4;
    float sum = 0.0;
    [unroll]
    for (int y = 0; y < taps; y++)
    {
        [unroll]
        for (int x = 0; x < taps; x++)
        {
            sum += ShadowMap.SampleCmpLevelZero(ShadowCompare, uv + (float2(x, y) - (taps - 1) * 0.5) * Battle.w, uvz.z - 0.0008);
        }
    }

    return sum / (taps * taps);
}

// Lumière des explosions et des tirs : sources ponctuelles, décroissance en carré de la distance.
float3 ExplosionLight(float3 world, float3 n, float3 albedo)
{
    float3 sum = 0.0;
    // Les sources sont triées par intensité : le mode léger ne garde que les quatre plus fortes.
    [unroll]
    for (int i = 0; i < (Light ? 4 : 8); i++)
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

// Rectangle plein (pochoir) : 1 dedans, 0 dehors.
float Stencil(float2 p, float2 center, float2 half)
{
    float2 q = abs(p - center) - half;
    return step(max(q.x, q.y), 0.0);
}

// Chiffre de 0 à 9 en sept segments, peint au pochoir : coordonnées 0-1 dans la case du chiffre.
float Digit(uint n, float2 p)
{
    const uint masks[10] = { 0x3Fu, 0x06u, 0x5Bu, 0x4Fu, 0x66u, 0x6Du, 0x7Du, 0x07u, 0x7Fu, 0x6Fu };
    uint m = masks[min(n, 9u)];
    const float t = 0.085;
    float on = 0.0;
    on += (m & 1u) != 0u ? Stencil(p, float2(0.5, 0.92), float2(0.3, t)) : 0.0;
    on += (m & 2u) != 0u ? Stencil(p, float2(0.86, 0.71), float2(t, 0.17)) : 0.0;
    on += (m & 4u) != 0u ? Stencil(p, float2(0.86, 0.29), float2(t, 0.17)) : 0.0;
    on += (m & 8u) != 0u ? Stencil(p, float2(0.5, 0.08), float2(0.3, t)) : 0.0;
    on += (m & 16u) != 0u ? Stencil(p, float2(0.14, 0.29), float2(t, 0.17)) : 0.0;
    on += (m & 32u) != 0u ? Stencil(p, float2(0.14, 0.71), float2(t, 0.17)) : 0.0;
    on += (m & 64u) != 0u ? Stencil(p, float2(0.5, 0.5), float2(0.3, t)) : 0.0;
    return saturate(on);
}

// Numéro du char peint sur le panneau du milieu des jupes (trois chiffres).
float TankNumber(float3 local, float3 localNormal, uint number)
{
    if (abs(localNormal.z) < 0.9 || abs(abs(local.z) - 2.262) > 0.01)
    {
        return 0.0;
    }

    float u = (local.z > 0.0 ? local.x : -local.x) + 0.42;
    float v = (local.y - 0.86) / 0.32;
    if (u < 0.0 || u > 0.74 || v < 0.0 || v > 1.0)
    {
        return 0.0;
    }

    uint k = (uint)(u / 0.26);
    float cell = frac(u / 0.26) * 0.26 / 0.2;
    uint digit = k == 0u ? number / 100u : (k == 1u ? (number / 10u) % 10u : number % 10u);
    return cell <= 1.0 ? Digit(digit, float2(cell, v)) : 0.0;
}

// Volume intérieur de la caisse (sous le toit, entre les flancs, derrière le glacis) : ce qu'on en voit par une ouverture
// est l'intérieur du char, même les faces qui regardent la caméra.
bool HullInterior(float3 local)
{
    float glacisX = 3.02 - (local.y - 1.24) / 0.38 * 1.22;
    return local.y > 1.245 && local.y < DeckY - 0.06 && abs(local.z) < 1.78 && local.x < glacisX - 0.04 && local.x > -2.92;
}

SceneOut SolidPS(SolidPixel input)
{
    SceneOut o;
    float3 v = normalize(CameraPos - input.World);
    float3 geometric = normalize(input.Normal);
    // Vue de l'intérieur (par le trou de l'obus ou la couronne de la tourelle) : la face regarde ailleurs que la caméra.
    bool inside = dot(geometric, v) < 0.0;
    float3 n = inside ? -geometric : geometric;
    float3 local = input.Local;
    float3 localNormal = normalize(input.LocalNormal);
    float hue = input.Info.x;
    float sinceHit = input.Info.y;
    float sinceBlast = input.Info.z;
    uint kind = (uint)(input.Info.w + 0.5);
    TankData data = TankList[input.Tank];
    float fireInside = sinceHit > 0.0 ? saturate(sinceHit * 2.0) * saturate(1.0 - sinceBlast / 70.0) : 0.0;
    float flicker = 0.7 + 0.3 * sin(Battle.x * 23.0 + input.Tank * 1.7) * sin(Battle.x * 13.0 + input.Tank);

    // Trou de l'obus : la plaque est percée (bord irrégulier), l'intérieur sombre se voit au travers.
    float soot = 0.0;
    float hot = 0.0;
    if (kind == 0u && sinceHit > 0.0)
    {
        float3 d = local - data.Hit.xyz;
        float along = dot(d, data.Shell.xyz);
        float3 across = d - data.Shell.xyz * along;
        float radial = length(across);
        float ragged = data.Hit.w * (0.8 + 0.4 * ValueNoise(normalize(across + 1e-4) * 3.0 + data.Explosion.z * 7.0));
        if (radial < ragged && abs(along) < 0.07)
        {
            discard;
        }

        soot = max(soot, saturate(1.0 - (radial - ragged) / 0.9) * (0.75 + 0.25 * ValueNoise(local * 9.0)));
        hot = max(hot, (1.0 - smoothstep(ragged, ragged + 0.07, radial)) * step(abs(along), 0.12));
    }

    // Couronne de la tourelle arrachée : grande ouverture dans le toit, bords tordus et noircis.
    if (kind == 0u && sinceBlast > 0.0)
    {
        float ring = length(local.xz - float2(TurretPivotX, 0.0));
        float edge = 0.95 + 0.08 * ValueNoise(float3(local.xz * 4.0, data.Explosion.z * 5.0));
        if (ring < edge && local.y > DeckY - 0.1 && localNormal.y > 0.7)
        {
            discard;
        }

        soot = max(soot, saturate(1.0 - (ring - edge) / 1.6) * step(DeckY - 0.4, local.y) * (0.7 + 0.3 * ValueNoise(local * 7.0)));
        hot = max(hot, (1.0 - smoothstep(edge, edge + 0.1, ring)) * step(DeckY - 0.12, local.y));
    }

    if (inside || (kind == 0u && sinceHit > 0.0 && HullInterior(local)))
    {
        // Intérieur du char : noir de suie, avec des braises et des flammes qui rougeoient par endroits.
        float embers = pow(ValueNoise(local * 5.0 + float3(0.0, Battle.x * 0.8, 0.0)), 3.0);
        float3 glow = float3(1.4, 0.45, 0.12) * fireInside * flicker * (0.03 + 0.6 * embers);
        o.Color = float4(float3(0.008, 0.007, 0.006) + glow, 1.0);
        o.Velocity = ScreenVelocity(input.World);
        return o;
    }

    // Camouflage : trois schémas (désert, forêt, gris), taches aux bords pulvérisés découpées dans un bruit déformé.
    float3 q = local * 1.05 + hue * 13.0;
    float warp = Fbm(q * 0.6 + 4.0, Light ? 1 : 2);
    float pattern = Fbm(q + warp * 0.9, Light ? 2 : 3);
    float scheme = floor(hue * 3.0);
    float3 c1, c2, c3;
    if (scheme < 0.5)
    {
        c1 = float3(0.5, 0.41, 0.25); c2 = float3(0.3, 0.25, 0.15); c3 = float3(0.13, 0.11, 0.08);
    }
    else if (scheme < 1.5)
    {
        c1 = float3(0.19, 0.23, 0.12); c2 = float3(0.31, 0.25, 0.15); c3 = float3(0.06, 0.06, 0.05);
    }
    else
    {
        c1 = float3(0.4, 0.41, 0.4); c2 = float3(0.24, 0.25, 0.26); c3 = float3(0.09, 0.09, 0.1);
    }

    float3 paint = lerp(c3, c2, smoothstep(-0.17, -0.1, pattern));
    paint = lerp(paint, c1, smoothstep(0.05, 0.12, pattern));
    paint *= 0.9 + 0.2 * ValueNoise(local * 4.0 + 11.0);
    // Peinture passée au soleil sur le dessus, plus terne.
    float topFace = saturate(localNormal.y);
    paint = lerp(paint, dot(paint, float3(0.4, 0.4, 0.2)) * 1.12, topFace * 0.18);

    // Joints de panneaux et rivets (sur la face, selon son orientation), coulures sous les arêtes, poussière, usure.
    float3 an = abs(localNormal);
    float2 face = an.y > max(an.x, an.z) ? local.xz : (an.x > an.z ? local.zy : local.xy);
    float2 cell = frac(face * float2(0.85, 1.25));
    float2 seamDistance = min(cell, 1.0 - cell);
    float seam = 1.0 - smoothstep(0.0, 0.012, min(seamDistance.x, seamDistance.y));
    float2 rivetCell = frac(face * 6.0) - 0.5;
    float rivet = (1.0 - smoothstep(0.12, 0.2, length(rivetCell))) * (1.0 - smoothstep(0.03, 0.05, min(seamDistance.x, seamDistance.y)));
    float streak = smoothstep(0.6, 0.95, ValueNoise(float3(local.x * 7.0, local.y * 0.6, local.z * 7.0))) * (1.0 - an.y);
    float dust = saturate(1.35 - local.y * 0.8) * (0.5 + 0.5 * ValueNoise(local * 3.0)) + topFace * 0.3 * ValueNoise(local * 7.0 + 3.0);
    float3 dustColor = float3(0.56, 0.45, 0.32);

    float material = input.Material;
    bool worn = material > 9.5;
    material = worn ? material - 10.0 : material;
    float chip = worn ? smoothstep(0.32, 0.55, ValueNoise(local * 17.0)) : smoothstep(0.84, 0.93, ValueNoise(local * 11.0)) * 0.7;
    float3 bareSteel = float3(0.3, 0.29, 0.27);

    float3 albedo;
    float metallic;
    float roughness;
    if (material < 0.5 || (material > 1.5 && material < 2.5))
    {
        albedo = paint * (1.0 - seam * 0.45) * (1.0 - streak * 0.25) * (1.0 + rivet * 0.25);
        float number = TankNumber(local, localNormal, (uint)data.Shell.w);
        albedo = lerp(albedo, float3(0.74, 0.72, 0.66) * (0.85 + 0.15 * ValueNoise(local * 30.0)), number * (1.0 - chip));
        albedo = lerp(albedo, dustColor, saturate(dust * 0.38));
        albedo = lerp(albedo, bareSteel, chip);
        metallic = lerp(0.05, 0.8, chip);
        roughness = lerp(0.62, 0.3, chip) + dust * 0.15;
        if (material > 1.5)
        {
            // Galets : bandage de caoutchouc sur le pourtour (faces dont la normale est dans le plan de la roue).
            float rim = 1.0 - abs(localNormal.z);
            albedo = lerp(albedo, float3(0.03, 0.03, 0.03) + dustColor * dust * 0.2, step(0.6, rim));
            roughness = lerp(roughness, 0.9, step(0.6, rim));
        }
    }
    else if (material < 1.5)
    {
        // Chenilles : acier sombre, crampons polis par le sol, rouille et terre dans les creux.
        float shine = saturate(-n.y + 0.2);
        albedo = lerp(float3(0.07, 0.065, 0.06), float3(0.2, 0.12, 0.07), ValueNoise(local * 6.0) * 0.6);
        albedo = lerp(albedo, dustColor * 0.7, saturate(dust * 0.6));
        metallic = 0.6 * (1.0 - saturate(dust));
        roughness = lerp(0.35, 0.8, saturate(dust)) - shine * 0.1;
    }
    else if (material < 3.5)
    {
        // Acier : canon, échappements bleuis par la chaleur, crochets ; arêtes polies.
        albedo = lerp(float3(0.09, 0.09, 0.085), float3(0.16, 0.12, 0.09), ValueNoise(local * 5.0) * 0.5);
        albedo = worn ? albedo * 1.6 : albedo;
        metallic = 0.75;
        roughness = worn ? 0.3 : 0.42;
    }
    else if (material < 4.5)
    {
        albedo = float3(0.035, 0.034, 0.032) + dustColor * dust * 0.15;
        metallic = 0.0;
        roughness = 0.9;
    }
    else if (material < 5.5)
    {
        // Grilles du moteur : barreaux d'acier sombre au-dessus du vide.
        float2 bars = abs(frac(local.xz * float2(14.0, 2.0)) - 0.5);
        albedo = lerp(float3(0.015, 0.014, 0.013), float3(0.12, 0.11, 0.1), step(0.3, bars.x));
        metallic = 0.6;
        roughness = 0.5;
    }
    else if (material < 6.5)
    {
        // Bois et toile (manche d'outil, sac de paquetage).
        float grain = 0.8 + 0.2 * sin(local.x * 60.0 + ValueNoise(local * 8.0) * 6.0);
        albedo = float3(0.32, 0.22, 0.13) * grain;
        metallic = 0.0;
        roughness = 0.8;
    }
    else if (material < 7.5)
    {
        // Verre des épiscopes et du viseur : sombre, lisse, il reflète le ciel.
        albedo = float3(0.02, 0.035, 0.035);
        metallic = 0.0;
        roughness = 0.06;
    }
    else
    {
        albedo = float3(0.55, 0.55, 0.5);
        metallic = 0.0;
        roughness = 0.12;
    }

    // Suie autour des ouvertures (pas sur tout le char) ; métal chauffé au rouge sur les bords du trou, qui refroidit.
    albedo = lerp(albedo, float3(0.03, 0.028, 0.026) * (0.7 + 0.6 * ValueNoise(local * 5.0)), soot * 0.92);
    metallic *= 1.0 - soot * 0.8;
    roughness = lerp(roughness, 0.95, soot);
    if (kind == 2u)
    {
        albedo = lerp(albedo, float3(0.04, 0.035, 0.03), 0.65);
        roughness = 0.9;
        hot = worn ? 0.3 * saturate(1.0 - sinceBlast / 3.0) : 0.0;
    }

    if (kind == 1u && sinceBlast > 0.0)
    {
        // Tourelle arrachée : dessous noirci par l'explosion.
        float under = saturate(1.0 - (local.y - DeckY) / 0.35);
        albedo = lerp(albedo, float3(0.03, 0.028, 0.026), under * 0.85);
    }

    // Occlusion : bas de caisse, train de roulement et dessous dans la pénombre.
    float ao = saturate(0.42 + 0.58 * saturate((local.y - 0.2) / 1.3)) * (localNormal.y < -0.5 ? 0.55 : 1.0);
    float shadow = SunShadow(input.World, n) * TerrainShadow(input.World + n * 0.2);
    float3 color = ShadeDirect(albedo, metallic, roughness, n, v, SunDir, SunColor * 4.0 * shadow);
    float3 skyLight = lerp(float3(0.1, 0.12, 0.18), float3(0.19, 0.13, 0.08), n.y * 0.5 + 0.5);
    float3 bounce = float3(0.2, 0.14, 0.08) * saturate(-n.y * 0.7 + 0.3);
    color += albedo * (skyLight * 0.6 + bounce * 0.5) * ao;
    color += ExplosionLight(input.World, n, albedo) * ao;

    // Reflet du ciel du soir (Fresnel) : le métal, le verre et la peinture satinée renvoient la lumière de l'horizon.
    float3 reflected = reflect(-v, n);
    float3 f0 = lerp(0.04.xxx, albedo, metallic);
    float3 fresnel = FresnelSchlick(saturate(dot(n, v)), f0);
    color += fresnel * Sky(reflected) * (1.0 - roughness) * (reflected.y > 0.0 ? 0.75 : 0.15) * max(shadow, 0.3);

    // Bords du trou et de la couronne rouges de chaleur, puis braises qui s'éteignent ; lueur du feu intérieur.
    float heat = saturate(1.0 - sinceHit / 14.0) * (sinceHit > 0.0 ? 1.0 : 0.0);
    color += float3(2.4, 0.7, 0.12) * hot * (0.25 + heat) * flicker;
    float crack = pow(saturate(Fbm(local * 3.0 + 7.0, Light ? 2 : 3) * 2.0 + 0.2), 6.0);
    color += float3(1.6, 0.45, 0.1) * crack * soot * fireInside * 0.6;

    o.Color = float4(color, 1.0);
    o.Velocity = ScreenVelocity(input.World);
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// Pierres et rochers posés sur le relief (enfoncés en partie dans le sable).
// ---------------------------------------------------------------------------------------------------------------

struct StoneVertex
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
};

struct StonePixel
{
    float4 Position : SV_Position;
    float3 World : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float3 Local : TEXCOORD2;
    nointerpolation float4 Color : COLOR0;
};

float3 StoneWorld(StoneVertex v, uint instance, out float3 normal, out Stone stone)
{
    stone = Stones[instance + (uint)Battle.y];
    float3 scale = float3(1.0, stone.Color.a, 1.0) * stone.PositionScale.w;
    float3 p = RotateAxis(v.Position * scale, stone.AxisAngle.xyz, stone.AxisAngle.w);
    normal = RotateAxis(normalize(v.Normal / scale), stone.AxisAngle.xyz, stone.AxisAngle.w);
    return p + stone.PositionScale.xyz;
}

StonePixel StoneVS(StoneVertex v, uint instance : SV_InstanceID)
{
    StonePixel o;
    float3 normal;
    Stone stone;
    float3 world = StoneWorld(v, instance, normal, stone);
    o.Position = mul(float4(world, 1.0), ViewProj);
    o.World = world;
    o.Normal = normal;
    o.Local = v.Position * 2.0 + stone.PositionScale.xyz;
    o.Color = stone.Color;
    return o;
}

float4 StoneShadowVS(StoneVertex v, uint instance : SV_InstanceID) : SV_Position
{
    float3 normal;
    Stone stone;
    float3 world = StoneWorld(v, instance, normal, stone);
    return mul(float4(world, 1.0), ShadowViewProj);
}

SceneOut StonePS(StonePixel input)
{
    SceneOut o;
    float3 n = normalize(input.Normal);
    float3 v = normalize(CameraPos - input.World);
    float detail = Fbm(input.Local * 1.7, Light ? 2 : 4);
    float3 albedo = input.Color.rgb * (0.72 + 0.56 * detail);
    // Veines plus claires, lichen sec, sable déposé sur le dessus, bas enterré plus sombre et humide.
    float vein = smoothstep(0.8, 0.95, abs(sin(dot(input.Local, float3(4.1, 6.3, 3.7)) + detail * 4.0)));
    albedo = lerp(albedo, albedo * 1.5 + 0.04, vein * 0.4);
    float lichen = smoothstep(0.62, 0.78, ValueNoise(input.Local * 3.0 + 5.0)) * saturate(n.y + 0.2);
    albedo = lerp(albedo, float3(0.36, 0.33, 0.2), lichen * 0.5);
    float sand = smoothstep(0.55, 0.9, n.y) * (0.5 + 0.5 * ValueNoise(input.Local * 5.0));
    albedo = lerp(albedo, float3(0.52, 0.4, 0.27), sand * 0.3);
    float base = saturate((input.World.y - TerrainHeight(input.World.xz)) / 0.15);
    albedo *= 0.65 + 0.35 * base;
    float roughness = 0.82 - vein * 0.15;

    float shadow = SunShadow(input.World, n) * TerrainShadow(input.World + n * 0.1);
    float3 color = ShadeDirect(albedo, 0.0, roughness, n, v, SunDir, SunColor * 4.0 * shadow);
    color += albedo * lerp(float3(0.1, 0.12, 0.18), float3(0.19, 0.13, 0.08), n.y * 0.5 + 0.5) * 0.55 * (0.6 + 0.4 * base);
    color += ExplosionLight(input.World, n, albedo);
    o.Color = float4(color, 1.0);
    o.Velocity = ScreenVelocity(input.World);
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// Sol vallonné et ciel du soir (une passe plein écran, profondeur écrite pour les objets).
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
    float t;
    if (!MarchTerrain(CameraPos, d, t))
    {
        o.Color = float4(Sky(d), 1.0);
        o.Velocity = DirectionVelocity(d);
        o.Depth = 1.0;
        return o;
    }

    float3 p = CameraPos + d * t;
    float2 xz = p.xz;
    float3 n = TerrainNormal(xz);

    // Petites bosses et rides du sable (dérivée d'un bruit) : elles inclinent la normale sans bouger la surface.
    float near = saturate(1.0 - t / 140.0);
    const float e = 0.08;
    const int octaves = Light ? 2 : 4;
    float b0 = Fbm(float3(xz * 0.7, 1.0), octaves);
    float bx = Fbm(float3((xz + float2(e, 0.0)) * 0.7, 1.0), octaves);
    float bz = Fbm(float3((xz + float2(0.0, e)) * 0.7, 1.0), octaves);
    float ripples = sin(xz.x * 1.9 + xz.y * 0.4 + Fbm(float3(xz * 0.25, 2.0), Light ? 1 : 2) * 6.0);
    n = normalize(n + (float3(-(bx - b0), 0.0, -(bz - b0)) / e * 0.06 + float3(ripples * 0.035, 0.0, 0.0)) * near);

    // Matières : sable clair et terre plus sombre en grandes taches, roche nue sur les pentes, graviers.
    float slope = 1.0 - n.y;
    float patches = Fbm(float3(xz * 0.03, 9.0), 3);
    float small = Fbm(float3(xz * 0.16, 4.0), Light ? 2 : 3);
    float3 sand = lerp(float3(0.56, 0.42, 0.27), float3(0.45, 0.33, 0.21), saturate(patches * 1.6 + 0.5));
    float3 earth = float3(0.3, 0.22, 0.15) * (0.85 + 0.3 * ValueNoise(float3(xz * 0.5, 3.0)));
    float3 gravel = float3(0.38, 0.35, 0.31);
    float earthy = smoothstep(0.05, 0.3, patches + b0 * 0.3 + small * 0.35);
    float3 albedo = lerp(sand, earth, earthy);
    albedo = lerp(albedo, gravel, smoothstep(0.1, 0.3, small - patches * 0.5) * 0.7);
    float rocky = smoothstep(0.05, 0.13, slope + 0.06 * Fbm(float3(xz * 0.4, 2.0), 2));
    float strata = 0.82 + 0.18 * sin(p.y * 9.0 + Fbm(float3(xz * 0.2, 6.0), 2) * 3.0);
    albedo = lerp(albedo, float3(0.42, 0.37, 0.32) * strata, rocky * 0.85);
    // Grain du sol : gravillons de quelques centimètres, plus ou moins clairs (estompés au loin).
    float grit = ValueNoise(float3(xz * 12.0, 1.0));
    albedo *= lerp(1.0, 0.78 + 0.44 * grit, near * near * 0.8);
    albedo *= 0.94 + 0.12 * ripples * near * (1.0 - rocky) * (1.0 - earthy);

    // Graviers et cailloux (bruit cellulaire), plus nombreux sur la roche.
    float stone = 0.0;
    if (!Light && t < 90.0)
    {
        float2 pebbles = Cellular(float3(xz * 2.2, 0.5));
        stone = smoothstep(0.3, 0.16, pebbles.x) * step(0.5 - rocky * 0.3, Hash31(floor(float3(xz * 2.2, 0.5))));
        float3 pebbleColor = lerp(float3(0.32, 0.28, 0.24), float3(0.55, 0.5, 0.44), Hash31(floor(float3(xz * 2.2, 3.0))));
        albedo = lerp(albedo, pebbleColor, stone * 0.85 * near);
    }

    // Traces de chenilles (les chars sont arrivés de loin) et brûlures autour des chars détruits.
    float3 glow = 0.0;
    [loop]
    for (uint i = 0; i < Tanks; i++)
    {
        TankData data = TankList[i];
        float heading = data.PositionHeading.w;
        float3 f = TankForwardFlat(heading);
        float2 side = float2(-f.z, f.x);
        float2 rel = xz - data.PositionHeading.xz;
        float along = dot(rel, f.xz);
        float across = dot(rel, side);
        float travelled = min(TankSpeed * clamp(Battle.x, 0.0, data.Explosion.x), TankTravel) + 2.6;
        if (along > -90.0 && along < travelled && abs(abs(across) - 1.91) < 0.34)
        {
            float tread = step(0.42, frac(along * 6.2));
            float age = saturate(1.0 + along / 90.0);
            float mark = (0.55 + 0.25 * tread) * age * (1.0 - smoothstep(0.26, 0.34, abs(abs(across) - 1.91)));
            albedo = lerp(albedo, albedo * float3(0.62, 0.6, 0.58), mark);
        }

        float since = Battle.x - data.Explosion.x;
        if (since < 0.0)
        {
            continue;
        }

        float r = length(xz - data.Ring.xz);
        float wave = since * 45.0;
        if (r > 13.0 && abs(r - wave) > 8.0)
        {
            continue;
        }

        float scorch = saturate(1.0 - r / (6.0 + 2.0 * Fbm(float3(xz * 0.4, i), 2))) * saturate(since * 3.0);
        albedo = lerp(albedo, float3(0.05, 0.045, 0.04), scorch * 0.9);
        float ringWave = exp(-pow((r - wave) * 0.5, 2.0)) * saturate(1.0 - since * 0.8);
        glow += float3(1.0, 0.75, 0.5) * ringWave * 0.12;
        glow += float3(0.9, 0.25, 0.05) * pow(scorch, 4.0) * saturate(1.0 - since / 20.0) * (0.6 + 0.4 * Fbm(float3(xz * 2.0, Battle.x * 2.0), 2));
    }

    float shadow = SunShadow(p, n) * TerrainShadow(p + n * 0.05);
    float3 v = -d;
    float3 color = ShadeDirect(albedo, 0.0, lerp(0.88, 0.7, stone), n, v, SunDir, SunColor * 4.0 * shadow);
    color += albedo * lerp(float3(0.09, 0.11, 0.17), float3(0.12, 0.12, 0.15), n.y) * 0.75;
    color += ExplosionLight(p, n, albedo) + glow;

    // Brume au loin : le sable et les collines se fondent dans la lumière du soir.
    float haze = 1.0 - exp(-t * 0.0024);
    color = lerp(color, Sky(normalize(float3(d.x, 0.02, d.z))) * 0.85, haze);

    // Mirage : au ras de l'horizon, l'air brûlant au-dessus du sable renvoie le ciel comme un miroir qui tremble.
    float grazing = smoothstep(-0.07, -0.004, d.y) * smoothstep(60.0, 220.0, t);
    float shimmer = Fbm(float3(xz * 0.05, Time * 1.5), Light ? 2 : 3) * 0.025;
    float3 mirrored = Sky(normalize(float3(d.x, abs(d.y) + shimmer, d.z)));
    color = lerp(color, mirrored, grazing * 0.6);

    float4 clip = mul(float4(p, 1.0), ViewProjNoJitter);
    o.Color = float4(color, 1.0);
    o.Velocity = ScreenVelocity(p);
    o.Depth = clip.z / clip.w;
    return o;
}

// ---------------------------------------------------------------------------------------------------------------
// Feu, fumée, étincelles, tirs et obus : chaque particule est une fonction du temps écoulé depuis son événement.
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
static const uint ShellParticles = 10;

// Tirs : chaque char tire toutes les 5 à 8 secondes jusqu'à ce qu'il soit touché (flamme de bouche, fumée, traceur).
bool Shot(uint tank, float time, out float shotTime, out float3 muzzle, out float3 direction)
{
    TankData data = TankList[tank];
    float period = 5.5 + 3.0 * Rand(tank, 31);
    float phase = 1.0 + 4.0 * Rand(tank, 32);
    float since = time - phase;
    shotTime = since - floor(since / period) * period;
    float shotStart = time - shotTime;
    float yaw = TurretYaw(data, shotStart);
    TankPose pose = TankFrame(data, shotStart);
    float3 pivot = float3(TurretPivotX, 0.0, 0.0);
    muzzle = ToWorld(pose, RotateY(float3(5.65, 2.06, 0) - pivot, yaw) + pivot);
    direction = DirectionToWorld(pose, RotateY(float3(1, 0.012, 0), yaw));
    return since >= 0.0 && shotStart < data.Explosion.w;
}

ParticlePixel Hidden()
{
    ParticlePixel o;
    o.Position = float4(0, 0, -1, 1);
    o.Corner = 0;
    o.Color = 0;
    o.Kind = 0;
    return o;
}

ParticlePixel Sprite(float3 position, float size, float4 color, float kind, uint vertex)
{
    ParticlePixel o;
    float4 view = mul(float4(position, 1.0), View);
    float2 corner = QuadCorners[vertex];
    view.xy += corner * size;
    o.Position = mul(view, Proj);
    o.Corner = corner;
    o.Color = color;
    o.Kind = kind;
    return o;
}

ParticlePixel ParticleVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    uint kind = (uint)Battle.z;          // 0 fumée, 1 feu, 2 étincelles, 3 tirs, 4 obus qui arrive
    if (kind == 4)
    {
        // Obus ennemi : un traceur file à 450 m/s et frappe le blindage à l'instant de l'impact.
        uint tank = instance / ShellParticles;
        uint k = instance % ShellParticles;
        TankData data = TankList[tank];
        float before = data.Explosion.w - Battle.x;
        float back = before * 450.0 + k * 1.4;
        if (before < 0.0 || back > 140.0)
        {
            return Hidden();
        }

        float fade = 1.0 - k / (float)ShellParticles;
        return Sprite(data.Impact.xyz - data.ShellWorld.xyz * back, k == 0 ? 0.24 : 0.17, float4(float3(3.4, 2.0, 0.8) * fade, 1.0), 1.0, vertex);
    }

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
            return Hidden();
        }

        if (k == 0)
        {
            // Traceur de l'obus : file à 380 m/s.
            return Sprite(muzzle + direction * 380.0 * shotTime, 0.22, float4(3.2, 1.9, 0.7, 1.0), 1.0, vertex);
        }

        float a = saturate(shotTime / muzzleLife);
        float3 spread = normalize(direction * (2.0 + 2.0 * Rand(shotSeed, 2)) + RandomDirection(shotSeed, 3) * 0.9);
        float3 position = muzzle + Dragged(spread * (6.0 + 14.0 * Rand(shotSeed, 4)), 6.0, shotTime, 0.6);
        float4 color = float4(lerp(float3(1.4, 0.9, 0.35), float3(0.02, 0.018, 0.015), saturate(a * 3.0)), 1.0 - a);
        return Sprite(position, 0.2 + 1.2 * sqrt(a), color, 1.0, vertex);
    }

    uint perTank = (uint)(kind == 0 ? Counts.z : (kind == 1 ? Counts.y : Counts.w));
    uint tank = instance / perTank;
    uint piece = instance % perTank;
    uint seed = instance * 3u + kind * 0x51ED27u;
    TankData data = TankList[tank];
    float t = Battle.x - data.Explosion.x;
    float3 origin = data.Ring.xyz + float3(0, 0.4, 0);
    float3 position;
    float size;
    float4 color;
    float life;

    if (kind == 1)
    {
        // Boule de feu : jaillit de la couronne de la tourelle, monte et s'assombrit.
        life = 1.2 + 2.2 * Rand(seed, 1);
        if (t < 0.0 || t > life)
        {
            return Hidden();
        }

        float3 dir = RandomDirection(seed, 2);
        dir.y = abs(dir.y) * 1.2 + 0.3;
        float speed = 10.0 + 22.0 * Rand(seed, 4);
        position = origin + Dragged(normalize(dir) * speed, 2.8, t, 3.5);
        float a = saturate(t / life);
        size = 0.5 + 2.8 * sqrt(a) * (0.6 + Rand(seed, 5));
        // Des milliers de flammèches se superposent : chacune ne porte qu'une petite part de la lumière.
        float3 hotColor = lerp(float3(0.09, 0.062, 0.028), float3(0.07, 0.02, 0.005), saturate(a * 2.0));
        color = float4(lerp(hotColor, float3(0.008, 0.002, 0.0005), saturate(a * 1.6 - 0.6)) * Gains.x * 0.55 * smoothstep(0.0, 0.12, t), 1.0 - a);
    }
    else if (kind == 2)
    {
        // Étincelles : la moitié jaillit du trou de l'obus à l'impact, l'autre moitié de l'explosion des munitions.
        bool impact = piece < perTank / 4u;
        float since = impact ? Battle.x - data.Explosion.w : t;
        life = impact ? 0.25 + 0.8 * Rand(seed, 1) : 0.8 + 2.0 * Rand(seed, 1);
        if (since < 0.0 || since > life)
        {
            return Hidden();
        }

        float3 dir = impact
            ? normalize(RandomDirection(seed, 2) + float3(0, 0.9, 0) - data.ShellWorld.xyz * 0.35 * Rand(seed, 6))
            : normalize(RandomDirection(seed, 2) + float3(0, 0.6, 0));
        float speed = impact ? 4.0 + 14.0 * Rand(seed, 4) : 18.0 + 40.0 * Rand(seed, 4);
        float firstHit, settled;
        position = BallisticTerrain(impact ? data.Impact.xyz : origin, dir * speed, since * 0.9, 0.02, 0.3, firstHit, settled);
        size = 0.05 + 0.07 * Rand(seed, 5);
        float a = saturate(since / life);
        color = float4(float3(1.6, 0.7, 0.2) * (1.0 - a), 1.0 - a);
        t = since;
    }
    else
    {
        // Fumée : d'abord un filet qui sort du trou de l'obus, puis la colonne qui monte de la couronne de la tourelle,
        // s'étale et dérive avec le vent pendant longtemps.
        bool fromHole = piece < perTank / 8u;
        float delay = fromHole ? 0.05 + 0.4 * Rand(seed, 6) : 0.2 + 1.5 * Rand(seed, 6);
        float local = (fromHole ? Battle.x - data.Explosion.w : t) - delay;
        life = fromHole ? 6.0 + 6.0 * Rand(seed, 1) : 22.0 + 26.0 * Rand(seed, 1);
        if (local < 0.0 || local > life)
        {
            return Hidden();
        }

        float3 start = fromHole ? data.Impact.xyz : origin;
        float3 dir = RandomDirection(seed, 2);
        dir.y = abs(dir.y);
        float speed = fromHole ? 0.6 + 1.5 * Rand(seed, 4) : 2.0 + 7.0 * Rand(seed, 4);
        float rise = (fromHole ? 3.0 + 6.0 * Rand(seed, 9) : 8.0 + 30.0 * Rand(seed, 9)) * (1.0 - exp(-max(local, 0.0) / 9.0));
        float3 drift = float3(0.35, 0, 0.12) * local * (0.3 + rise / 30.0);
        float swirl = Rand(seed, 7) * TAU + local * (0.2 + 0.3 * Rand(seed, 8));
        position = start + Dragged(dir * speed * 0.5, 1.5, max(local, 0.0), 0.0)
            + float3(0, rise, 0) + drift
            + float3(cos(swirl), 0, sin(swirl)) * min(local, 15.0) * (0.08 + rise / 160.0);
        float a = saturate(local / life);
        size = (fromHole ? 0.35 + 1.4 * sqrt(a) : 0.8 + 2.7 * sqrt(a)) + rise * 0.055;
        // Fumée noire à la base, plus grise en hauteur.
        float shade = lerp(0.035, 0.16, saturate(rise / 30.0)) * (0.8 + 0.4 * Rand(seed, 10));
        color = float4(shade.xxx, min(0.42 * Gains.y, 0.85) * smoothstep(0.0, 0.08, a) * (1.0 - a));
        t = local;
    }

    if (t < 0.0 || t > life)
    {
        return Hidden();
    }

    return Sprite(position, size, color, (float)kind, vertex);
}

float4 SmokePS(ParticlePixel input) : SV_Target
{
    // Bouffée en volume : bord effiloché par un bruit, cœur plus épais et plus sombre, côté du soleil couchant plus clair,
    // lueur du feu par-dessous.
    float r2 = dot(input.Corner, input.Corner);
    float seed = input.Color.r * 97.0 + input.Color.a * 13.0;
    float billow = Fbm(float3(input.Corner * 1.8, seed), Light ? 1 : 2);
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
