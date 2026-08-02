"""
最终版：生成 256x256 全尺寸圆角透明 logo
"""

from PIL import Image, ImageDraw
from pathlib import Path

root = Path("D:\\UGit\\edu-training-cabinet-fingerlock\\CabinetLock\\CabinetLock")
ico_in = root / "Resources" / "logo.original.ico"
png_out = root / "Resources" / "logo-rounded.png"
ico_out = root / "Resources" / "logo.ico"

if not ico_in.exists():
    print("❌ 找不到 logo.original.ico")
    exit(1)

img = Image.open(ico_in)
if img.size != (256, 256):
    print(f"⚠️ 原始尺寸 {img.size}，调整为 256x256")
    img = img.resize((256, 256), Image.Resampling.LANCZOS)

radius = 46

# 圆角蒙版
mask = Image.new("L", (256, 256), 0)
draw = ImageDraw.Draw(mask)
draw.rounded_rectangle((0, 0, 255, 255), radius=radius, fill=255)

# 应用圆角透明
r, g, b, a = img.split()
a = Image.composite(a, Image.new("L", (256, 256), 0), mask)
out = Image.merge("RGBA", (r, g, b, a))

out.save(png_out, "PNG")
print(f"✅ 已生成 256x256 圆角透明 PNG: {png_out}")

out.save(ico_out, format="ICO", sizes=[(256, 256)])
print(f"✅ 已保存为 256x256 全尺寸圆角 ICO: {ico_out}")
print("   现在 logo.ico 应该是全尺寸 256x256 了")

# 可选：清理 original 文件
# ico_in.unlink()  # 注释掉可保留备份