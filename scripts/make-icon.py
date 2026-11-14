"""產生 icon.png（NuGet 套件圖示與 README 用）。

概念：同一個資源鍵有兩份定義 —— 實心的那把是「贏的」，後面半透明的那把是
被合併順序蓋掉、永遠輪不到的那一份。這就是這個工具在回答的問題。

風格跟家族其他套件一致：圓角方塊、扁平、單一概念圖形。

重新產生：py -3 scripts/make-icon.py
（以 4 倍大小繪製再縮小，邊緣才會平滑。）
"""

from PIL import Image, ImageDraw

SIZE = 256          # 輸出尺寸
S = 4               # 超取樣倍率
N = SIZE * S

BG = (24, 27, 33, 255)          # 深炭灰
KEY = (245, 176, 65, 255)       # 黃銅色
GHOST = (245, 176, 65, 105)     # 同色、低透明度 —— 被蓋掉的那一份


def key_layer(color):
    """在透明圖層上畫一把水平的鑰匙（環在左、齒在右下），之後再旋轉。"""
    layer = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    cy = N // 2
    ring_cx = int(N * 0.30)
    ring_r = int(N * 0.150)
    hole_r = int(N * 0.068)
    shaft_h = int(N * 0.058)
    shaft_end = int(N * 0.80)

    # 鑰匙柄
    d.rounded_rectangle(
        [ring_cx, cy - shaft_h // 2, shaft_end, cy + shaft_h // 2],
        radius=shaft_h // 2, fill=color)

    # 兩個齒
    tooth_w = int(N * 0.050)
    for x, h in ((0.64, 0.105), (0.735, 0.080)):
        x0 = int(N * x)
        d.rounded_rectangle(
            [x0, cy, x0 + tooth_w, cy + int(N * h)],
            radius=tooth_w // 4, fill=color)

    # 環（先畫實心圓，再挖洞）
    d.ellipse([ring_cx - ring_r, cy - ring_r, ring_cx + ring_r, cy + ring_r], fill=color)
    d.ellipse([ring_cx - hole_r, cy - hole_r, ring_cx + hole_r, cy + hole_r], fill=(0, 0, 0, 0))

    return layer


def main():
    # 圓角方塊底
    tile = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    ImageDraw.Draw(tile).rounded_rectangle([0, 0, N - 1, N - 1], radius=int(N * 0.19), fill=BG)

    angle = 38

    # 影子鑰匙：往左上偏一點
    ghost = key_layer(GHOST).rotate(angle, resample=Image.BICUBIC)
    shift = int(N * 0.095)
    ghost_shifted = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    ghost_shifted.paste(ghost, (-shift, -shift), ghost)

    # 實心鑰匙：往右下偏一點，兩把合起來才會置中
    solid = key_layer(KEY).rotate(angle, resample=Image.BICUBIC)
    solid_shifted = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    solid_shifted.paste(solid, (shift // 2, shift // 2), solid)

    art = Image.alpha_composite(ghost_shifted, solid_shifted)

    # 只留在圓角方塊範圍內
    out = Image.alpha_composite(tile, Image.composite(art, Image.new("RGBA", (N, N), (0, 0, 0, 0)), tile.split()[3]))

    out = out.resize((SIZE, SIZE), Image.LANCZOS)
    out.save("icon.png", optimize=True)
    print(f"wrote icon.png ({SIZE}x{SIZE})")


if __name__ == "__main__":
    main()
