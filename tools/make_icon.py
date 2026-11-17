# Draws the WinSwitch icon: a lit toggle switch on a dark glass tile. Usage: python make_icon.py <output path without extension>
import sys
from PIL import Image, ImageDraw, ImageFilter
size = 1024
transparent = lambda: Image.new("RGBA", (size, size), (0, 0, 0, 0))
mask = Image.new("L", (size, size), 0); ImageDraw.Draw(mask).rounded_rectangle([40, 40, size - 40, size - 40], radius=220, fill=255)
gradient = Image.new("RGBA", (size, size)); gradient_draw = ImageDraw.Draw(gradient)
for row in range(size): blend = row / size; gradient_draw.line([(0, row), (size, row)], fill=(int(26 - 18 * blend), int(30 - 20 * blend), int(40 - 26 * blend), 255))
tile = transparent(); tile.paste(gradient, (0, 0), mask)
highlight = transparent(); ImageDraw.Draw(highlight).ellipse([-200, -560, size + 200, 380], fill=(255, 255, 255, 26))
tile = Image.alpha_composite(tile, Image.composite(highlight, transparent(), mask))
green = (88, 226, 140, 255)
art, glow = transparent(), transparent()
for layer in (art, glow):
    draw = ImageDraw.Draw(layer)
    draw.rounded_rectangle([170, 352, 854, 672], radius=160, outline=green, width=36)  # switch body
    draw.ellipse([574, 402, 794, 622], fill=green)  # knob, switched on
tile = Image.alpha_composite(tile, Image.composite(glow.filter(ImageFilter.GaussianBlur(30)), transparent(), mask))
tile = Image.alpha_composite(tile, art)
bolt = transparent(); ImageDraw.Draw(bolt).polygon([(370, 420), (300, 530), (350, 530), (320, 610), (410, 490), (360, 490)], fill=(255, 210, 90, 255))
tile = Image.alpha_composite(tile, bolt)
tile.save(sys.argv[1] + ".png")
tile.resize((256, 256), Image.LANCZOS).save(sys.argv[1] + "-256.png")
tile.save(sys.argv[1] + ".ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
