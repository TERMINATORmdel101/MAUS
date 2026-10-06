// Benchmark MAUS, test « Galerie des glaces » (lancer de rayons matériel). Une galerie baroque de 40 m : dix fenêtres
// face à dix arcades de miroirs, deux grands miroirs aux extrémités qui se renvoient la salle à l'infini, statues en or,
// en chrome, en verre et en marbre, six lustres de cristal, sol de marbre ciré. Chaque pixel lance lui-même ses rayons
// (requêtes de rayons de DirectX Raytracing 1.1, modèle de shader 6.5, compilé par DXC) dans la structure d'accélération
// que la carte a construite : rayon de vue, reflets en cascade (miroirs, métaux, sol et marbres vernis), réfraction dans
// le verre et le cristal, ombres douces du soleil, lumière renvoyée par les murs (éclairage global, un rebond), rayons
// de soleil dans la poussière. Rien n'est précalculé : tout est retrouvé rayon par rayon, à chaque image.
// Shader écrit pour MAUS (GPL-3.0). Formules classiques citées là où elles servent.
#include "common.hlsli"

struct RtVertex
{
    float3 Position;
    float3 Normal;
};

// Une ligne par partie de maillage placé : où trouver ses sommets et ses indices, et de quoi elle est faite. L'instance
// donne la première ligne, la partie touchée (GeometryIndex) le décalage.
struct RtInstance
{
    uint FirstVertex;
    uint FirstIndex;
    uint Material;
    uint Padding;
};

RaytracingAccelerationStructure Scene : register(t0);
StructuredBuffer<RtVertex> Vertices : register(t1);
StructuredBuffer<uint> Indices : register(t2);
StructuredBuffer<RtInstance> Instances : register(t3);

// Masques des instances : les rayons d'ombre ne voient que les objets opaques (le verre laisse passer le soleil),
// les flammes ne sont vues que par la caméra et les reflets.
static const uint MaskSolid = 1;
static const uint MaskGlass = 2;
static const uint MaskFlame = 4;

// Matières (même numérotation que MirrorHallScene.Material en C#).
static const uint MatFloor = 0;
static const uint MatStucco = 1;
static const uint MatPilaster = 2;
static const uint MatGilt = 3;
static const uint MatMirror = 4;
static const uint MatVault = 5;
static const uint MatFrame = 6;
static const uint MatGlass = 7;
static const uint MatBlueGlass = 8;
static const uint MatChrome = 9;
static const uint MatStatueMarble = 10;
static const uint MatCrystal = 11;
static const uint MatFlame = 12;
static const uint MatWax = 13;
static const uint MatPedestal = 14;
static const uint MatGold = 15;

static const uint KindDiffuse = 0;   // mat : lumière diffuse et reflet du soleil
static const uint KindVarnish = 1;   // diffus sous un vernis qui reflète la salle (sol, marbres polis)
static const uint KindMetal = 2;     // métal : tout est reflet, teinté
static const uint KindGlass = 3;     // verre : reflet et réfraction (loi de Snell, Fresnel)
static const uint KindLight = 4;     // flamme : émet sa propre lumière

// Travail par pixel : le mode léger (720p, cartes intégrées) garde la même image avec beaucoup moins de rayons.
static const int ShadowRays = Light ? 1 : 4;
static const int BounceRays = Light ? 2 : 8;
static const int MaxDepth = Light ? 4 : 10;
static const int ShaftSteps = Light ? 4 : 24;

// Mode léger : deux rayons de rebond seulement ; leur résultat est mélangé à l'ambiance moyenne de la salle et les points
// très lumineux (ciel vu par une fenêtre) sont plafonnés plus bas, pour deux fois moins de grain.
static const float BounceWeight = Light ? 0.5 : 1.0;
static const float BounceCeiling = Light ? 1.5 : 3.0;

static const float VaultBase = 7.35;
static const float3 HallGlow = float3(0.42, 0.36, 0.28);

// ---------------------------------------------------------------------------------------------------------------
// Hasard : une suite par pixel et par image (l'anticrénelage temporel moyenne les tirages d'une image à l'autre).
// ---------------------------------------------------------------------------------------------------------------

