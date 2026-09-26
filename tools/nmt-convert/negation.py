"""意味が反転する誤訳（prevent / can't / immune など）だけを集めて比べる。

61M が "prevent all damage" を「ダメージを与えることができる」と訳した例が出たため、
これが単発か傾向かを確かめる。**意味の反転はゲームでは致命的**（RULES.md ⚫ の 4 位「翻訳の正確さ」）。
"""
import sys, time
sys.stdout.reconfigure(encoding="utf-8")

LINES = [
    "Prevent all damage done to this Pokemon by attacks.",
    "During your next turn, this Pokemon can't attack.",
    "Your opponent cannot play any Item cards from their hand.",
    "This Pokemon takes no damage from attacks.",
    "You cannot use this skill while stunned.",
    "Enemies in the area are immune to fire damage.",
    "Does not stack with other defense buffs.",
    "You must not let the reactor overheat.",
]


def elan():
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer
    d = "out/elan-en-ja/int8"
    tok = AutoTokenizer.from_pretrained(d)
    m = ORTModelForSeq2SeqLM.from_pretrained(d)
    b = tok(LINES, return_tensors="pt", padding=True)
    t = time.perf_counter()
    o = m.generate(**b, num_beams=4, max_length=512)
    return (time.perf_counter()-t)*1000, tok.batch_decode(o, skip_special_tokens=True)


def mbart():
    import torch
    from transformers import MBart50TokenizerFast, MBartForConditionalGeneration
    n = "facebook/mbart-large-50-many-to-many-mmt"
    tok = MBart50TokenizerFast.from_pretrained(n, src_lang="en_XX")
    m = MBartForConditionalGeneration.from_pretrained(n); m.eval()
    torch.set_num_threads(4)
    b = tok(LINES, return_tensors="pt", padding=True)
    t = time.perf_counter()
    with torch.no_grad():
        o = m.generate(**b, num_beams=4, max_length=512,
                       forced_bos_token_id=tok.lang_code_to_id["ja_XX"])
    return (time.perf_counter()-t)*1000, tok.batch_decode(o, skip_special_tokens=True)


ms1, a = elan()
ms2, b = mbart()
print(f"  61M {ms1:.0f} ms  /  610M {ms2:.0f} ms（{ms2/ms1:.1f} 倍）\n")
for s, x, y in zip(LINES, a, b):
    print(f"  {s}")
    print(f"    61M : {x}")
    print(f"    610M: {y}")
