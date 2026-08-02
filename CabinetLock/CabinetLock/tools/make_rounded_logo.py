"""
生成带圆角透明边的 logo
用法：
  python tools/make_rounded_logo.py
"""

from pathlib import Path
from PIL import Image, ImageDraw

def rounded_mask(size: tuple[int, int], radius: int) -> Image.Image:
    w, h = size
    radius = max(0, min(radius, min(w, h) // 2))
    mask = Image.new("L", (w, h), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((0, 0, w-1, h-1), radius=radius, fill=255)
    return mask

def apply_rounded_corners(img: Image.Image, radius: int) -> Image.Image:
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    corner = rounded_mask(img.size, radius)
    r, g, b, a = img.split()
    a = Image.composite(a, Image.new("L", img.size, 0), corner)
    return Image.merge("RGBA", (r, g, b, a))

def make_rounded_logo():
    root = Path(__file__).parent.parent
    input_path = root / "Resources" / "logo.ico"
    output_png = root / "Resources" / "logo-rounded.png"
    ico_path = root / "Resources" / "logo.ico"

    if not input_path.exists():
        print("❌ 找不到原始 logo.ico")
        return

    img = Image.open(input_path)
    if img.size == (16, 16):
        print("⚠️ 检测到当前 logo.ico 是 16x16，尝试加载原始尺寸...")
        # 尝试找原始文件
        orig = root / "Resources" / "logo.original.ico"
        if orig.exists():
            img = Image.open(orig)
            print(f"   加载原始尺寸: {img.size}")

    w, h = img.size
    radius = int(min(w, h) * 0.18)  # 18% 圆角

    rounded = apply_rounded_corners(img, radius)
    rounded.save(output_png, "PNG")
    print(f"✅ 已生成圆角 PNG: {output_png}")

    # 生成多尺寸 ICO，始终包含 256x256 作为最大尺寸
    sizes = [16, 24, 32, 48, 64, 128, 256]
    sizes = [s for s in sizes if s <= max(w, h)]
    if max(w, h) >= 256 and 256 not in sizes:
        sizes.append(256)
    sizes = sorted(set(sizes))

    frames = [rounded.resize((s, s), Image.Resampling.LANCZOS) for s in sizes]
    frames[0].save(
        ico_path,
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=frames[1:],
    )
    print(f"✅ 已更新圆角 ICO: {ico_path}（尺寸: {sizes}）")

if __name__ == "__main__":
    make_rounded_logo()