static uint RandomState;

float Random()
{
    RandomState = HashU(RandomState);
    return RandomState * (1.0 / 4294967296.0);
}

float2 Random2()
{
    return float2(Random(), Random());
}

// Tirage étagé : la k-ième de n directions, décalée au hasard pour ce pixel et cette image (réseau de rang 1 de
// Fibonacci). Les n rayons couvrent régulièrement l'hémisphère ou le disque du soleil : bien moins de grain qu'avec des
// tirages indépendants.
float2 Stratified(float2 offset, int k, int n)
{
    return frac(offset + float2((k + 0.5) / n, k * 0.61803399));
}

// Repère orthonormé autour d'une direction (J. R. Frisvad, « Building an Orthonormal Basis from a 3D Unit Vector
// Without Normalization », 2012, avec la correction de signe de Duff et al., 2017).
void Basis(float3 n, out float3 t, out float3 b)
{
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float c = n.x * n.y * a;
    t = float3(1.0 + s * n.x * n.x * a, s * c, -s * n.x);
    b = float3(c, s + n.y * n.y * a, -n.y);
}

// Direction tirée dans un cône (disque du soleil vu depuis la salle), répartition uniforme sur l'angle solide.
float3 ConeSample(float3 axis, float cosMax, float2 u)
{
    float c = lerp(1.0, cosMax, u.x);
    float s = sqrt(saturate(1.0 - c * c));
    float phi = TAU * u.y;
    float3 t, b;
    Basis(axis, t, b);
    return normalize(axis * c + (t * cos(phi) + b * sin(phi)) * s);
}

// Direction tirée selon le cosinus autour de la normale (rebond diffus, loi de Lambert).
float3 CosineSample(float3 n, float2 u)
{
    float r = sqrt(u.x);
    float phi = TAU * u.y;
    float3 t, b;
    Basis(n, t, b);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(saturate(1.0 - u.x)));
}

// ---------------------------------------------------------------------------------------------------------------
// Rayons.
// ---------------------------------------------------------------------------------------------------------------

struct Hit
{
    float T;
    float3 Position;
    float3 Normal;       // normale lissée, tournée vers le rayon
    float3 Geometric;    // normale du triangle, tournée vers le rayon
    uint Material;
    bool Front;          // le rayon arrive par l'extérieur de la surface (entrée dans le verre)
};

// Premier triangle touché : sommets relus dans les tampons de la scène, normale interpolée, matière de l'instance.
bool TraceClosest(float3 origin, float3 direction, float tMax, uint mask, out Hit hit)
{
    hit = (Hit)0;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = tMax;
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    query.TraceRayInline(Scene, RAY_FLAG_NONE, mask, ray);
    query.Proceed();
    if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
    {
        return false;
    }

    RtInstance info = Instances[query.CommittedInstanceID() + query.CommittedGeometryIndex()];
    uint first = info.FirstIndex + query.CommittedPrimitiveIndex() * 3;
    RtVertex a = Vertices[info.FirstVertex + Indices[first]];
    RtVertex b = Vertices[info.FirstVertex + Indices[first + 1]];
    RtVertex c = Vertices[info.FirstVertex + Indices[first + 2]];
    float2 bary = query.CommittedTriangleBarycentrics();
    float3x3 toWorld = (float3x3)query.CommittedObjectToWorld3x4();
    float3 smooth = a.Normal * (1.0 - bary.x - bary.y) + b.Normal * bary.x + c.Normal * bary.y;
    float3 geometric = normalize(mul(toWorld, cross(b.Position - a.Position, c.Position - a.Position)));
    smooth = normalize(mul(toWorld, smooth));
    hit.T = query.CommittedRayT();
    hit.Position = origin + direction * hit.T;
    hit.Front = dot(geometric, direction) < 0.0;
    hit.Geometric = hit.Front ? geometric : -geometric;
    smooth = hit.Front ? smooth : -smooth;
    // Normale lissée du même côté que le triangle (silhouettes des modèles).
    hit.Normal = dot(smooth, hit.Geometric) > 0.0 ? smooth : hit.Geometric;
    hit.Material = info.Material;
    return true;
}

