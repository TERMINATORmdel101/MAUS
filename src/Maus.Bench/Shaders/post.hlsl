// Benchmark MAUS : anticrénelage temporel, halo lumineux (bloom), rendu final façon cinéma.
#include "common.hlsli"

cbuffer PostConstants : register(b1)
{
    float4 SourceSize;      // largeur, hauteur, 1/largeur, 1/hauteur de la texture lue
    float4 OutputRect;      // zone de l'image affichée : x0, y0, largeur, hauteur (pixels)
    float4 Grade;           // intensité du halo, vignettage, grain, aberration chromatique
    float4 Lift;            // relève des noirs (rgb), saturation (a)
    float4 Gain;            // gain par couleur (rgb), contraste (a)
    float4 Extra;           // x = poids de l'image courante (anticrénelage), y = seuil du halo, z = fondu au noir, w = netteté
};

Texture2D<float4> Source : register(t0);
Texture2D<float4> History : register(t1);
Texture2D<float2> Motion : register(t2);
Texture2D<float4> BloomTexture : register(t3);

// ---------------------------------------------------------------------------------------------------------------
// Anticrénelage temporel : l'image précédente, recalée par le mouvement, est mélangée à l'image courante après avoir
// été bornée par les couleurs du voisinage (principe exposé par B. Karis, « High Quality Temporal Supersampling »,
// SIGGRAPH 2014 ; implémentation écrite pour MAUS).
// ---------------------------------------------------------------------------------------------------------------

float3 ToYCoCg(float3 c)
{
    return float3(dot(c, float3(0.25, 0.5, 0.25)), dot(c, float3(0.5, 0.0, -0.5)), dot(c, float3(-0.25, 0.5, -0.25)));
}

float3 FromYCoCg(float3 c)
{
    return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}

// Lecture bicubique (Catmull-Rom) en 9 lectures bilinéaires : l'historique reste net malgré les recalages.
float3 SampleCatmullRom(Texture2D<float4> tex, float2 uv, float4 size)
{
    float2 position = uv * size.xy;
    float2 center = floor(position - 0.5) + 0.5;
    float2 f = position - center;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 offset12 = w2 / w12;
    float2 t0 = (center - 1.0) * size.zw;
    float2 t3 = (center + 2.0) * size.zw;
    float2 t12 = (center + offset12) * size.zw;
    float3 result = 0.0;
    result += tex.SampleLevel(LinearClamp, float2(t0.x, t0.y), 0).rgb * w0.x * w0.y;
    result += tex.SampleLevel(LinearClamp, float2(t12.x, t0.y), 0).rgb * w12.x * w0.y;
    result += tex.SampleLevel(LinearClamp, float2(t3.x, t0.y), 0).rgb * w3.x * w0.y;
    result += tex.SampleLevel(LinearClamp, float2(t0.x, t12.y), 0).rgb * w0.x * w12.y;
    result += tex.SampleLevel(LinearClamp, float2(t12.x, t12.y), 0).rgb * w12.x * w12.y;
    result += tex.SampleLevel(LinearClamp, float2(t3.x, t12.y), 0).rgb * w3.x * w12.y;
    result += tex.SampleLevel(LinearClamp, float2(t0.x, t3.y), 0).rgb * w0.x * w3.y;
    result += tex.SampleLevel(LinearClamp, float2(t12.x, t3.y), 0).rgb * w12.x * w3.y;
    result += tex.SampleLevel(LinearClamp, float2(t3.x, t3.y), 0).rgb * w3.x * w3.y;
    return max(result, 0.0);
}

