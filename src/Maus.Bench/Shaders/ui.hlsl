// Benchmark MAUS : interface par-dessus l'image (rectangles arrondis, textes à champ de distance, images).
#include "common.hlsli"

// Taille de l'image affichée (l'interface est dessinée à la résolution de l'écran, pas à celle du rendu).
cbuffer UiConstants : register(b1)
{
    float4 Viewport;    // largeur, hauteur, 1/largeur, 1/hauteur
};

Texture2D<float> FontAtlas : register(t0);
Texture2D<float4> Picture : register(t1);

struct UiVertex
{
    float2 Position : POSITION;   // pixels de l'image affichée
    float2 Local : TEXCOORD0;     // rectangle : position par rapport au centre (pixels) ; texte, image : coordonnées de texture
    float4 Color : COLOR0;        // couleur sRVB, opacité
    float4 Shape : TEXCOORD1;     // x = genre (0 rectangle, 1 texte, 2 image, 3 halo de texte), y = rayon ou seuil, zw = demi-taille
};

struct UiPixel
{
    float4 Position : SV_Position;
    float2 Local : TEXCOORD0;
    float4 Color : COLOR0;
    float4 Shape : TEXCOORD1;
};

UiPixel UiVS(UiVertex input)
{
    UiPixel o;
    float2 ndc = input.Position * Viewport.zw * float2(2.0, -2.0) + float2(-1.0, 1.0);
    o.Position = float4(ndc, 0.0, 1.0);
    o.Local = input.Local;
    o.Color = input.Color;
    o.Shape = input.Shape;
    return o;
}

float4 UiPS(UiPixel input) : SV_Target
{
    float kind = input.Shape.x;
    float alpha;
    float3 color = input.Color.rgb;
    if (kind < 0.5)
    {
        // Rectangle arrondi exact (distance signée), bord lissé sur un pixel.
        float radius = input.Shape.y;
        float2 q = abs(input.Local) - (input.Shape.zw - radius);
        float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        alpha = saturate(0.5 - d);
    }
    else if (kind < 1.5)
    {
        float distance = FontAtlas.SampleLevel(LinearClamp, input.Local, 0);
        float width = max(fwidth(distance), 1e-3) * 0.7;
        alpha = smoothstep(input.Shape.y - width, input.Shape.y + width, distance);
    }
    else if (kind < 2.5)
    {
        float4 texel = Picture.SampleLevel(LinearClamp, input.Local, 0);
        color *= texel.rgb;
        alpha = texel.a;
    }
    else
    {
        // Halo doux autour d'un texte (titres) : la distance est convertie en lueur décroissante.
        float distance = FontAtlas.SampleLevel(LinearClamp, input.Local, 0);
        alpha = smoothstep(input.Shape.y - 0.25, input.Shape.y, distance);
        alpha *= alpha;
    }

    alpha *= input.Color.a;
    return float4(color * alpha, alpha);
}
