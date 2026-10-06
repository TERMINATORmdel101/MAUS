// Benchmark MAUS, test « Bande passante » : collision de deux galaxies. Des millions d'étoiles et de nuages de gaz
// sont déplacés par la carte à chaque image (problème restreint à trois corps, comme les simulations de Toomre et
// Toomre en 1972 : les étoiles subissent l'attraction des deux noyaux), puis dessinés en points lumineux et en grands
// voiles de gaz mélangés par addition dans une image HDR : la mémoire vidéo et les unités de mélange travaillent à fond.
#include "common.hlsli"

struct Particle
{
    float4 Position;    // xyz, w = éclat
    float4 Velocity;    // xyz, w = genre (0 étoile du disque, 1 étoile du bulbe, 2 gaz)
};

cbuffer GalaxyConstants : register(b1)
{
    float4 CoreA;           // position du noyau A, masse
    float4 CoreB;           // position du noyau B, masse
    float4 Simulation;      // pas de temps, adoucissement², nombre d'étoiles, nombre total de particules
    float4 Look;            // taille des étoiles (pixels), taille du gaz (unités du monde), intensité du gaz, intensité des étoiles
    float4 InitA;           // vitesse initiale du noyau A (xyz), inclinaison (w, radians)
    float4 InitB;           // vitesse initiale du noyau B (xyz), inclinaison (w, radians)
};

RWStructuredBuffer<Particle> Particles : register(u0);
StructuredBuffer<Particle> ParticlesRead : register(t0);

float3x3 Tilt(float angle, float spin)
{
    float c = cos(angle), s = sin(angle), cs = cos(spin), ss = sin(spin);
    float3x3 tilt = float3x3(1, 0, 0, 0, c, -s, 0, s, c);
    float3x3 turn = float3x3(cs, 0, ss, 0, 1, 0, -ss, 0, cs);
    return mul(tilt, turn);
}

float Random(uint index, uint salt)
{
    return HashU(index * 0x9E3779B9u ^ HashU(salt * 0x85EBCA6Bu + 0x68E31DA4u)) * (1.0 / 4294967296.0);
}

// Disques galactiques : rayons en loi exponentielle, bras spiraux pour le gaz et les jeunes étoiles, bulbe central.
[numthreads(256, 1, 1)]
void InitCS(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    uint total = (uint)Simulation.w;
    if (i >= total)
    {
        return;
    }

    uint stars = (uint)Simulation.z;
    bool gas = i >= stars;
    bool second = (gas ? (i - stars) : i) % 5 >= 3;     // 60 % dans la galaxie A, 40 % dans la B
    float4 core = second ? CoreB : CoreA;
    float4 init = second ? InitB : InitA;
    float mass = core.w;

    float u = Random(i, 1), v = Random(i, 2), w = Random(i, 3), q = Random(i, 4);
    float scale = second ? 0.9 : 1.15;
    float radius;
    float kind;
    float3 local;
    float bulge = !gas && u < 0.18;
    if (bulge)
    {
        // Bulbe : boule d'étoiles anciennes, plus dense au centre.
        radius = 0.06 + 0.45 * pow(v, 2.2);
        float theta = TAU * w;
        float phi = acos(2.0 * q - 1.0);
        local = radius * float3(sin(phi) * cos(theta), 0.55 * cos(phi), sin(phi) * sin(theta));
        kind = 1;
    }
    else
    {
        radius = 0.22 - scale * log(1.0 - v * (1.0 - exp(-4.2 / scale)));
        float theta = TAU * w;
        if (gas || q < 0.45)
        {
            // Bras spiraux logarithmiques : le gaz et les étoiles jeunes s'y regroupent.
            float arm = floor(Random(i, 5) * 2.0) * PI;
            theta = arm + 1.9 * log(radius) + 0.35 * (Random(i, 6) - 0.5) * (1.0 + radius * 0.3);
        }
        float thickness = (gas ? 0.012 : 0.03) * (1.0 + radius) * (Random(i, 7) + Random(i, 8) - 1.0);
        local = float3(radius * cos(theta), thickness, radius * sin(theta));
        kind = gas ? 2 : 0;
    }

    // Vitesse circulaire autour d'un noyau adouci (potentiel de Plummer).
    float r2 = dot(local.xz, local.xz) + Simulation.y;
    float speed = sqrt(mass * dot(local.xz, local.xz) / pow(r2, 1.5));
    float3 tangent = normalize(float3(-local.z, 0, local.x) + 1e-6);
    float3 velocity = tangent * speed * (second ? -1.0 : 1.0) * (bulge ? 0.35 : 1.0);
    if (bulge)
    {
        velocity += (float3(Random(i, 9), Random(i, 10), Random(i, 11)) - 0.5) * 0.5 * sqrt(mass / max(radius, 0.08));
    }

    float3x3 frame = Tilt(init.w, second ? 1.1 : 0.2);
    float3 position = mul(local, frame) + core.xyz;
    velocity = mul(velocity, frame) + init.xyz;

    Particle p;
    float brightness = gas ? (0.6 + 0.8 * Random(i, 12)) : (bulge ? 0.25 + 0.5 * Random(i, 12) : 0.4 + 1.8 * pow(Random(i, 12), 3.0));
    p.Position = float4(position, brightness);
    p.Velocity = float4(velocity, kind + (second ? 0.5 : 0.0));
    Particles[i] = p;
}

