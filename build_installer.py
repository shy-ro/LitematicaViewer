# 一键出安装包：publish ShellPreview(AOT dll) -> publish Setup(单文件 exe)。
# 两步必须分开跑：Setup 把 ShellPreview 的 dll 当资源嵌进自己，同图并发构建会
# 抢公共引用（Core）的 obj 把 pdb 锁死（csproj 注释里钉过的坑）。
# 用法：python build_installer.py   产物：dist/LitematicaViewer.Setup.exe
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SHELL = ROOT / "src" / "LitematicaViewer.ShellPreview" / "LitematicaViewer.ShellPreview.csproj"
SETUP = ROOT / "src" / "LitematicaViewer.Setup" / "LitematicaViewer.Setup.csproj"
PAYLOAD_DIR = ROOT / "dist" / "ShellPreview"
PAYLOAD = PAYLOAD_DIR / "LitematicaViewer.ShellPreview.dll"
ANGLE = PAYLOAD_DIR / "av_libglesv2.dll"


def run(cmd):
    print("+", " ".join(cmd))
    r = subprocess.run(cmd, cwd=ROOT)
    if r.returncode != 0:
        sys.exit(f"failed: {' '.join(cmd)} (rc={r.returncode})")


def main():
    run(["dotnet", "publish", str(SHELL), "-c", "Release", "-o", str(PAYLOAD_DIR)])

    # payload 校验：Setup 嵌的是这两个文件 + packs jar，缺了会装出空壳
    for f in (PAYLOAD, ANGLE):
        if not f.exists() or f.stat().st_size < 1024:
            sys.exit(f"payload missing or too small: {f}")

    run(["dotnet", "publish", str(SETUP), "-c", "Release", "-o", str(ROOT / "dist")])

    setup_exe = ROOT / "dist" / "LitematicaViewer.Setup.exe"
    print(f"OK {setup_exe} ({setup_exe.stat().st_size / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
