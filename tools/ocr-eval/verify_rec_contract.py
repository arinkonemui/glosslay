"""認識モデルの入出力の約束事を実測で確定させる（C# 実装の前提）.

C# で CTC デコードを書くには、次の 2 つを推測でなく確定させる必要がある。

  1. 前処理: 画素値をどう正規化して入力テンソルにするか
  2. 出力の並び: クラス番号と文字辞書の対応（blank と空白文字の位置）

PaddleOCR（Python）が出す正解テキストと、ONNX を直接叩いた結果を突き合わせて確認する。

使い方:
    python verify_rec_contract.py
"""

from __future__ import annotations

import io
import sys
import warnings
from pathlib import Path

warnings.filterwarnings("ignore")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

import numpy as np  # noqa: E402
import onnxruntime as ort  # noqa: E402
from PIL import Image, ImageDraw, ImageFont  # noqa: E402

REC_ONNX = Path("models/PP-OCRv6_small_rec_onnx/inference.onnx")
CHARSET = Path("models/ppocrv6_charset.txt")
REC_HEIGHT = 48

SAMPLE_TEXT = "Damage 50 Cooldown 12s"


def make_sample(path: Path) -> None:
    """検証用に、文字が分かっている画像を作る."""
    image = Image.new("RGB", (520, 90), (20, 24, 34))
    draw = ImageDraw.Draw(image)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 40)
    except OSError:
        font = ImageFont.load_default()
    draw.text((16, 22), SAMPLE_TEXT, font=font, fill=(235, 238, 245))
    image.save(path)


def load_charset() -> list[str]:
    # 空白文字のエントリが空行になりうるので、行末の改行だけを外す。
    return CHARSET.read_text(encoding="utf-8").split("\n")[:-1]


def preprocess(image: Image.Image, mode: str, width: int = 320) -> np.ndarray:
    """高さ 48 に合わせ、アスペクト比を保って右側を 0 で埋める."""
    ratio = image.width / image.height
    w = min(width, max(1, int(np.ceil(REC_HEIGHT * ratio))))
    resized = image.resize((w, REC_HEIGHT), Image.BILINEAR)

    array = np.asarray(resized, dtype=np.float32)[:, :, ::-1]  # RGB -> BGR
    if mode == "pm1":
        array = (array / 255.0 - 0.5) / 0.5      # [-1, 1]
    elif mode == "zero_one":
        array = array / 255.0                     # [0, 1]
    else:
        raise ValueError(mode)

    chw = array.transpose(2, 0, 1)
    padded = np.zeros((3, REC_HEIGHT, width), dtype=np.float32)
    padded[:, :, :w] = chw
    return padded[None]


def ctc_greedy(logits: np.ndarray, charset: list[str], blank_first: bool) -> tuple[str, float]:
    """CTC の貪欲デコード。連続する同一クラスを畳み、blank を捨てる."""
    probs = logits[0]                      # (T, C)
    indices = probs.argmax(axis=1)
    scores = probs.max(axis=1)

    out: list[str] = []
    picked: list[float] = []
    previous = -1
    for i, idx in enumerate(indices):
        if idx != previous and idx != 0:
            # クラス 0 が blank。辞書はクラス 1 から始まる。
            pos = idx - 1 if blank_first else idx
            if 0 <= pos < len(charset):
                out.append(charset[pos])
            elif pos == len(charset):
                out.append(" ")            # 辞書の次のクラスは空白文字という仮説
            picked.append(float(scores[i]))
        previous = idx
    confidence = float(np.mean(picked)) if picked else 0.0
    return "".join(out), confidence


def main() -> int:
    sample = Path("_verify_sample.png")
    make_sample(sample)
    charset = load_charset()

    session = ort.InferenceSession(str(REC_ONNX), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    classes = session.get_outputs()[0].shape[-1]

    print(f"正解テキスト   : {SAMPLE_TEXT}")
    print(f"辞書エントリ数 : {len(charset)}")
    print(f"出力クラス数   : {classes}")
    print(f"差分           : {classes - len(charset)}  （blank と空白文字のぶん）")
    print()

    # PaddleOCR 本体の結果を正解として使う
    from paddleocr import PaddleOCR

    ocr = PaddleOCR(
        text_detection_model_name="PP-OCRv6_small_det",
        text_recognition_model_name="PP-OCRv6_small_rec",
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        enable_mkldnn=False,
    )
    payload = ocr.predict(str(sample))[0].json
    payload = payload.get("res", payload)
    reference = payload.get("rec_texts", [])
    print(f"PaddleOCR の出力: {reference}")
    print()

    # 検出された行を切り出して、ONNX へ直接投入する
    polys = payload.get("rec_polys", payload.get("dt_polys", []))
    if not polys:
        print("行が検出できなかったため検証できません")
        return 1

    poly = np.array(polys[0])
    x0, y0 = poly[:, 0].min(), poly[:, 1].min()
    x1, y1 = poly[:, 0].max(), poly[:, 1].max()
    crop = Image.open(sample).convert("RGB").crop((int(x0), int(y0), int(x1), int(y1)))

    print("--- 前処理とクラス対応の組み合わせを総当たり ---")
    for mode in ("pm1", "zero_one"):
        tensor = preprocess(crop, mode)
        logits = session.run(None, {input_name: tensor})[0]
        for blank_first in (True, False):
            text, score = ctc_greedy(logits, charset, blank_first)
            ok = "  ← 一致" if reference and text.strip() == reference[0].strip() else ""
            label = f"正規化={mode:9s} blank=先頭" if blank_first else f"正規化={mode:9s} blank=なし"
            print(f"  {label}  score={score:.3f}  {text!r}{ok}")

    sample.unlink(missing_ok=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