// Rayon d'ombre : arrêté au premier objet opaque rencontré, sans chercher le plus proche.
bool Visible(float3 origin, float3 direction, float tMax)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = tMax;
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> query;
    query.TraceRayInline(Scene, RAY_FLAG_NONE, MaskSolid, ray);
    query.Proceed();
    return query.CommittedStatus() == COMMITTED_NOTHING;
}

// ---------------------------------------------------------------------------------------------------------------
// Matières, calculées à partir de la position (aucune texture).
// ---------------------------------------------------------------------------------------------------------------

struct Surface
{
    uint Kind;
    float3 Albedo;      // couleur diffuse, ou teinte du métal et du verre
    float Roughness;
    float Ior;
    float3 Emission;
};

// Veines de marbre : sinus déformé par un bruit fractal, affiné par une puissance.
float Veins(float3 p, float scale, float sharpness)
{
    float n = Fbm(p * scale, Light ? 3 : 5);
    return pow(saturate(1.0 - abs(sin((p.x * 0.8 + p.y * 1.3 + p.z * 1.1) * scale + n * 7.0))), sharpness);
}

// Damier de marbre posé en diagonale (carreaux de 0,9 m) : blanc de Carrare veiné de gris, vert antique.
float3 FloorAlbedo(float3 p)
{
    float2 q = float2(p.x + p.z, p.x - p.z) * (0.70710678 / 0.9);
    float2 cell = floor(q);
    float2 f = frac(q);
    float joint = smoothstep(0.0, 0.01, min(min(f.x, 1.0 - f.x), min(f.y, 1.0 - f.y)));
    float vein = Veins(p + float3(cell.x * 3.1, 0.0, cell.y * 1.7), 1.8, 9.0);
    float cloud = Fbm(p * 2.3 + 11.0, Light ? 2 : 3) * 0.5 + 0.5;
    bool dark = ((int)(cell.x + cell.y) & 1) != 0;
    float3 white = lerp(float3(0.86, 0.85, 0.82), float3(0.5, 0.51, 0.53), vein * 0.75) * (0.92 + 0.08 * cloud);
    float3 green = lerp(float3(0.05, 0.1, 0.08), float3(0.45, 0.55, 0.48), vein * 0.55) * (0.75 + 0.35 * cloud);
    return lerp(float3(0.22, 0.21, 0.19), dark ? green : white, joint);
}

// Marbre rouge des pilastres (veines claires sur fond brun rouge).
float3 RedMarble(float3 p)
{
    float vein = Veins(p, 2.6, 6.0);
    float cloud = Fbm(p * 4.0 + 3.0, Light ? 2 : 3) * 0.5 + 0.5;
    return lerp(float3(0.36, 0.09, 0.07) * (0.7 + 0.5 * cloud), float3(0.78, 0.66, 0.6), vein * 0.8);
}

// Marbre vert sombre des socles.
float3 GreenMarble(float3 p)
{
    float vein = Veins(p, 3.4, 7.0);
    float cloud = Fbm(p * 5.0 + 9.0, Light ? 2 : 3) * 0.5 + 0.5;
    return lerp(float3(0.03, 0.08, 0.06) * (0.6 + 0.6 * cloud), float3(0.55, 0.68, 0.6), vein * 0.7);
}

// Stuc des murs : crème chaud, à peine nuancé.
float3 StuccoAlbedo(float3 p)
{
    return float3(0.8, 0.72, 0.6) * (0.94 + 0.06 * Fbm(p * 1.5, 2));
}

