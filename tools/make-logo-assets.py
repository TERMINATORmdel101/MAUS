#!/usr/bin/env python3
"""Tire les images de l'application du logo du porteur (assets/logo/maus-logo.jpg), par recadrage et
redimensionnement seulement : rien n'est redessiné. Nécessite Pillow (pip install pillow).

  python3 tools/make-logo-assets.py
"""
import os
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "assets", "logo", "maus-logo.jpg")
APP = os.path.join(ROOT, "src", "Maus.App", "Assets")

def square(image, box, background):
    crop = image.crop(box)
    side = max(crop.size)
    canvas = Image.new("RGB", (side, side), background)
    canvas.paste(crop, ((side - crop.width) // 2, (side - crop.height) // 2))
    return canvas

def main():
    os.makedirs(APP, exist_ok=True)
    logo = Image.open(SOURCE).convert("RGB")


    # Bannière : les lettres, serrées, avec un peu du contour du char (lisibles dans la barre de 200 px de large).
    banner = logo.crop((100, 540, 1948, 1440))
    banner.resize((720, round(720 * banner.height / banner.width)), Image.LANCZOS).save(os.path.join(APP, "maus-banner.png"), optimize=True)

    # Logo réduit pour le README et le Store.
    logo.resize((800, 800), Image.LANCZOS).save(os.path.join(ROOT, "assets", "logo", "maus-logo-800.jpg"), quality=90, optimize=True)

    # Icône (le M aux petites tailles, le logo entier aux grandes), M seul, logo d'« À propos », écran de démarrage et
    # logo du rapport : produits sous Windows par tools/make-app-images.ps1 (29/09/2026), qui fait foi.

if __name__ == "__main__":
    main()
