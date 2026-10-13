"""PaddleOCR（PP-OCRv6）の ONNX を int8 に量子化する（PLAN.md 課題4）。

全画面の読み取りが約 1,075ms かかり、性能要件 1.5 秒に収まらない。
内訳は「検出などの固定費 695ms ＋ 認識 13.8ms/行」（2026-10-12 の実測）。

**利用者に手間をかけずに速くできる唯一の道**として量子化を試す。
Windows OCR の高速モードは言語パックの追加を利用者に求めるため、
配布物としては採りにくい（2026-10-13 の判断）。

注意: **動的量子化が効くのは MatMul / Gemm が主体のモデル。**
畳み込みが主体の検出モデルでは効かない、あるいは遅くなることがある。
どちらも作って実測で決める（効かない方は fp32 のまま使えばよい）。

使い方:

    cd tools/ocr-eval
    ../nmt-convert/.venv/Scripts/python.exe quantize.py

出力は models_int8/ に入る。**元の models/ は触らない。**
"""

from __future__ import annotations

import shutil
import sys
import tempfile
import time
from pathlib import Path


def size_mb(path: Path) -> float:
    return sum(f.stat().st_size for f in path.rglob("*") if f.is_file()) / 1024 / 1024


def quantize_in_ascii_path(quantize_dynamic, quant_type, source: Path, destination: Path) -> None:
    """ASCII だけのディレクトリへ写してから量子化する。

    **このリポジトリのパスには日本語が含まれる**（009_ゲーム翻訳アプリ）。
    onnxruntime は量子化の前に形状推論を行い、その途中のファイルを
    モデルと同じ場所へ書くが、ネイティブ側が日本語のパスを扱えず
    FileNotFoundError になる（2026-10-13 に実際に踏んだ）。

    モデルの中身とは無関係な、環境だけの問題。写せば済む。
    """
    with tempfile.TemporaryDirectory() as work:
        workspace = Path(work)
        staged = workspace / source.name
        shutil.copy2(source, staged)

        quantized = workspace / ("int8_" + source.name)
        quantize_dynamic(staged, quantized, weight_type=quant_type.QInt8)

        shutil.copy2(quantized, destination)


def main() -> int:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    here = Path(__file__).resolve().parent
    source = here / "models"
    target = here / "models_int8"

    if not source.exists():
        print(f"モデルがありません: {source}")
        return 1

    print(f"元      : {source}  {size_mb(source):.1f} MB")
    print(f"出力先  : {target}")
    print()

    target.mkdir(parents=True, exist_ok=True)

    for item in sorted(source.rglob("*")):
        if item.is_dir():
            continue

        destination = target / item.relative_to(source)
        destination.parent.mkdir(parents=True, exist_ok=True)

        if item.suffix != ".onnx":
            # 文字セットなどはそのまま持っていく。
            shutil.copy2(item, destination)
            continue

        started = time.perf_counter()
        quantize_in_ascii_path(quantize_dynamic, QuantType, item, destination)
        print(
            f"  {item.relative_to(source).as_posix():<40} "
            f"{item.stat().st_size / 1024 / 1024:6.1f} MB → "
            f"{destination.stat().st_size / 1024 / 1024:5.1f} MB"
            f"  ({time.perf_counter() - started:.1f} 秒)"
        )

    print()
    print(f"  fp32 合計 {size_mb(source):6.1f} MB")
    print(f"  int8 合計 {size_mb(target):6.1f} MB")
    print()
    print("次は OcrBench で速度と精度を測ること。**速くなっても精度の代償を見ずに採らない。**")
    print("  dotnet run --project tools/ocr-eval/OcrBench -- "
          "tools/ocr-eval/samples tools/ocr-eval/models_int8 --quick")
    return 0


if __name__ == "__main__":
    sys.exit(main())