// Voûte peinte : travées de 5 m encadrées de dorures ; au centre un ciel nuageux en trompe-l'œil, sur les côtés des
// tableaux aux tons chauds (grandes masses de couleur, plus sombres vers leur cadre, comme une peinture ancienne).
float3 VaultAlbedo(float3 p)
{
    float angle = atan2(p.y - VaultBase, p.z);
    float2 f = frac(float2(p.x / 5.0, angle / PI * 3.0));
    float band = floor(angle / PI * 3.0);
    float edge = min(min(f.x, 1.0 - f.x), min(f.y, 1.0 - f.y) * 1.7);
    float frame = 1.0 - smoothstep(0.025, 0.05, edge);
    float clouds = saturate(Fbm(float3(p.x * 0.3, angle * 1.8, 1.7), Light ? 3 : 5) * 1.1 + 0.4);
    float3 sky = lerp(float3(0.3, 0.46, 0.74), float3(0.96, 0.9, 0.84), clouds);
    float scene = saturate(Fbm(float3(p.x * 0.45, angle * 2.6, band * 5.3), Light ? 3 : 4) * 1.2 + 0.5);
    float figures = saturate(Fbm(float3(p.x * 1.3, angle * 7.0, band * 2.1 + 8.0), 3) * 1.5 + 0.35);
    float3 painting = lerp(float3(0.32, 0.17, 0.1), float3(0.86, 0.66, 0.42), scene);
    painting = lerp(painting, float3(0.82, 0.6, 0.5), smoothstep(0.55, 0.8, figures) * 0.6);
    painting *= 0.55 + 0.45 * smoothstep(0.05, 0.22, edge);
    return lerp(band == 1.0 ? sky : painting, float3(0.9, 0.68, 0.3), frame);
}

// Matière d'un point. Sans détail (rebonds de la lumière, reflets flous), les marbres et la voûte gardent leur couleur
// moyenne : leurs veines ne se voient pas dans une lumière renvoyée, et des pixels voisins qui touchent des matières
// différentes n'exécutent pas chacun tous les bruits.
Surface SurfaceAt(uint material, float3 p, bool detailed)
{
    Surface s;
    s.Kind = KindDiffuse;
    s.Albedo = 0.8;
    s.Roughness = 0.6;
    s.Ior = 1.5;
    s.Emission = 0.0;
    switch (material)
    {
    case MatFloor:
        s.Kind = KindVarnish;
        s.Albedo = detailed ? FloorAlbedo(p) : float3(0.45, 0.47, 0.44);
        s.Roughness = 0.06;
        break;
    case MatStucco:
        s.Albedo = detailed ? StuccoAlbedo(p) : float3(0.8, 0.72, 0.6);
        s.Roughness = 0.8;
        break;
    case MatPilaster:
        s.Kind = KindVarnish;
        s.Albedo = detailed ? RedMarble(p) : float3(0.42, 0.18, 0.15);
        s.Roughness = 0.12;
        break;
    case MatGilt:
        s.Kind = KindMetal;
        s.Albedo = float3(0.96, 0.74, 0.38);
        s.Roughness = 0.08;
        break;
    case MatMirror:
        s.Kind = KindMetal;
        s.Albedo = float3(0.84, 0.85, 0.82);
        s.Roughness = 0.0;
        break;
    case MatVault:
        s.Albedo = detailed ? VaultAlbedo(p) : float3(0.62, 0.55, 0.45);
        s.Roughness = 0.9;
        break;
    case MatFrame:
        s.Albedo = float3(0.82, 0.8, 0.74);
        s.Roughness = 0.5;
        break;
    case MatGlass:
        s.Kind = KindGlass;
        s.Albedo = float3(0.96, 0.99, 0.97);
        break;
    case MatBlueGlass:
        s.Kind = KindGlass;
        s.Albedo = float3(0.5, 0.72, 0.95);
        s.Ior = 1.52;
        break;
    case MatChrome:
        s.Kind = KindMetal;
        s.Albedo = float3(0.8, 0.81, 0.82);
        s.Roughness = 0.0;
        break;
    case MatStatueMarble:
        s.Albedo = float3(0.86, 0.84, 0.8);
        s.Roughness = 0.35;
        break;
    case MatCrystal:
        s.Kind = KindGlass;
        s.Albedo = 0.99;
        s.Ior = 1.62;
        break;
    case MatFlame:
        s.Kind = KindLight;
        s.Emission = float3(16.0, 8.5, 3.0);
        break;
    case MatWax:
        s.Albedo = float3(0.92, 0.9, 0.82);
        s.Roughness = 0.5;
        break;
    case MatPedestal:
        s.Kind = KindVarnish;
        s.Albedo = detailed ? GreenMarble(p) : float3(0.08, 0.14, 0.11);
        s.Roughness = 0.08;
        break;
    case MatGold:
        s.Kind = KindMetal;
        s.Albedo = float3(1.0, 0.77, 0.33);
        s.Roughness = 0.04;
        break;
    }

    return s;
}

