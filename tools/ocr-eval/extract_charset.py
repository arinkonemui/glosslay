"""認識モデルの文字辞書を YAML から抽出して 1 行 1 文字のテキストにする.

C# 側（Glosslay.Core）で CTC デコードに使う。
YAML パーサを .NET に持ち込みたくないため、ここで素のテキストに落としておく。

使い方:
    python extract_charset.py models/PP-OCRv6_small_rec_onnx/inference.yml <出力先.txt>

出力の仕様:
    - UTF-8 / LF 区切り / BOM なし
    - 1 行 1 エントリ。行番号 0 始まりが CTC のクラス番号 1 以降に対応する
      （クラス 0 は CTC の blank。辞書には含まれない）
    - 空白文字のエントリは空行になるため、読み込み側は空行を捨ててはいけない
"""

from __future__ import annotations

import sys
from pathlib import Path

import yaml


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 1

    src, dst = Path(sys.argv[1]), Path(sys.argv[2])
    config = yaml.safe_load(src.read_text(encoding="utf-8"))

    charset = config["PostProcess"]["character_dict"]
    if not isinstance(charset, list):
        print(f"character_dict がリストではありません: {type(charset)}")
        return 1

    # YAML は数字だけの項目を int として読むため、必ず文字列へ戻す。
    entries = [str(c) for c in charset]

    dst.parent.mkdir(parents=True, exist_ok=True)
    with dst.open("w", encoding="utf-8", newline="\n") as f:
        for entry in entries:
            f.write(entry + "\n")

    non_bmp = sum(1 for e in entries if any(ord(ch) > 0xFFFF for ch in e))
    multi = sum(1 for e in entries if len(e) != 1)

    print(f"出力      : {dst}")
    print(f"エントリ数: {len(entries)}  （CTC のクラス数は blank を足して {len(entries) + 1}）")
    print(f"BMP外を含む: {non_bmp} 件  ← C# では char ではなく string で扱うこと")
    print(f"2文字以上 : {multi} 件")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
