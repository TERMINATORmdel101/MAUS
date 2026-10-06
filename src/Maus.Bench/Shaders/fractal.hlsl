// Benchmark MAUS, test « Calcul » : vol à l'intérieur d'une Mandelbox (fractale de T. Lowe, 2010), entièrement
// calculée à chaque pixel par lancer de rayons sur une fonction de distance. Chaque pixel évalue la fractale des
// centaines de fois (rayon principal, ombres douces vers deux lumières, occlusion, reflet) : la carte graphique est
// limitée par sa puissance de calcul pure, pas par sa mémoire.
#include "common.hlsli"

// Params0 : échelle, rayon minimal², rayon fixe², itérations
// Params1 : position de l'orbe lumineux (xyz), intensité (w)
// Params2 : densité du brouillard, intensité de la lueur, pas maximum, pas des ombres
// Params3 : décalage des couleurs, finesse des détails, reflets (0 ou 1), rayon de l'orbe

struct SceneOut
{
    float4 Color : SV_Target0;
    float2 Velocity : SV_Target1;
};

float FractalDistance(float3 p, out float4 trap)
{
    float scale = Params0.x;
    float minRadius2 = Params0.y;
    float fixedRadius2 = Params0.z;
    int iterations = (int)Params0.w;
    float3 z = p;
    float derivative = 1.0;
    trap = float4(1e9, 1e9, 1e9, 1e9);
    [loop]
    for (int i = 0; i < iterations; i++)
    {
        // Pliage de boîte : ce qui dépasse le cube [-1, 1] est replié vers l'intérieur.
        z = clamp(z, -1.0, 1.0) * 2.0 - z;
        // Pliage de sphère : inversion dans la sphère de rayon fixe, mise à l'échelle près du centre.
        float r2 = dot(z, z);
        float k = r2 < minRadius2 ? fixedRadius2 / minRadius2 : (r2 < fixedRadius2 ? fixedRadius2 / r2 : 1.0);
        z *= k;
        derivative *= k;
        z = z * scale + p;
        derivative = derivative * abs(scale) + 1.0;
        trap = min(trap, float4(abs(z.x), abs(z.y), abs(z.z), r2));
    }

    return length(z) / abs(derivative);
}

float FractalDistance(float3 p)
{
    float4 trap;
    return FractalDistance(p, trap);
}

// Marche le long du rayon jusqu'à la surface ; la précision suit la taille d'un pixel à la distance atteinte.
float March(float3 origin, float3 direction, float maxDistance, int maxSteps, float pixelAngle, out float steps)
{
    float t = 0.0;
    steps = 0.0;
    [loop]
    for (int i = 0; i < maxSteps; i++)
    {
        float d = FractalDistance(origin + direction * t);
        float epsilon = max(pixelAngle * t * Params3.y, 2e-5);
        steps = (float)i;
        if (d < epsilon)
        {
            return t;
        }

        t += d * 0.9;
        if (t > maxDistance)
        {
            break;
        }
    }

    return -1.0;
}

float3 SurfaceNormal(float3 p, float epsilon)
{
    float2 k = float2(1.0, -1.0);
    return normalize(
        k.xyy * FractalDistance(p + k.xyy * epsilon) +
        k.yyx * FractalDistance(p + k.yyx * epsilon) +
        k.yxy * FractalDistance(p + k.yxy * epsilon) +
        k.xxx * FractalDistance(p + k.xxx * epsilon));
}

// Ombre douce : la distance minimale au bord, rapportée au chemin parcouru, donne la pénombre.
float SoftShadow(float3 p, float3 direction, float maxDistance, float hardness, int steps)
{
    float result = 1.0;
    float t = 0.002;
    [loop]
    for (int i = 0; i < steps; i++)
    {
        float h = FractalDistance(p + direction * t);
        result = min(result, hardness * h / t);
        t += clamp(h, 0.002, 0.2);
        if (result < 0.002 || t > maxDistance)
        {
            break;
        }
    }

    return saturate(result);
}

float AmbientOcclusion(float3 p, float3 n, float radius)
{
    float occlusion = 0.0;
    float weight = 1.0;
    const int samples = Light ? 3 : 6;
    [unroll]
    for (int i = 1; i <= samples; i++)
    {
        float h = radius * i / samples;
        occlusion += (h - FractalDistance(p + n * h)) * weight;
        weight *= 0.65;
    }

    return saturate(1.0 - occlusion * 2.5 / radius);
}

