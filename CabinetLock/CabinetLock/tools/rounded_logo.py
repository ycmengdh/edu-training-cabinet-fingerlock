"""
重新生成 **256x256 全尺寸** 圆角透明 logo
直接运行即可

用法：
  python tools/rounded_logo.py
"""

from PIL import Image, ImageDraw
from pathlib import Path

def main():
    root = Path(__file__).parent.parent
    ico_in = root / "Resources" / "logo.ico"
    png_out = root / "Resources" / "logo-rounded.png"
    ico_out = root / "Resources" / "logo.ico"

    if not ico_in.exists():
        print("❌ 找不到原始 logo.ico")
        return

    img = Image.open(ico_in)
    if img.size != (256, 256):
        print(f"⚠️ 原始尺寸不是 256x256，已调整为: {img.size} → 256x256")
        img = img.resize((256, 256), Image.Resampling.LANCZOS)

    radius = 46  # 18% of 256

    # 圆角蒙版
    mask = Image.new("L", (256, 256), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((0, 0, 255, 255), radius=radius, fill=255)

    # 应用圆角透明
    r, g, b, a = img.split()
    a = Image.composite(a, Image.new("L", (256, 256), 0), mask)
    out = Image.merge("RGBA", (r, g, b, a))

    # 保存
    out.save(png_out, "PNG")
    print(f"✅ 已生成 256x256 圆角透明 PNG: {png_out}")

    # 保存为 256x256 ICO（全尺寸）
    out.save(ico_out, format="ICO", sizes=[(256, 256)])
    print(f"✅ 已保存为 256x256 圆角 ICO: {ico_out}")

if __name__ == "__main__":
    main()