// ---------------------------------------------------------------------------------------------------------------
// Dehors : ciel d'après-midi et jardins vus par les fenêtres.
// ---------------------------------------------------------------------------------------------------------------

// Ciel ; le disque du soleil seulement pour la vue directe et les reflets nets (ailleurs, la lumière directe le compte
// déjà : le tirer au hasard ferait des points brillants isolés).
float3 Sky(float3 d, bool sunDisk)
{
    float h = saturate(d.y);
    float3 sky = lerp(float3(1.0, 0.86, 0.68) * 1.9, float3(0.32, 0.5, 0.92) * 1.4, pow(h, 0.45));
    float sun = dot(d, SunDir);
    sky += SunColor * (0.05 * pow(saturate(sun), 12.0));
    if (sunDisk)
    {
        sky += SunColor * 0.5 * pow(saturate(sun), 400.0);
        if (sun > cos(Params0.x))
        {
            sky += SunColor * 60.0;
        }
    }

    return sky;
}

float3 Outside(float3 origin, float3 d, bool sunDisk)
{
    if (d.y > -0.003)
    {
        return Sky(d, sunDisk);
    }

    // Parterre à la française : pelouse rayée, allées de gravier, bordures de buis, éclairés par le soleil.
    float t = -origin.y / d.y;
    float3 p = origin + d * t;
    float stripe = step(0.5, frac(p.x * 0.2));
    float3 lawn = lerp(float3(0.11, 0.24, 0.06), float3(0.15, 0.3, 0.08), stripe);
    float2 garden = abs(frac(float2(p.x / 14.0, (p.z - 6.0) / 9.0)) - 0.5);
    float alley = step(min(garden.x * 14.0, garden.y * 9.0), 0.9);
    float hedge = step(abs(min(garden.x * 14.0, garden.y * 9.0) - 1.05), 0.15);
    float3 ground = lerp(lawn, float3(0.62, 0.56, 0.45), alley);
    ground = lerp(ground, float3(0.05, 0.14, 0.04), hedge) * (sunDisk ? 0.8 + 0.3 * Fbm(p * 0.5, 2) : 0.95);
    float3 lit = ground * (SunColor * saturate(SunDir.y) + float3(0.35, 0.45, 0.6));
    float haze = 1.0 - exp(-t * 0.006);
    return lerp(lit, Sky(normalize(float3(d.x, 0.03, d.z)), false), haze);
}

// ---------------------------------------------------------------------------------------------------------------
// Lumière.
// ---------------------------------------------------------------------------------------------------------------

// Soleil : ombre douce par plusieurs rayons tirés vers des points du disque solaire, puis diffus et reflet (GGX).
float3 SunLight(float3 p, float3 ng, float3 n, float3 v, Surface s, int rays)
{
    if (dot(n, SunDir) <= 0.0 || dot(ng, SunDir) <= 0.0)
    {
        return 0.0;
    }

    float cosMax = cos(Params0.x);
    float visible = 0.0;
    float2 offset = Random2();
    [loop]
    for (int i = 0; i < rays; i++)
    {
        visible += Visible(p + ng * 0.003, ConeSample(SunDir, cosMax, Stratified(offset, i, rays)), 300.0) ? 1.0 : 0.0;
    }

    if (visible <= 0.0)
    {
        return 0.0;
    }

    return ShadeDirect(s.Albedo, 0.0, max(s.Roughness, 0.08), n, v, SunDir, SunColor) * (visible / rays);
}

// Lumière ambiante d'un point vu dans un reflet ou au bout d'un rebond : la teinte moyenne de la galerie.
float3 AmbientGuess(float3 n)
{
    return lerp(HallGlow * 0.7, HallGlow * 1.3, n.y * 0.5 + 0.5);
}