void SurfaceMaterial(float4 trap, float3 p, out float3 albedo, out float metallic, out float roughness, out float3 emissive)
{
    float shell = saturate(sqrt(trap.w) * 0.45);
    float band = smoothstep(0.55, 0.75, frac(shell * 2.7 + Params3.x));
    float axis = saturate(trap.x * 1.5);
    float3 gold = float3(1.00, 0.71, 0.33);
    float3 copper = float3(0.92, 0.50, 0.32);
    float3 stone = float3(0.07, 0.065, 0.07);
    float3 teal = float3(0.03, 0.16, 0.19);
    albedo = lerp(lerp(stone, teal, axis), lerp(gold, copper, axis), band);
    float jewel = smoothstep(0.7, 0.9, frac(sqrt(trap.z) * 3.1 + Params3.x * 0.5));
    float3 gem = lerp(float3(0.05, 0.42, 0.25), float3(0.08, 0.18, 0.55), saturate(trap.y * 2.0));
    albedo = lerp(albedo, gem, jewel * (1.0 - band) * 0.85);
    metallic = band;
    roughness = lerp(0.6, 0.25, band);

    // Veines incandescentes là où l'orbite passe très près d'un plan de pliage.
    float vein = pow(saturate(1.0 - trap.y * 9.0), 10.0);
    emissive = float3(1.0, 0.32, 0.06) * vein * 4.0 * (0.6 + 0.4 * sin(Time * 1.7 + p.x * 3.0));
}

float3 Sky(float3 direction)
{
    float up = direction.y * 0.5 + 0.5;
    float3 color = lerp(float3(0.006, 0.008, 0.016), float3(0.02, 0.045, 0.09), up);
    // Voile de nébuleuse très léger, pour que le fond ne soit pas un aplat.
    color += float3(0.03, 0.015, 0.05) * pow(saturate(Fbm(direction * 3.0, 4) + 0.35), 3.0);
    float sun = saturate(dot(direction, SunDir));
    color += SunColor * (pow(sun, 1500.0) * 60.0 + pow(sun, 60.0) * 0.6 + pow(sun, 6.0) * 0.06);
    return color;
}

// Diffusion vers l'avant (Henyey-Greenstein) : le brouillard s'illumine surtout face au soleil.
float PhaseHG(float cosTheta, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4), 1.5));
}

// Le soleil est-il visible depuis ce point du brouillard ? (marche grossière, arrêt dès que la structure le cache)
float SunVisibility(float3 p, int steps)
{
    float t = 0.01;
    [loop]
    for (int i = 0; i < steps; i++)
    {
        float h = FractalDistance(p + SunDir * t);
        if (h < 0.001)
        {
            return 0.0;
        }

        t += max(h, 0.01);
        if (t > 5.0)
        {
            break;
        }
    }

    return 1.0;
}

// Rayons de lumière : le soleil filtre à travers les ouvertures de la fractale et éclaire le brouillard.
// Le point de départ change à chaque pixel et à chaque image, l'anticrénelage temporel lisse le résultat.
float4 VolumetricLight(float3 origin, float3 direction, float distance, float2 pixel)
{
    // Mode léger : moins d'échantillons, plus de bruit lissé par l'anticrénelage temporel.
    const int samples = Light ? 10 : 28;
    float stepSize = min(distance, 10.0) / samples;
    float offset = InterleavedNoise(pixel);
    float phase = PhaseHG(dot(direction, SunDir), 0.6) * 4.0 * PI;
    float3 light = 0.0;
    float transmittance = 1.0;
    [loop]
    for (int i = 0; i < samples; i++)
    {
        float t = (i + offset) * stepSize;
        float3 p = origin + direction * t;
        // Brume concentrée autour de la fractale : le ciel lointain reste sombre et contrasté.
        float aura = exp(-max(length(p) - 1.6, 0.0) * 2.2);
        float density = Params2.x * aura * (0.45 + 1.1 * ValueNoise(p * 2.5 + float3(0.0, Time * 0.03, Time * 0.02)));
        float visibility = SunVisibility(p, Light ? 8 : 18);
        light += transmittance * density * stepSize * SunColor * (visibility * phase * 0.9 + 0.003);
        transmittance *= exp(-density * stepSize);
    }

    return float4(light, transmittance);
}

// Lumière diffusée par le brouillard autour de l'orbe (intégrale exacte d'une source ponctuelle le long du rayon).
float OrbScattering(float3 origin, float3 direction, float distance)
{
    float3 toLight = Params1.xyz - origin;
    float along = dot(toLight, direction);
    float closest = max(length(toLight - direction * along), 1e-3);
    return (atan((distance - along) / closest) - atan(-along / closest)) / closest;
}