float4 TemporalPS(FullscreenOut input) : SV_Target
{
    float2 uv = input.UV;
    int2 pixel = int2(input.Position.xy);

    // Mouvement le plus long du voisinage immédiat : les bords des objets qui bougent restent propres.
    float2 velocity = 0.0;
    float best = -1.0;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 v = Motion.Load(int3(pixel + int2(x, y), 0));
            float l = dot(v, v);
            if (l > best)
            {
                best = l;
                velocity = v;
            }
        }
    }

    float3 current = Source.Load(int3(pixel, 0)).rgb;
    float3 m1 = 0.0;
    float3 m2 = 0.0;
    [unroll]
    for (int j = -1; j <= 1; j++)
    {
        [unroll]
        for (int i = -1; i <= 1; i++)
        {
            float3 c = ToYCoCg(Source.Load(int3(pixel + int2(i, j), 0)).rgb);
            m1 += c;
            m2 += c * c;
        }
    }

    float3 mean = m1 / 9.0;
    float3 sigma = sqrt(max(m2 / 9.0 - mean * mean, 0.0));
    float3 boxMin = mean - 1.25 * sigma;
    float3 boxMax = mean + 1.25 * sigma;

    float2 previousUV = uv - velocity;
    float3 history = SampleCatmullRom(History, previousUV, SourceSize);
    float3 historyY = ToYCoCg(history);

    // Bornage de l'historique vers le centre de la boîte (et non par simple serrage) : moins de traînées.
    float3 center = 0.5 * (boxMax + boxMin);
    float3 extents = 0.5 * (boxMax - boxMin) + 1e-4;
    float3 offset = historyY - center;
    float3 units = abs(offset / extents);
    float maxUnit = max(units.x, max(units.y, units.z));
    if (maxUnit > 1.0)
    {
        historyY = center + offset / maxUnit;
    }

    history = FromYCoCg(historyY);
    float weight = Extra.x;
    if (any(previousUV < 0.0) || any(previousUV > 1.0))
    {
        weight = 1.0;
    }

    // Mélange pondéré par la luminance : un pixel très brillant isolé ne laisse pas de traînée.
    float wc = weight / (1.0 + Luminance(current));
    float wh = (1.0 - weight) / (1.0 + Luminance(history));
    float3 result = (current * wc + history * wh) / (wc + wh);
    return float4(max(result, 0.0), 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
// Halo lumineux : réductions successives (13 lectures, moyenne pondérée à la première pour éviter les scintillements)
// puis agrandissements en tente 3×3 additionnés (principe présenté par J. Jimenez, « Next Generation Post Processing
// in Call of Duty: Advanced Warfare », SIGGRAPH 2014 ; implémentation écrite pour MAUS).
// ---------------------------------------------------------------------------------------------------------------

float3 KarisAverage(float3 a, float3 b, float3 c, float3 d)
{
    float wa = 1.0 / (1.0 + Luminance(a));
    float wb = 1.0 / (1.0 + Luminance(b));
    float wc = 1.0 / (1.0 + Luminance(c));
    float wd = 1.0 / (1.0 + Luminance(d));
    return (a * wa + b * wb + c * wc + d * wd) / (wa + wb + wc + wd);
}

float4 Downsample(float2 uv, bool firstPass)
{
    float2 t = SourceSize.zw;
    float3 a = Source.SampleLevel(LinearClamp, uv + t * float2(-2, -2), 0).rgb;
    float3 b = Source.SampleLevel(LinearClamp, uv + t * float2(0, -2), 0).rgb;
    float3 c = Source.SampleLevel(LinearClamp, uv + t * float2(2, -2), 0).rgb;
    float3 d = Source.SampleLevel(LinearClamp, uv + t * float2(-2, 0), 0).rgb;
    float3 e = Source.SampleLevel(LinearClamp, uv, 0).rgb;
    float3 f = Source.SampleLevel(LinearClamp, uv + t * float2(2, 0), 0).rgb;
    float3 g = Source.SampleLevel(LinearClamp, uv + t * float2(-2, 2), 0).rgb;
    float3 h = Source.SampleLevel(LinearClamp, uv + t * float2(0, 2), 0).rgb;
    float3 i = Source.SampleLevel(LinearClamp, uv + t * float2(2, 2), 0).rgb;
    float3 j = Source.SampleLevel(LinearClamp, uv + t * float2(-1, -1), 0).rgb;
    float3 k = Source.SampleLevel(LinearClamp, uv + t * float2(1, -1), 0).rgb;
    float3 l = Source.SampleLevel(LinearClamp, uv + t * float2(-1, 1), 0).rgb;
    float3 m = Source.SampleLevel(LinearClamp, uv + t * float2(1, 1), 0).rgb;
    float3 result;
    if (firstPass)
    {
        result = KarisAverage(j, k, l, m) * 0.5
            + KarisAverage(a, b, d, e) * 0.125 + KarisAverage(b, c, e, f) * 0.125
            + KarisAverage(d, e, g, h) * 0.125 + KarisAverage(e, f, h, i) * 0.125;
        // Seuil doux : seules les zones vraiment lumineuses débordent.
        float brightness = Luminance(result);
        float knee = Extra.y * 0.5;
        float soft = clamp(brightness - Extra.y + knee, 0.0, 2.0 * knee);
        soft = soft * soft / (4.0 * knee + 1e-4);
        float contribution = max(soft, brightness - Extra.y) / max(brightness, 1e-4);
        result *= contribution;
    }
    else
    {
        result = (j + k + l + m) * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + e * 0.125;
    }
    return float4(max(result, 0.0), 1.0);
}

float4 BloomFirstDownPS(FullscreenOut input) : SV_Target
{
    return Downsample(input.UV, true);
}

float4 BloomDownPS(FullscreenOut input) : SV_Target
{
    return Downsample(input.UV, false);
}

float4 BloomUpPS(FullscreenOut input) : SV_Target
{
    float2 t = SourceSize.zw;
    float2 uv = input.UV;
    float3 sum = Source.SampleLevel(LinearClamp, uv + t * float2(-1, -1), 0).rgb;
    sum += Source.SampleLevel(LinearClamp, uv + t * float2(0, -1), 0).rgb * 2.0;
    sum += Source.SampleLevel(LinearClamp, uv + t * float2(1, -1), 0).rgb;
    sum += Source.SampleLevel(LinearClamp, uv + t * float2(-1, 0), 0).rgb * 2.0;
    sum += Source.SampleLevel(LinearClamp, uv, 0).rgb * 4.0;
    sum += Source.SampleLevel(LinearClamp, uv + t * float2(1, 0), 0).rgb * 2.0;
    sum += Source.SampleLevel(LinearClamp, uv + t * float2(-1, 1), 0).rgb;
    sum += Source.SampleLevel(LinearClamp, uv + t * float2(0, 1), 0).rgb * 2.0;
    sum += Source.SampleLevel(LinearClamp, uv + t * float2(1, 1), 0).rgb;
    // Mélange additif : chaque niveau garde sa part, le halo s'étale sur toutes les échelles.
    return float4(sum / 16.0, 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
// Image finale : exposition, courbe filmique, étalonnage, vignettage, grain, aberration, mise à l'échelle de l'écran.
// ---------------------------------------------------------------------------------------------------------------

// Courbe filmique approchant celle de l'ACES (ajustement rationnel publié par K. Narkowicz, 2016).
float3 FilmicCurve(float3 x)
{
    return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
}

float3 LinearToSrgb(float3 c)
{
    c = saturate(c);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

float4 FinalPS(FullscreenOut input) : SV_Target
{
    float2 local = (input.Position.xy - OutputRect.xy) / OutputRect.zw;
    if (any(local < 0.0) || any(local > 1.0))
    {
        return float4(0, 0, 0, 1);
    }

    float2 fromCenter = local - 0.5;
    float aberration = Grade.w * dot(fromCenter, fromCenter);
    float3 color;
    color.r = SampleCatmullRom(Source, local - fromCenter * aberration, SourceSize).r;
    color.g = SampleCatmullRom(Source, local, SourceSize).g;
    color.b = SampleCatmullRom(Source, local + fromCenter * aberration, SourceSize).b;

    // Netteté légère (masque flou) pour compenser la douceur de l'anticrénelage.
    if (Extra.w > 0.0)
    {
        float3 blurred = Source.SampleLevel(LinearClamp, local + SourceSize.zw * float2(0.5, 0.5), 0).rgb
            + Source.SampleLevel(LinearClamp, local + SourceSize.zw * float2(-0.5, 0.5), 0).rgb
            + Source.SampleLevel(LinearClamp, local + SourceSize.zw * float2(0.5, -0.5), 0).rgb
            + Source.SampleLevel(LinearClamp, local + SourceSize.zw * float2(-0.5, -0.5), 0).rgb;
        color = max(color + (color - blurred * 0.25) * Extra.w, 0.0);
    }

    // Le halo additionne tous les niveaux (six) : on le ramène à une moyenne avant d'appliquer son intensité.
    float3 bloom = BloomTexture.SampleLevel(LinearClamp, local, 0).rgb / 6.0;
    color += bloom * Grade.x;
    color *= Exposure;

    // Étalonnage avant la courbe : relève des noirs, gain par couleur, saturation, contraste autour du gris moyen.
    color = color * Gain.rgb + Lift.rgb * 0.01;
    float luma = Luminance(color);
    color = max(lerp(luma.xxx, color, Lift.a), 0.0);
    color = 0.18 * pow(max(color / 0.18, 0.0), Gain.a);

    color = FilmicCurve(color);

    float vignette = 1.0 - Grade.y * pow(saturate(length(fromCenter * float2(1.0, 0.85)) * 1.25), 2.5);
    color *= vignette;
    color *= 1.0 - Extra.z;

    float3 srgb = LinearToSrgb(color);
    float grain = (InterleavedNoise(input.Position.xy) - 0.5);
    srgb += grain * (Grade.z + 1.0 / 255.0);
    return float4(srgb, 1.0);
}

// Simple recopie (aperçus, captures).
float4 CopyPS(FullscreenOut input) : SV_Target
{
    return Source.SampleLevel(LinearClamp, input.UV, 0);
}
