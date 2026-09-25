"""Marian の .spm を Microsoft.ML.Tokenizers が読める形に直す。

Marian のモデルは BOS を持たず `bos_id = -1` になっているが、
`SentencePieceUnigramModel` は BOS の存在を前提にしていて IndexOutOfRangeException で落ちる。

varint の -1 は 10 バイト、0 も冗長表現で 10 バイトに書けるため、
**ファイル長を変えずにその場で置換できる**（長さ接頭辞を作り直さなくてよい）。

読み込み側は `addBeginningOfSentence: false` を指定すること。0 番の片は出力されない。
"""
import sys
from pathlib import Path

BOS_KEY = bytes([0xC8, 0x02])            # field 41 (bos_id), wire type 0
MINUS_ONE = bytes([0xFF] * 9 + [0x01])   # -1 の 10 バイト表現
ZERO_10 = bytes([0x80] * 9 + [0x00])     # 0 の冗長な 10 バイト表現（同じ長さ）


def patch(path: Path) -> bool:
    data = bytearray(path.read_bytes())
    i = data.find(BOS_KEY + MINUS_ONE)
    if i < 0:
        print(f"  {path.name}: bos_id = -1 が見つからない（既に直っているか、形式が違う）")
        return False

    before = len(data)
    data[i + len(BOS_KEY): i + len(BOS_KEY) + len(ZERO_10)] = ZERO_10
    assert len(data) == before, "ファイル長が変わった"
    path.write_bytes(data)
    print(f"  {path.name}: bos_id を -1 → 0 に書き換えた（{before:,} バイトのまま）")
    return True


if __name__ == "__main__":
    targets = [Path(a) for a in sys.argv[1:]]
    if not targets:
        print("使い方: python patch_spm.py <source.spm> [target.spm ...]")
        raise SystemExit(1)
    for t in targets:
        patch(t)
