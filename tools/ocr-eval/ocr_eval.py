"""PaddleOCR による OCR 精度・速度の計測ツール（v0.1 PoC 用）.

PLAN.md P0-7「精度計測」で使う。1 枚の画像に対して OCR を実行し、
認識テキスト・信頼度・バウンディングボックス・処理時間を JSON に落とし、
確認用に枠を描いた画像も出力する。

正解テキストとの突き合わせは人手で行う（PLAN.md の 3 段階判定）。
本ツールは「機械が出した生の結果」を再現可能な形で残すところまでを担当する。

使い方:
    python ocr_eval.py <画像またはディレクトリ> [オプション]

例:
    python ocr_eval.py captures/                       # 既定（PP-OCRv6 small）
    python ocr_eval.py card.png --scale 3              # 3倍に拡大してから OCR
    python ocr_eval.py card.png --version PP-OCRv5     # 旧世代と比較する
"""

from __future__ import annotations

import argparse
import io
import json
import sys
import time
import warnings
from dataclasses import asdict, dataclass
from pathlib import Path

warnings.filterwarnings("ignore")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

from PIL import Image, ImageDraw  # noqa: E402

IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".bmp", ".webp"}


@dataclass
class Line:
    """OCR が認識した 1 行."""

    index: int
    text: str
    score: float
    box: list[list[int]]  # 四隅 [[x,y], ...]


@dataclass
class Result:
    """1 枚分の計測結果."""

    image: str
    width: int
    height: int
    ocr_version: str
    det_model: str | None
    rec_model: str | None
    scale: float
    cpu_threads: int
    enable_mkldnn: bool
    elapsed_ms: float
    line_count: int
    lines: list[Line]


def build_engine(args: argparse.Namespace):
    """PaddleOCR を組み立てる.

    ゲーム画面はスキャン文書ではないため、傾き補正・歪み補正・行方向判定はすべて切る。
    速度に効くうえ、切っても精度は落ちない。
    """
    from paddleocr import PaddleOCR

    kwargs: dict = {
        "use_doc_orientation_classify": False,
        "use_doc_unwarping": False,
        "use_textline_orientation": False,
        "enable_mkldnn": args.mkldnn,
        "cpu_threads": args.threads,
    }
    if args.version:
        kwargs["ocr_version"] = args.version
    if args.det_model:
        kwargs["text_detection_model_name"] = args.det_model
    if args.rec_model:
        kwargs["text_recognition_model_name"] = args.rec_model
    return PaddleOCR(**kwargs)


def run_one(ocr, path: Path, args: argparse.Namespace, out_dir: Path) -> Result:
    image = Image.open(path).convert("RGB")

    # FR-OCR-04: OCR 前の拡大。小さい UI 文字で最も効果が大きいとされる前処理。
    if args.scale != 1.0:
        image = image.resize(
            (int(image.width * args.scale), int(image.height * args.scale)),
            Image.LANCZOS,
        )

    import numpy as np

    array = np.array(image)[:, :, ::-1]  # RGB -> BGR

    start = time.perf_counter()
    results = ocr.predict(array)
    elapsed_ms = (time.perf_counter() - start) * 1000

    payload = results[0].json
    payload = payload.get("res", payload)

    texts = payload.get("rec_texts", [])
    scores = payload.get("rec_scores", [])
    polys = payload.get("rec_polys", payload.get("dt_polys", []))

    lines = [
        Line(
            index=i,
            text=t,
            score=round(float(s), 4),
            box=[[int(x), int(y)] for x, y in poly],
        )
        for i, (t, s, poly) in enumerate(zip(texts, scores, polys))
    ]

    result = Result(
        image=str(path),
        width=image.width,
        height=image.height,
        ocr_version=args.version or "(既定)",
        det_model=args.det_model,
        rec_model=args.rec_model,
        scale=args.scale,
        cpu_threads=args.threads,
        enable_mkldnn=args.mkldnn,
        elapsed_ms=round(elapsed_ms, 1),
        line_count=len(lines),
        lines=lines,
    )

    out_dir.mkdir(parents=True, exist_ok=True)
    stem = path.stem

    (out_dir / f"{stem}.json").write_text(
        json.dumps(asdict(result), ensure_ascii=False, indent=2), encoding="utf-8"
    )

    # 確認用に枠と連番を描いた画像を出す。JSON の index と対応する。
    annotated = image.copy()
    draw = ImageDraw.Draw(annotated)
    for line in lines:
        points = [tuple(p) for p in line.box]
        draw.polygon(points, outline=(255, 0, 0), width=2)
        draw.text((points[0][0], max(0, points[0][1] - 12)), str(line.index), fill=(255, 0, 0))
    annotated.save(out_dir / f"{stem}_annotated.png")

    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="PaddleOCR の精度・速度計測")
    parser.add_argument("target", type=Path, help="画像ファイル、またはそれを含むディレクトリ")
    parser.add_argument("--out", type=Path, default=Path("results"), help="出力先（既定: results）")
    parser.add_argument("--version", default=None, help='ocr_version 例: PP-OCRv6 / PP-OCRv5')
    parser.add_argument("--det-model", default=None, help="検出モデル名を明示する")
    parser.add_argument("--rec-model", default=None, help="認識モデル名を明示する")
    parser.add_argument("--scale", type=float, default=1.0, help="OCR 前の拡大倍率（FR-OCR-04）")
    parser.add_argument("--threads", type=int, default=4, help="CPU スレッド数（FR-OCR-11）")
    parser.add_argument(
        "--mkldnn",
        action=argparse.BooleanOptionalAction,
        default=False,
        help="oneDNN を使う。paddlepaddle 3.3.1 では有効にするとクラッシュする",
    )
    args = parser.parse_args()

    if args.target.is_dir():
        paths = sorted(p for p in args.target.iterdir() if p.suffix.lower() in IMAGE_SUFFIXES)
    else:
        paths = [args.target]

    if not paths:
        print(f"画像が見つかりません: {args.target}")
        return 1

    print(f"対象 {len(paths)} 件 / モデル {args.version or '(既定)'} / 拡大 {args.scale}x")
    ocr = build_engine(args)

    for path in paths:
        try:
            result = run_one(ocr, path, args, args.out)
        except Exception as exc:  # 1 枚失敗しても残りは続ける
            print(f"  {path.name}: 失敗 {type(exc).__name__}: {exc}")
            continue

        print(f"\n=== {path.name}  {result.width}x{result.height}"
              f"  {result.elapsed_ms:.0f} ms  {result.line_count} 行 ===")
        for line in result.lines:
            mark = "  " if line.score >= 0.9 else ("△ " if line.score >= 0.7 else "× ")
            print(f"  {mark}[{line.index:3d}] {line.score:.3f}  {line.text}")

    print(f"\n出力先: {args.out.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
