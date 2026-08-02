"""
简单生成 256x256 圆角透明 logo
用法：python tools/make_fullsize_rounded.py
"""

from PIL import Image, ImageDraw
from pathlib import Path

def make_fullsize_rounded():
    root = Path(__file__).parent.parent
    input_path = root / "Resources" / "logo.ico"
    png_out = root / "Resources" / "logo-rounded.png"
    ico_out = root / "Resources" / "logo.ico"

    if not input_path.exists():
        print("❌ 找不到原始 logo.ico")
        return

    # 读取原始大图
    img = Image.open(input_path)
    if img.size != (256, 256):
        print(f"⚠️ 检测到尺寸不是 256x256，已加载最大尺寸: {img.size}")
        img = img.resize((256, 256), Image.Resampling.LANCZOS)

    radius = 46  # 18% of 256

    # 创建圆角蒙版
    mask = Image.new("L", (256, 256), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((0, 0, 255, 255), radius=radius, fill=255)

    # 应用圆角透明
    r, g, b, a = img.split()
    a = Image.composite(a, Image.new("L", (256, 256), 0), mask)
    out = Image.merge("RGBA", (r, g, b, a))
    out.save(png_out, "PNG")
    print(f"✅ 已生成 256x256 圆角透明 PNG: {png_out}")

    # 保存为 ICO（256x256 单尺寸）
    out.save(ico_out, format="ICO", sizes=[(256, 256)])
    print(f"✅ 已保存为 256x256 圆角 ICO: {ico_out}")

if __name__ == "__main__":
    make_fullsize_rounded()