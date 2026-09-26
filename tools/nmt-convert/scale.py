"""モデルを大きくすると精度と速度がどうなるかを実測する。

61M（elan-mt-bt）と 610M（mbart-large-50）を、同じゲーム文で比べる。
mbart は ONNX 化せず torch のまま動かす。**速度の桁を知るのが目的**。
"""
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")

LINES = [
    "Your printed body can borrow tricks from indigenous life.",
    "As long as this Pokemon is on your Bench, prevent all damage done to this Pokemon by attacks.",
    "* Someone ought to teach you how things work around here!",
    "Search your deck for up to 2 Basic Energy cards and attach them to 1 of your Pokemon.",
]


def bench_elan():
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer
    d = "out/elan-en-ja/int8"
    tok = AutoTokenizer.from_pretrained(d)
    model = ORTModelForSeq2SeqLM.from_pretrained(d)

    tok(LINES, return_tensors="pt", padding=True)  # 温め
    started = time.perf_counter()
    batch = tok(LINES, return_tensors="pt", padding=True)
    out = model.generate(**batch, num_beams=4, max_length=512)
    ms = (time.perf_counter() - started) * 1000
    return ms, tok.batch_decode(out, skip_special_tokens=True)


def bench_mbart():
    import torch
    from transformers import MBart50TokenizerFast, MBartForConditionalGeneration
    name = "facebook/mbart-large-50-many-to-many-mmt"
    tok = MBart50TokenizerFast.from_pretrained(name, src_lang="en_XX")
    model = MBartForConditionalGeneration.from_pretrained(name)
    model.eval()
    torch.set_num_threads(4)

    started = time.perf_counter()
    batch = tok(LINES, return_tensors="pt", padding=True)
    with torch.no_grad():
        out = model.generate(**batch, num_beams=4, max_length=512,
                             forced_bos_token_id=tok.lang_code_to_id["ja_XX"])
    ms = (time.perf_counter() - started) * 1000
    return ms, tok.batch_decode(out, skip_special_tokens=True)


if __name__ == "__main__":
    for label, fn, params in [("elan-mt-bt 61M (ONNX int8)", bench_elan, 61),
                              ("mbart-large-50 610M (torch fp32)", bench_mbart, 610)]:
        try:
            ms, texts = fn()
        except Exception as e:
            print(f"■ {label}: 失敗 {type(e).__name__} {e}")
            continue
        print(f"■ {label}   4 行で {ms:.0f} ms")
        for s, t in zip(LINES, texts):
            print(f"    {t}")
        print()