// Éclairage global, un rebond : rayons tirés selon le cosinus ; chaque point touché renvoie le soleil qu'il reçoit
// (un rayon d'ombre) et l'ambiance de la salle ; par les fenêtres, le ciel et les jardins.
float3 Bounce(float3 p, float3 ng, float3 n)
{
    float3 sum = 0.0;
    float2 offset = Random2();
    [loop]
    for (int i = 0; i < BounceRays; i++)
    {
        float3 d = CosineSample(n, Stratified(offset, i, BounceRays));
        if (dot(d, ng) <= 0.0)
        {
            d = reflect(d, ng);
        }

        Hit h;
        if (!TraceClosest(p + ng * 0.003, d, 60.0, MaskSolid | MaskGlass, h))
        {
            sum += min(Outside(p, d, false), BounceCeiling);
            continue;
        }

        Surface s = SurfaceAt(h.Material, h.Position, false);
        if (s.Kind == KindGlass)
        {
            sum += AmbientGuess(h.Normal);
            continue;
        }

        float3 albedo = s.Kind == KindMetal ? s.Albedo * 0.35 : s.Albedo;
        float sun = 0.0;
        float lambert = dot(h.Normal, SunDir);
        if (lambert > 0.0 && dot(h.Geometric, SunDir) > 0.0)
        {
            sun = Visible(h.Position + h.Geometric * 0.003, ConeSample(SunDir, cos(Params0.x), Random2()), 300.0) ? lambert : 0.0;
        }

        sum += albedo * (SunColor * (sun / PI) + AmbientGuess(h.Normal));
    }

    return lerp(AmbientGuess(n), sum / BounceRays, BounceWeight);
}

// Rayons de soleil dans la poussière, le long du rayon de vue jusqu'à la première surface : à chaque pas, un rayon
// d'ombre vers le soleil (seuls les points dans les faisceaux des fenêtres sont éclairés). Fonction de phase de
// Henyey et Greenstein (1941), diffusion vers l'avant.
float3 Shafts(float3 origin, float3 direction, float distance)
{
    float length = min(distance, 42.0);
    float step = length / ShaftSteps;
    float offset = Random();
    float g = 0.6;
    float cosTheta = dot(direction, SunDir);
    float phase = (1.0 - g * g) / (4.0 * PI * pow(1.0 + g * g - 2.0 * g * cosTheta, 1.5));
    float cosMax = cos(Params0.x);
    float lit = 0.0;
    [loop]
    for (int i = 0; i < ShaftSteps; i++)
    {
        float3 p = origin + direction * ((i + offset) * step);
        lit += Visible(p, ConeSample(SunDir, cosMax, Random2()), 300.0) ? 1.0 : 0.0;
    }

    return SunColor * (Params0.y * phase * length * lit / ShaftSteps);
}

// Couleur d'un point vu dans le reflet d'un verre : soleil (un rayon d'ombre) et ambiance, sans autre rebond.
float3 ReflectionOnce(float3 origin, float3 direction)
{
    Hit h;
    if (!TraceClosest(origin, direction, 300.0, MaskSolid | MaskFlame, h))
    {
        return Outside(origin, direction, true);
    }

    Surface s = SurfaceAt(h.Material, h.Position, true);
    if (s.Kind == KindLight)
    {
        return s.Emission;
    }

    float3 albedo = s.Kind == KindMetal ? s.Albedo * 0.5 : s.Albedo;
    return SunLight(h.Position, h.Geometric, h.Normal, -direction, s, 1) + albedo * AmbientGuess(h.Normal);
}

// ---------------------------------------------------------------------------------------------------------------
// L'image.
// ---------------------------------------------------------------------------------------------------------------

struct TraceOut
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
};

