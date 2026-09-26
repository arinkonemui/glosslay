"""行を繋いでから訳した場合と、切れたまま訳した場合を並べる。

FR-TRN-10「OCR の行分割で切れた文を結合してから翻訳する」が未実装。
Gemini は番号付きの指示で補えていたが、小型 NMT にその力は無い。
どれだけ効くかを見る。
"""
import sys
sys.stdout.reconfigure(encoding="utf-8")

CASES = [
    (["Your printed body can",
      "borrow tricks from indigenous life."],
     "Your printed body can borrow tricks from indigenous life."),
    (["As long as this Pokémon is on your Bench, prevent all damage done to this",
      "Pokémon by attacks (both yours and your opponent's)."],
     "As long as this Pokémon is on your Bench, prevent all damage done to this Pokémon by attacks (both yours and your opponent's)."),
    (["* Someone ought to teach", "you how things work", "around here!"],
     "* Someone ought to teach you how things work around here!"),
    (["Search your deck for up to 2 Basic Energy cards and attach",
      "them to 1 of your Pokémon. Then, shuffle your deck."],
     "Search your deck for up to 2 Basic Energy cards and attach them to 1 of your Pokémon. Then, shuffle your deck."),
]


def main() -> None:
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    d = "out/elan-en-ja/int8"
    tok = AutoTokenizer.from_pretrained(d)
    model = ORTModelForSeq2SeqLM.from_pretrained(d)

    def tr(texts: list[str]) -> list[str]:
        batch = tok(texts, return_tensors="pt", padding=True)
        return tok.batch_decode(model.generate(**batch, num_beams=4, max_length=512),
                                skip_special_tokens=True)

    for split, whole in CASES:
        print("原文:", whole[:90])
        print("  切れたまま:")
        for s, t in zip(split, tr(split)):
            print(f"      {t}")
        print(f"  繋いでから: {tr([whole])[0]}")
        print()


if __name__ == "__main__":
    main()
