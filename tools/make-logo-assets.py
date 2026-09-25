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
    grey = logo.getpixel((20, 20))

    # Bannière : le char et les lettres, sans le sol ni le haut du mur.
    banner = logo.crop((0, 380, 2048, 1460))
    banner.resize((720, round(720 * banner.height / banner.width)), Image.LANCZOS).save(os.path.join(APP, "maus-banner.png"), optimize=True)

    # Logo réduit pour le README et le Store.
    logo.resize((800, 800), Image.LANCZOS).save(os.path.join(ROOT, "assets", "logo", "maus-logo-800.jpg"), quality=90, optimize=True)

    # Icône : le logo entier, sans recadrage, à toutes les tailles (demande du porteur).
    # bitmap_format="bmp" est indispensable : le décodeur d'icônes de Windows (WIC, utilisé par WPF) refuse
    # les petites tailles compressées en PNG, et MAUS plantait au démarrage. Sous Windows, tools/make-icon.ps1
    # produit la même icône.
    frames = [logo.resize((s, s), Image.LANCZOS) for s in (16, 24, 32, 48, 64, 128, 256)]
    frames[-1].save(os.path.join(APP, "maus.ico"), format="ICO", sizes=[f.size for f in frames],
                    append_images=frames[:-1], bitmap_format="bmp")
    letter = square(logo, (240, 700, 670, 1225), grey)
    letter.resize((256, 256), Image.LANCZOS).save(os.path.join(APP, "maus-letter-256.png"), optimize=True)

if __name__ == "__main__":
    main()
