"""中国語→日本語の質を、P0-7 の実サンプルで見る。

原文は Steam の公式スクリーンショットから起こした正解テキスト
（`ref_taiwu_menu_zh` / `ref_guigu_dialog_zh`）。
"""
import sys, time
from pathlib import Path
sys.stdout.reconfigure(encoding="utf-8")

SAMPLES = Path(r"C:\Users\arink\source\009_ゲーム翻訳アプリ\tools\ocr-eval\samples")


def lines_of(name):
    t = (SAMPLES / f"{name}.truth.txt").read_text(encoding="utf-8")
    return [l.strip() for l in t.splitlines() if l.strip() and not l.strip()[0].isdigit()]


def main():
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    d = "out/opus-zh-ja/int8"
    tok = AutoTokenizer.from_pretrained(d)
    model = ORTModelForSeq2SeqLM.from_pretrained(d)

    for name, title in [("ref_taiwu_menu_zh", "太吾绘卷 メニュー（UC-5）"),
                        ("ref_guigu_dialog_zh", "鬼谷八荒 会話（UC-1）")]:
        src = lines_of(name)
        batch = tok(src, return_tensors="pt", padding=True)
        t = time.perf_counter()
        out = model.generate(**batch, num_beams=4, max_length=512)
        ms = (time.perf_counter() - t) * 1000
        dst = tok.batch_decode(out, skip_special_tokens=True)

        print(f"■ {title}   {len(src)} 行で {ms:.0f} ms")
        for s, x in zip(src, dst):
            print(f"    {s}")
            print(f"      → {x}")
        print()

    # ゲームらしい文も足す（メニュー語だけでは文の質が分からない）
    extra = [
        "承让了！你非常厉害，但是我更强！",
        "使用这个技能需要消耗30点灵力。",
        "对目标造成120点伤害，并使其眩晕2秒。",
        "你无法在战斗中使用此物品。",
        "装备后，防御力提升15%，但移动速度降低10%。",
    ]
    batch = tok(extra, return_tensors="pt", padding=True)
    t = time.perf_counter()
    out = model.generate(**batch, num_beams=4, max_length=512)
    ms = (time.perf_counter() - t) * 1000
    print(f"■ ゲームらしい文（数値つき）   {len(extra)} 行で {ms:.0f} ms")
    for s, x in zip(extra, tok.batch_decode(out, skip_special_tokens=True)):
        print(f"    {s}")
        print(f"      → {x}")


if __name__ == "__main__":
    main()