TraceOut TracePS(FullscreenOut input)
{
    RandomState = HashU((uint)input.Position.x * 1973u + (uint)input.Position.y * 9277u + (uint)FrameIndex * 26699u);
    float3 origin = CameraPos;
    float3 direction = RayDirection(input.UV);
    float3 radiance = 0.0;
    float3 throughput = 1.0;
    float3 firstPosition = origin + direction * 1000.0;
    float firstDistance = 60.0;
    bool open = true;
    bool sharp = true;

    [loop]
    for (int depth = 0; depth < MaxDepth && open; depth++)
    {
        Hit hit;
        if (!TraceClosest(origin, direction, 1000.0, 0xFF, hit))
        {
            radiance += throughput * Outside(origin, direction, sharp);
            open = false;
            break;
        }

        if (depth == 0)
        {
            firstPosition = hit.Position;
            firstDistance = hit.T;
        }

        Surface s = SurfaceAt(hit.Material, hit.Position, sharp || depth <= 1);
        float3 v = -direction;
        if (s.Kind == KindLight)
        {
            radiance += throughput * s.Emission;
            open = false;
            break;
        }

        if (s.Kind == KindDiffuse || s.Kind == KindVarnish)
        {
            // Partie diffuse : soleil (ombre douce) et rebond de la lumière (au premier point seulement ; plus loin,
            // l'ambiance moyenne de la salle).
            // Attention : « a ? b : c » calcule toujours b et c en HLSL 2018 ; le rebond coûteux passe par un vrai test.
            float3 diffuse;
            if (depth == 0)
            {
                diffuse = SunLight(hit.Position, hit.Geometric, hit.Normal, v, s, ShadowRays) + s.Albedo * Bounce(hit.Position, hit.Geometric, hit.Normal);
            }
            else
            {
                diffuse = SunLight(hit.Position, hit.Geometric, hit.Normal, v, s, 1) + s.Albedo * AmbientGuess(hit.Normal);
            }

            if (s.Kind == KindDiffuse)
            {
                radiance += throughput * diffuse;
                open = false;
                break;
            }

            // Vernis : le reflet net continue le chemin, pondéré par Fresnel (approximation de Schlick, 1994). Les marbres
            // et le sol sont polis : un reflet tiré au hasard autour de la direction miroir ne ferait que du grain. Le
            // reflet du soleil y est déjà compté par la lumière directe (GGX) : plus de disque solaire après ce rebond.
            float fresnel = 0.04 + 0.96 * pow(1.0 - saturate(dot(hit.Normal, v)), 5.0);
            radiance += throughput * diffuse * (1.0 - fresnel);
            throughput *= fresnel;
            direction = reflect(direction, hit.Normal);
            origin = hit.Position + hit.Geometric * 0.003;
            sharp = false;
        }
        else if (s.Kind == KindMetal)
        {
            // Métal poli (miroirs, dorures, or, chrome) : reflet net, teinté par le métal.
            throughput *= FresnelSchlick(saturate(dot(hit.Normal, v)), s.Albedo);
            direction = reflect(direction, hit.Normal);
            origin = hit.Position + hit.Geometric * 0.003;
        }
        else
        {
            // Verre : réfraction (loi de Snell-Descartes) et reflet (Fresnel) ; teinte à la sortie de la matière.
            float eta = hit.Front ? 1.0 / s.Ior : s.Ior;
            float f0 = pow((1.0 - s.Ior) / (1.0 + s.Ior), 2.0);
            float fresnel = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(hit.Normal, v)), 5.0);
            float3 refracted = refract(direction, hit.Normal, eta);
            if (dot(refracted, refracted) < 1e-6)
            {
                direction = reflect(direction, hit.Normal);
                origin = hit.Position + hit.Geometric * 0.003;
                continue;
            }

            if (depth < 3)
            {
                radiance += throughput * fresnel * ReflectionOnce(hit.Position + hit.Geometric * 0.003, reflect(direction, hit.Normal));
            }

            throughput *= (1.0 - fresnel) * (hit.Front ? 1.0 : s.Albedo);
            direction = refracted;
            origin = hit.Position - hit.Geometric * 0.003;
        }

        if (max(throughput.r, max(throughput.g, throughput.b)) < 0.01)
        {
            open = false;
        }
    }

    // Chemin arrêté après le dernier reflet permis (deux miroirs face à face) : l'ambiance de la salle, sans trou noir.
    if (open)
    {
        radiance += throughput * HallGlow;
    }

    radiance += Shafts(CameraPos, RayDirection(input.UV), firstDistance);

    TraceOut output;
    output.Color = float4(max(radiance, 0.0), 1.0);
    output.Velocity = ScreenVelocity(firstPosition);
    return output;
}
