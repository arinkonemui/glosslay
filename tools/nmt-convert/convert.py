"""ローカルNMT のモデルを ONNX へ変換し、int8 量子化する（PLAN.md「ローカルNMT の調査」）。

製品は ONNX Runtime で直接実行するため、配布物はここで作った ONNX と
書き換え済みの .spm / vocab.json だけでよい。Python は配布物に含めない。

    python convert.py Mitsua/elan-mt-bt-en-ja out/elan-en-ja
"""
import shutil
import sys
import time
from pathlib import Path


def size_mb(p: Path) -> float:
    return sum(f.stat().st_size for f in p.rglob("*") if f.is_file()) / 1024 / 1024


def main(model_id: str, out_dir: str) -> int:
    out = Path(out_dir)
    fp32 = out / "fp32"
    int8 = out / "int8"
    out.mkdir(parents=True, exist_ok=True)

    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    print(f"=== 1. {model_id} を ONNX へ書き出す ===")
    started = time.perf_counter()
    model = ORTModelForSeq2SeqLM.from_pretrained(model_id, export=True)
    model.save_pretrained(fp32)
    AutoTokenizer.from_pretrained(model_id).save_pretrained(fp32)
    print(f"  完了 {time.perf_counter() - started:.1f} 秒 / {size_mb(fp32):.1f} MB")
    for f in sorted(fp32.glob("*.onnx")):
        print(f"    {f.name:<34} {f.stat().st_size / 1024 / 1024:7.1f} MB")

    print()
    print("=== 2. int8 に量子化する ===")
    from onnxruntime.quantization import QuantType, quantize_dynamic

    int8.mkdir(parents=True, exist_ok=True)
    for f in sorted(fp32.iterdir()):
        if f.suffix != ".onnx":
            if f.is_file():
                shutil.copy2(f, int8 / f.name)
            continue
        started = time.perf_counter()
        quantize_dynamic(f, int8 / f.name, weight_type=QuantType.QInt8)
        print(f"    {f.name:<34} "
              f"{f.stat().st_size / 1024 / 1024:7.1f} MB → "
              f"{(int8 / f.name).stat().st_size / 1024 / 1024:6.1f} MB"
              f"  ({time.perf_counter() - started:.1f} 秒)")

    print()
    print("=== 3. .spm を Microsoft.ML.Tokenizers 向けに直す ===")
    from patch_spm import patch
    for name in ("source.spm", "target.spm"):
        for d in (fp32, int8):
            if (d / name).exists():
                patch(d / name)

    print()
    print(f"  fp32 合計 {size_mb(fp32):7.1f} MB")
    print(f"  int8 合計 {size_mb(int8):7.1f} MB   ← 配布するのはこちら")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        raise SystemExit(1)
    raise SystemExit(main(sys.argv[1], sys.argv[2]))
