"""P0-7 の実サンプルの原文を、ローカルNMT に訳させて並べる。

「Gemini より落ちる」を印象で語らないための材料。人が読んで判断する。
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
SAMPLES = Path(r"C:\Users\arink\source\009_ゲーム翻訳アプリ\tools\ocr-eval\samples")


def lines_of(name: str) -> list[str]:
    text = (SAMPLES / f"{name}.truth.txt").read_text(encoding="utf-8")
    # 数値・記号だけの行は製品側でも翻訳に回さない（FR / 問題 C の対策と同じ）
    return [l for l in text.splitlines()
            if l.strip() and any(c.isalpha() for c in l) and len(l.strip()) > 2]


def main() -> None:
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    d = "out/elan-en-ja/int8"
    tok = AutoTokenizer.from_pretrained(d)
    model = ORTModelForSeq2SeqLM.from_pretrained(d)

    for name, title in [
        ("ref_ptcgl_daily_quest", "PTCGL デイリークエスト（Gemini の訳が記録にある）"),
        ("undertale_flowey_teach", "UNDERTALE セリフ"),
        ("ref_subnautica2_noa_log", "Subnautica 2 の長文"),
        ("ptcgl_flareon_ex", "ポケカ カードテキスト"),
    ]:
        src = lines_of(name)
        if not src:
            continue
        batch = tok(src, return_tensors="pt", padding=True)
        out = model.generate(**batch, num_beams=4, max_length=512)
        dst = tok.batch_decode(out, skip_special_tokens=True)

        print(f"■ {title}")
        for s, t in zip(src, dst):
            print(f"    {s}")
            print(f"      → {t}")
        print()


if __name__ == "__main__":
    main()
