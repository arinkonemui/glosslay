"""OCR ランタイムの速度比較（PLAN.md P0-4 の判断根拠）.

「PaddleOCR を .NET からどう動かすか」を決めるための計測。

    案A: ONNX Runtime で直接実行
    案B: 別プロセス化（Python / PaddlePaddle ランタイム）

2026-09-09 の計測では案A が約 8 倍速く、要件（トリガーから表示まで 1.5 秒以内）を
満たす唯一の選択肢だったため案A を採用した。結果の要約は PLAN.md P0-4 を参照。

前提:
    python -m venv .venv && .venv/Scripts/pip install paddleocr onnxruntime pillow
    ONNX モデルは models/ に配置（README.md 参照）

使い方:
    python bench_runtime.py <計測に使う画像>
"""

from __future__ import annotations

import argparse
import io
import sys
import time
import warnings
from pathlib import Path

warnings.filterwarnings("ignore")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

import numpy as np  # noqa: E402
from PIL import Image  # noqa: E402

DET_ONNX = Path("models/PP-OCRv6_small_det_onnx/inference.onnx")
REC_ONNX = Path("models/PP-OCRv6_small_rec_onnx/inference.onnx")

# ImageNet の正規化パラメータ。PP-OCR の検出前処理はこれを使う。
MEAN = np.array([0.485, 0.456, 0.406], np.float32)
STD = np.array([0.229, 0.224, 0.225], np.float32)


def bench_paddle(image_path: Path, threads: int) -> tuple[float, int]:
    """案B: PaddlePaddle ランタイム。検出から認識まで一式を測る."""
    from paddleocr import PaddleOCR

    ocr = PaddleOCR(
        text_detection_model_name="PP-OCRv6_small_det",
        text_recognition_model_name="PP-OCRv6_small_rec",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        # oneDNN は paddlepaddle 3.3.1 で有効にするとクラッシュする
        # （NotImplementedError: ConvertPirAttribute2RuntimeAttribute not support）
        enable_mkldnn=False,
        cpu_threads=threads,
    )
    ocr.predict(str(image_path))  # ウォームアップ

    start = time.perf_counter()
    results = ocr.predict(str(image_path))
    elapsed_ms = (time.perf_counter() - start) * 1000

    payload = results[0].json
    payload = payload.get("res", payload)
    return elapsed_ms, len(payload.get("rec_texts", []))


def bench_onnx(image_path: Path, threads: int, lines: int) -> tuple[float, float]:
    """案A: ONNX Runtime。純粋な推論時間のみで、DB 後処理と CTC デコードは含まない."""
    import onnxruntime as ort

    def make_session(path: Path):
        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        return ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])

    # --- 検出: 入力は 32 の倍数に丸める ---
    image = Image.open(image_path).convert("RGB")
    width, height = (image.width // 32) * 32, (image.height // 32) * 32
    array = np.asarray(image.resize((width, height)), dtype=np.float32) / 255.0
    det_input = ((array - MEAN) / STD).transpose(2, 0, 1)[None]

    det = make_session(DET_ONNX)
    det_name = det.get_inputs()[0].name
    det.run(None, {det_name: det_input})

    start = time.perf_counter()
    for _ in range(3):
        det.run(None, {det_name: det_input})
    det_ms = (time.perf_counter() - start) / 3 * 1000

    # --- 認識: 高さ 48 の行画像を lines 行ぶん逐次投入 ---
    # 実測ではバッチ投入のほうが遅かったため逐次で測る。
    rec = make_session(REC_ONNX)
    rec_name = rec.get_inputs()[0].name
    crop = np.random.rand(1, 3, 48, 320).astype(np.float32)
    rec.run(None, {rec_name: crop})

    start = time.perf_counter()
    for _ in range(lines):
        rec.run(None, {rec_name: crop})
    rec_ms = (time.perf_counter() - start) * 1000

    return det_ms, rec_ms


def main() -> int:
    parser = argparse.ArgumentParser(description="OCR ランタイムの速度比較")
    parser.add_argument("image", type=Path, help="計測に使う画像")
    parser.add_argument("--threads", type=int, default=4, help="CPU スレッド数")
    args = parser.parse_args()

    image = Image.open(args.image)
    print(f"画像: {args.image}  {image.width}x{image.height}  / CPU {args.threads} スレッド\n")

    paddle_ms, lines = bench_paddle(args.image, args.threads)
    print(f"案B PaddlePaddle    検出+認識 一式   {paddle_ms:8.1f} ms  （{lines} 行）")

    det_ms, rec_ms = bench_onnx(args.image, args.threads, lines)
    print(f"案A ONNX Runtime    検出            {det_ms:8.1f} ms")
    print(f"案A ONNX Runtime    認識 {lines} 行 逐次  {rec_ms:8.1f} ms")
    print(f"案A ONNX Runtime    合計            {det_ms + rec_ms:8.1f} ms"
          f"  ← 後処理は未計上")
    print(f"\n案A は案B の約 {paddle_ms / (det_ms + rec_ms):.1f} 倍速い")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
