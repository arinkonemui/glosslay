# tools/nmt-convert — ローカルNMT モデルの変換

ローカルNMT（FR-TRN-02 / RULES.md 🟡-1 の**既定バックエンド**）のモデルを、
配布できる形（ONNX + int8）に変換するための道具。

> **ここは変換専用です。製品（`Glosslay.App` / `Glosslay.Core`）は Python に依存しません。**
> 製品側は ONNX Runtime でモデルを直接実行します（OCR と同じ方針）。

---

## セットアップ

仮想環境は `.gitignore` で除外しているため、各自で作成してください。

```bash
py -m venv .venv
./.venv/Scripts/python.exe -m pip install --index-url https://download.pytorch.org/whl/cpu torch
./.venv/Scripts/python.exe -m pip install "optimum[onnxruntime]" transformers sentencepiece
```

`torch` は**変換のときだけ**必要です。CPU 版で足ります。

---

## 使い方

```bash
./.venv/Scripts/python.exe convert.py Mitsua/elan-mt-bt-en-ja out/elan-en-ja
```

`out/elan-en-ja/int8/` に、配布する一式（ONNX・`.spm`・`vocab.json`）が出ます。

### 計測・比較のスクリプト

| ファイル | 何を見るか |
|---|---|
| `bench.py` | 速度。実際のゲーム画面の行数で測る（PLAN.md 課題4） |
| `compare.py` | P0-7 の実サンプルを訳して並べる。質は人が読んで判断する |
| `joined.py` | **行を繋ぐ前と後**の訳を比べる（FR-TRN-10 の効き方） |
| `scale.py` | 61M と 610M の速度・質を比べる |
| `negation.py` | 否定・阻害の文だけを集めて意味の反転を見る |

> `scale.py` と `negation.py` は比較のため `facebook/mbart-large-50`（約 2.4GB）を
> 取得します。**採用しないモデルなので、普段は動かす必要はありません。**

---

## `.spm` の書き換えについて

**Marian のモデルは BOS を持たず `bos_id = -1` になっており、そのままでは
`Microsoft.ML.Tokenizers` が `IndexOutOfRangeException` で落ちます。**

`convert.py` は `patch_spm.py` を呼んで `bos_id` を `0` に書き換えます。
varint の `-1` は 10 バイト、`0` も冗長表現で 10 バイトに書けるため、
**ファイル長を変えずにその場で置換できます**（長さ接頭辞の作り直しが不要）。

読み込む側は必ずこう指定してください。

```csharp
SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: true);
```

### id の引き方に注意

**Marian は `.spm` で「分割」だけを行い、id は `vocab.json` で引きます。**
`EncodeToIds` が返す id をそのまま使うと別物になります。
`EncodeToTokens` で片を取り出し、`vocab.json` の辞書で id に直してください。

---

## ライセンス

**変換したモデルは元モデルの派生物です。** 配布時は元のライセンスに従ってください。

| モデル | ライセンス | 備考 |
|---|---|---|
| `Mitsua/elan-mt-bt-en-ja` | **CC BY-SA 4.0** | 帰属表示と**同ライセンスでの公開**が要る |
| `staka/fugumt-en-ja` | **CC BY-SA 4.0** | 同上 |
| `Helsinki-NLP/opus-mt-tc-big-zh-ja` | CC BY-4.0 | 帰属表示が要る |
| `facebook/nllb-200-*` | **CC-BY-NC** | **非商用。使用不可** |

**書き換えた `.spm` と量子化した ONNX も派生物**にあたります。

### 配布時に必ず書くこと（CC BY-SA 4.0 §3(a) の逐語要件）

ライセンス本文が挙げている項目。**1 つでも欠けると条件違反**になる。

- [ ] 作成者の表示 — **ELAN MITSUA Project / Abstract Engine**
- [ ] 著作権表示
- [ ] このライセンスを指す表示 — **CC BY-SA 4.0**
- [ ] 免責（無保証）を指す表示
- [ ] 元データへの URI — `https://huggingface.co/Mitsua/elan-mt-bt-en-ja`
- [ ] **改変したことの明示** — 「ONNX へ変換し int8 量子化」「`.spm` の `bos_id` を書き換え」
- [ ] ライセンス本文または URI の同梱

さらに §3(b)（ShareAlike）より:

- [ ] **量子化したモデルを CC BY-SA 4.0（または同等）で公開する**
- [ ] そのライセンスの本文か URI を添える
- [ ] **技術的保護手段でモデルの利用を制限しない**

> **アプリ本体のソース公開は不要。** モデルは改変せず**並べて配る**だけであり、
> アプリはモデルの派生物にあたらない。**モデルは実行ファイルに埋め込まず、
> 別ファイルとして配ること**（この立場を明確に保つため）。
