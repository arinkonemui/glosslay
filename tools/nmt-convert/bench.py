"""変換したローカルNMT の速度を、実際のゲーム文で測る（PLAN.md 課題4）。

性能要件は「手動翻訳のトリガーから表示まで 1.5 秒以内」。
クラウド翻訳ではなく**既定バックエンド**で判定すべきなので、ここで測る。
"""
import sys
import time
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

# P0-7 のサンプルから採った実際の画面テキスト
SHORT = ["Attack Power", "30 damage"]

SCREEN = [
    "STAGE 1", "Flareon ex", "HP 270", "Evolves from Eevee",
    "As long as this Pokemon is on your Bench, prevent all damage done to this",
    "Pokemon by attacks (both yours and your opponent's).",
    "Burning Charge",
    "Search your deck for up to 2 Basic Energy cards and attach",
    "them to 1 of your Pokemon. Then, shuffle your deck.",
    "Carnelian",
    "During your next turn, this Pokemon can't attack.",
    "Pokemon ex rule", "When your Pokemon ex",
    "is Knocked Out, your opponent takes 2 Prize cards.",
]

LONG = [
    "You are burning in slow motion. Under 60 atmospheres of pressure,",
    "oxygen becomes a neurotoxin. Nitrogen bubbles saturate your flesh and",
    "pry your joints apart.",
    "I've opened the door to the adaptation chamber. Your printed body can",
    "borrow tricks from indigenous life. You'll feel better. You'll be better.",
    "The last pioneer didn't make it to the lifepod. I'm activating their blackbox",
    "signal. You will make it. That's why you're the QI.",
]


def run(model_dir: str, threads: int) -> None:
    import onnxruntime
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    options = onnxruntime.SessionOptions()
    options.intra_op_num_threads = threads
    options.inter_op_num_threads = 1

    print(f"=== {model_dir}  スレッド {threads} ===")
    started = time.perf_counter()
    tok = AutoTokenizer.from_pretrained(model_dir)
    model = ORTModelForSeq2SeqLM.from_pretrained(model_dir, session_options=options)
    print(f"  読み込み {time.perf_counter() - started:.2f} 秒")

    for label, lines, beams in [
        ("短い 2 行", SHORT, 1),
        ("カード 1 画面 15 行", SCREEN, 1),
        ("長文 7 行", LONG, 1),
        ("カード 1 画面 15 行（ビーム 4）", SCREEN, 4),
    ]:
        # 1 回目は初期化を含むので捨てる
        for attempt in range(2):
            started = time.perf_counter()
            batch = tok(lines, return_tensors="pt", padding=True)
            out = model.generate(**batch, num_beams=beams, max_length=512)
            elapsed = (time.perf_counter() - started) * 1000
        texts = tok.batch_decode(out, skip_special_tokens=True)
        mark = "" if elapsed <= 1500 else "  ★ 1.5 秒超過"
        print(f"  {label:<30} {elapsed:7.0f} ms{mark}")
        print(f"      例: {texts[0][:56]}")

    print()


if __name__ == "__main__":
    d = sys.argv[1] if len(sys.argv) > 1 else "out/elan-en-ja/int8"
    for t in (2, 4):
        run(d, t)