float3 ShadePoint(float3 p, float3 n, float3 v, float4 trap, float pixelSize, bool detailed)
{
    float3 albedo, emissive;
    float metallic, roughness;
    SurfaceMaterial(trap, p, albedo, metallic, roughness, emissive);
    int shadowSteps = (int)Params2.w;

    float ao = AmbientOcclusion(p, n, 0.12);
    float3 color = emissive;

    // Soleil filtrant à travers la structure.
    float sunShadow = detailed ? SoftShadow(p + n * pixelSize * 2.0, SunDir, 6.0, 12.0, shadowSteps) : 1.0;
    color += ShadeDirect(albedo, metallic, roughness, n, v, SunDir, SunColor * 3.0 * sunShadow);

    // Orbe de plasma : lumière ponctuelle, décroissance en carré de la distance.
    float3 toOrb = Params1.xyz - p;
    float orbDistance = length(toOrb);
    float3 l = toOrb / orbDistance;
    float orbShadow = detailed ? SoftShadow(p + n * pixelSize * 2.0, l, orbDistance - Params3.w, 16.0, shadowSteps) : 1.0;
    float3 orbColor = float3(1.0, 0.55, 0.22) * Params1.w / (orbDistance * orbDistance + 0.05);
    color += ShadeDirect(albedo, metallic, roughness, n, v, l, orbColor * orbShadow);

    // Ciel et rebonds : lumière ambiante assombrie par l'occlusion.
    float3 ambient = lerp(float3(0.05, 0.03, 0.02), float3(0.02, 0.04, 0.07), n.y * 0.5 + 0.5);
    color += albedo * ambient * ao * (1.0 - metallic * 0.7);
    color += lerp(0.04.xxx, albedo, metallic) * Sky(reflect(-v, n)) * ao * 0.6;
    return color;
}

SceneOut FractalPS(FullscreenOut input)
{
    SceneOut output;
    float3 origin = CameraPos;
    float3 direction = RayDirection(input.UV);
    float pixelAngle = 1.2 * InvResolution.y;
    float maxDistance = 40.0;
    float steps;
    float t = March(origin, direction, maxDistance, (int)Params2.z, pixelAngle, steps);

    float3 color;
    float3 hitPoint;
    if (t > 0.0)
    {
        hitPoint = origin + direction * t;
        float4 trap;
        FractalDistance(hitPoint, trap);
        float pixelSize = max(pixelAngle * t, 1e-4);
        float3 n = SurfaceNormal(hitPoint, pixelSize * 0.5);
        float3 v = -direction;
        color = ShadePoint(hitPoint, n, v, trap, pixelSize, true);

        // Un reflet complet sur les métaux : un second lancer de rayon dans la fractale.
        float3 albedo, emissive;
        float metallic, roughness;
        SurfaceMaterial(trap, hitPoint, albedo, metallic, roughness, emissive);
        if (!Light && Params3.z > 0.5 && metallic > 0.5)
        {
            float3 r = reflect(direction, n);
            float reflectionSteps;
            float3 start = hitPoint + n * pixelSize * 4.0;
            float tr = March(start, r, 8.0, (int)Params2.z / 2, pixelAngle * 2.0, reflectionSteps);
            float3 reflected;
            if (tr > 0.0)
            {
                float3 rp = start + r * tr;
                float4 rtrap;
                FractalDistance(rp, rtrap);
                float3 rn = SurfaceNormal(rp, pixelSize * 2.0);
                reflected = ShadePoint(rp, rn, -r, rtrap, pixelSize * 2.0, false) * exp(-tr * Params2.x);
            }
            else
            {
                reflected = Sky(r);
            }

            float3 f0 = lerp(0.04.xxx, albedo, metallic);
            float3 fresnel = FresnelSchlick(saturate(dot(n, -direction)), f0);
            color += reflected * fresnel * (1.0 - roughness) * 0.8;
        }

    }
    else
    {
        t = maxDistance;
        hitPoint = origin + direction * 1000.0;
        color = Sky(direction);
    }

    float4 volume = VolumetricLight(origin, direction, t, input.Position.xy);
    color = color * volume.a + volume.rgb;

    // Halo de l'orbe dans le brouillard, cœur incandescent, lueur des zones frôlées par le rayon.
    color += float3(1.0, 0.5, 0.2) * Params1.w * Params2.x * 0.15 * OrbScattering(origin, direction, t);
    float3 toOrb = Params1.xyz - origin;
    float along = dot(toOrb, direction);
    if (along > 0.0 && along < t)
    {
        float closest = length(toOrb - direction * along);
        color += float3(1.0, 0.85, 0.6) * Params1.w * 2.0 * smoothstep(Params3.w, Params3.w * 0.2, closest);
    }

    color += float3(0.1, 0.35, 0.4) * Params2.y * pow(steps / Params2.z, 3.0);

    output.Color = float4(color, 1.0);
    output.Velocity = t < maxDistance ? ScreenVelocity(hitPoint) : DirectionVelocity(direction);
    return output;
}
