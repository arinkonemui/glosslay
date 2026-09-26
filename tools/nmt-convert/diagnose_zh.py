"""中→日が壊れた原因を、torch / ONNX fp32 / ONNX int8 で切り分ける。"""
import sys, time
sys.stdout.reconfigure(encoding="utf-8")

TEXT = ["承让了！你非常厉害，但是我更强！", "使用这个技能需要消耗30点灵力。"]
NAME = "Helsinki-NLP/opus-mt-tc-big-zh-ja"


def torch_original():
    import torch
    from transformers import AutoTokenizer, AutoModelForSeq2SeqLM
    tok = AutoTokenizer.from_pretrained(NAME)
    m = AutoModelForSeq2SeqLM.from_pretrained(NAME); m.eval()
    with torch.no_grad():
        o = m.generate(**tok(TEXT, return_tensors="pt", padding=True), num_beams=4, max_length=512)
    return tok.batch_decode(o, skip_special_tokens=True)


def onnx(d):
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer
    tok = AutoTokenizer.from_pretrained(d)
    m = ORTModelForSeq2SeqLM.from_pretrained(d)
    o = m.generate(**tok(TEXT, return_tensors="pt", padding=True), num_beams=4, max_length=512)
    return tok.batch_decode(o, skip_special_tokens=True)


for label, fn in [("1. torch（元のモデル）", torch_original),
                  ("2. ONNX fp32", lambda: onnx("out/opus-zh-ja/fp32")),
                  ("3. ONNX int8", lambda: onnx("out/opus-zh-ja/int8"))]:
    try:
        r = fn()
    except Exception as e:
        print(f"■ {label}: 失敗 {type(e).__name__} {e}"); continue
    print(f"■ {label}")
    for s, t in zip(TEXT, r):
        print(f"    {s}")
        print(f"      → {t}")
    print()