// Un pas de temps : attraction des deux noyaux (adoucie), intégration d'Euler semi-implicite.
[numthreads(256, 1, 1)]
void UpdateCS(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= (uint)Simulation.w)
    {
        return;
    }

    Particle p = Particles[i];
    float3 da = CoreA.xyz - p.Position.xyz;
    float3 db = CoreB.xyz - p.Position.xyz;
    float ra = dot(da, da) + Simulation.y;
    float rb = dot(db, db) + Simulation.y;
    float3 acceleration = da * (CoreA.w / (ra * sqrt(ra))) + db * (CoreB.w / (rb * sqrt(rb)));
    p.Velocity.xyz += acceleration * Simulation.x;
    p.Position.xyz += p.Velocity.xyz * Simulation.x;
    Particles[i] = p;
}

// ---------------------------------------------------------------------------------------------------------------
// Dessin : chaque particule devient un carré tourné vers la caméra (6 sommets générés sans tampon de sommets).
// ---------------------------------------------------------------------------------------------------------------

struct SpriteOut
{
    float4 Position : SV_Position;
    float2 Corner : TEXCOORD0;
    float3 Color : COLOR0;
};

float3 StarColor(float kind, float brightness, float seed)
{
    // Étoiles anciennes du bulbe dorées, jeunes étoiles du disque bleues, quelques géantes rouges.
    float3 young = float3(0.5, 0.68, 1.0);
    float3 old = float3(1.0, 0.7, 0.38);
    float3 red = float3(1.0, 0.4, 0.25);
    float3 color = frac(kind) > 0.25 ? lerp(young, float3(0.9, 0.85, 1.0), 0.3) : young;
    color = floor(kind) == 1 ? old : color;
    color = seed > 0.93 ? red : color;
    return color * brightness;
}

static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };

SpriteOut StarVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    SpriteOut o;
    Particle p = ParticlesRead[instance];
    float4 clip = mul(float4(p.Position.xyz, 1.0), ViewProj);
    float2 corner = Corners[vertex];
    // Taille fixe à l'écran (en pixels) : une étoile reste un point net, même de près.
    float size = Look.x * (0.7 + 0.6 * saturate(p.Position.w));
    clip.xy += corner * size * InvResolution * 2.0 * clip.w;
    o.Position = clip;
    o.Corner = corner;
    float seed = Hash11((float)instance);
    o.Color = StarColor(p.Velocity.w, p.Position.w, seed) * Look.w;
    // Les étoiles derrière la caméra sont rejetées.
    if (clip.w <= 0.0)
    {
        o.Position = float4(0, 0, -1, 1);
    }
    return o;
}

float4 StarPS(SpriteOut input) : SV_Target
{
    float r2 = dot(input.Corner, input.Corner);
    float glow = exp(-r2 * 4.0);
    return float4(input.Color * glow, 0.0);
}

SpriteOut GasVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    SpriteOut o;
    uint index = (uint)Simulation.z + instance;
    Particle p = ParticlesRead[index];
    float4 view = mul(float4(p.Position.xyz, 1.0), View);
    float2 corner = Corners[vertex];
    // Voiles de gaz : taille fixe dans l'espace (grands près de la caméra : beaucoup de pixels superposés).
    float size = Look.y * (0.6 + 0.8 * Hash11((float)index * 1.37));
    view.xy += corner * size;
    float4 clip = mul(view, Proj);
    o.Position = view.z < -0.05 ? clip : float4(0, 0, -1, 1);
    o.Corner = corner;
    float seed = Hash11((float)index);
    // Régions de formation d'étoiles roses (rares, vives), nébuleuses bleues, poussière chaude.
    float3 pink = float3(1.0, 0.22, 0.5) * 2.2;
    float3 blue = float3(0.25, 0.45, 1.0);
    float3 dust = float3(0.85, 0.45, 0.22) * 0.8;
    float3 color = seed < 0.18 ? pink : (seed < 0.62 ? blue : dust);
    // Le gaz pressé au passage de l'autre galaxie s'allume (zones de formation d'étoiles) : on éclaire selon la vitesse.
    float speed = length(p.Velocity.xyz);
    o.Color = color * Look.z * p.Position.w * (0.6 + 0.8 * saturate(speed * 0.6));
    return o;
}

float4 GasPS(SpriteOut input) : SV_Target
{
    float r2 = dot(input.Corner, input.Corner);
    float soft = saturate(1.0 - r2);
    soft *= soft;
    return float4(input.Color * soft, 0.0);
}

// Fond : ciel d'étoiles lointaines et nébuleuse très légère.
struct BackgroundOut
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
};

BackgroundOut BackgroundPS(FullscreenOut input)
{
    BackgroundOut o;
    float3 direction = RayDirection(input.UV);
    float3 color = float3(0.002, 0.003, 0.006);
    float nebula = Fbm(direction * 2.2 + 3.0, 5);
    color += float3(0.04, 0.012, 0.05) * pow(saturate(nebula + 0.3), 3.0);
    color += float3(0.01, 0.025, 0.05) * pow(saturate(Fbm(direction * 4.0 - 7.0, 4) + 0.25), 4.0);
    float3 cell = floor(direction * 420.0);
    float star = Hash31(cell);
    color += star > 0.9985 ? (0.4 + 2.0 * Hash31(cell + 7.0)) * float3(0.8, 0.85, 1.0) : 0.0;
    o.Color = float4(color, 1.0);
    o.Velocity = DirectionVelocity(direction);
    return o;
}
