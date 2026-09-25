# tools/ocr-eval — OCR の精度・速度計測

v0.1 PoC の **P0-4（OCR）** と **P0-7（精度計測）** で使う Python 側の検証ツール。

> ここは検証専用です。**製品（`Glosslay.App` / `Glosslay.Core`）は Python に依存しません。**
> 製品側は ONNX Runtime でモデルを直接実行します（PLAN.md P0-4 の決定）。

---

## セットアップ

仮想環境と DL 済みモデルは `.gitignore` で除外しているため、各自で作成してください。

```bash
py -m venv .venv
./.venv/Scripts/python.exe -m pip install paddleocr onnxruntime pillow
```

ONNX モデル（公式配布・Apache 2.0）を `models/` に取得します。

```bash
./.venv/Scripts/python.exe -c "
from huggingface_hub import snapshot_download
for r in ['PaddlePaddle/PP-OCRv6_small_det_onnx','PaddlePaddle/PP-OCRv6_small_rec_onnx']:
    snapshot_download(repo_id=r, local_dir='models/'+r.split('/')[1])
"
```

PaddlePaddle ランタイム側のモデルは `paddleocr` の初回実行時に
`%USERPROFILE%\.paddlex\official_models\` へ自動でダウンロードされます。

---

## `ocr_eval.py` — 精度計測（P0-7）

画像を OCR にかけ、**認識テキスト・信頼度・バウンディングボックス・処理時間**を JSON に落とし、
枠と連番を描いた確認用画像も出力します。正解テキストとの突き合わせは人手で行います
（PLAN.md の 3 段階判定）。

```bash
# %APPDATA%\Glosslay\captures\ に貯めたキャプチャをまとめて処理する
./.venv/Scripts/python.exe ocr_eval.py "$APPDATA/Glosslay/captures" --out results

# 小さい UI 文字は拡大が効く（FR-OCR-04）。倍率を変えて比較する
./.venv/Scripts/python.exe ocr_eval.py card.png --scale 3

# 世代を変えて比較する
./.venv/Scripts/python.exe ocr_eval.py card.png --version PP-OCRv5
```

出力は `results/<名前>.json` と `results/<名前>_annotated.png`。
JSON の `index` と画像に描かれた番号が対応します。

---

## `bench_runtime.py` — ランタイムの速度比較（P0-4）

「ONNX Runtime で直接実行する」か「別プロセス化する」かを決めるための計測です。

```bash
./.venv/Scripts/python.exe bench_runtime.py <画像>
```

### 2026-09-09 の計測結果

領域 700x420 / 30 行 / CPU 4 スレッド / PP-OCRv6_small。

| ランタイム | 時間 | 要件 1.5 秒 |
|---|---|---|
| **ONNX Runtime** | **約 0.43 秒**（検出 165ms + 認識30行 269ms） | 満たす |
| PaddlePaddle | 2.66 秒 | 超過 |

**この結果から案A（ONNX Runtime 直接実行）を採用しました。**
配布面でも、案B は Python ランタイムの同梱が必要になり、
配布サイズと AV 誤検知の両方で不利になります。

> ONNX 側の数値は純粋な推論時間で、DB 後処理と CTC デコードを含みません。
> C# 実装後に再計測してください。

---

## `OcrBench` — 前処理の効き方の計測（P0-3 / P0-7）

**製品と同じコード（`Glosslay.Core`）を通して**、前処理の設定を変えながら精度を比較します。
Python 側の `ocr_eval.py` は PaddleOCR 本家の挙動を見るためのもので、役割が違います。

### サンプルの置き方

`samples/` に、画像と同じ名前の正解ファイルを並べます。

```
samples/
  galvantula_ex.png         ← ゲーム画面のキャプチャ（.gitignore で除外）
  galvantula_ex.truth.txt   ← 正解テキスト。1 行 1 要素（コミットする）
```

> **ゲーム画面はゲームの著作物なのでコミットしません。**
> 正解テキストは自分で書き起こしたものなので残します。

キャプチャは、テスト UI の「PNG で保存」で `%APPDATA%\Glosslay\captures\` に出したものを
`samples/` へ移し、名前を合わせてください。

### `ref_` で始まるサンプルは参考値

**`ref_` を付けたものは P0-7 の合否判定に使いません。** Web から拾った縮小画像など、
実際のキャプチャ経路を通っていないものに付けます。

理由は解像度。PP-OCRv6 は短辺が 736px 未満なら 736 まで内部で拡大するため、
**既に潰れた文字を引き伸ばしても情報は戻らない。** JPEG 再圧縮のノイズも
WGC のキャプチャには存在しない劣化で、実態より悪い数字が出る。

参考値の読み方には制限がある。

| 結果 | 言えること |
|---|---|
| 高い | **意味がある。** 低解像度で読めるなら実解像度ではもっと読める |
| 低い | **何も言えない。** フォントのせいか解像度のせいか切り分けられない |

**ゲーム画面以外（Web サイト・ランチャー）は `ref_` を付けても計測対象にしない。**
UI の描画方式が違い、ゲームの難しさを代表しないため。

### 実行

```bash
dotnet run --project tools/ocr-eval/OcrBench
```

引数でディレクトリを指定することもできます。

```bash
dotnet run --project tools/ocr-eval/OcrBench -- <サンプルのディレクトリ> <モデルのディレクトリ>
```

出力は、設定ごとの **一致度**（編集距離ベース）、**数値の正解数**、**余分な数値**、検出行数、処理時間です。
数値は RULES.md が最重視するため個別に数えています。

| 列 | 意味 |
|---|---|
| 数値 | 正解に含まれる数値のうち、読めた数 |
| **余分** | **読み取った結果にあって、正解に無い数値の数。1 以上なら ★ を付け、一致度に関わらず失敗扱い** |

「余分」は 2026-09-20 に追加した。Pokémon TCG Live のクエストで ⚡（雷エネルギーのアイコン）が数字の「4」と読まれ、
**原文に無い数値**が生まれたが、「数値」の列は満点（6/6）のままで見逃していた。

最も成績の良い設定の**認識結果と正解を並べて表示**します。P0-7 の 3 段階判定（✅ / ⚠️ / ❌）はこれを見て人が行います。
各行には、行の信頼度に加えて**その行で最も自信の無かった 1 文字**（`最小 4 0.48`）を出します。
行の信頼度は平均のため、1 文字だけ誤読していても高いままになるためです。

> **「最小」で誤読を機械的に判定することはできません。** 正常な文字が 0.39 まで下がる一方、
> アイコンを誤読した「4」は前処理次第で 0.94 まで上がり、分布が重なります（PLAN.md 課題6 の計測）。
> 人が結果を読むときの手がかりとして出しています。

## 既知の落とし穴

| 事象 | 内容 |
|---|---|
| oneDNN が使えない | paddlepaddle 3.3.1 で `enable_mkldnn=True` にすると `NotImplementedError: ConvertPirAttribute2RuntimeAttribute not support` でクラッシュする。案B の計測値はこの高速化を切った状態のもの |
| `paddle2onnx` が動かない | リリース版 paddlepaddle と DLL のシンボルが合わず、nightly 版を要求される。**公式が ONNX 版を配布しているため自前変換は不要** |
| `onnx` が Python 3.13 に非対応 | ソースビルドに cmake が必要。`ocr_eval.py` / `bench_runtime.py` は onnx 本体を使わないため 3.13 でも動く |
| PP-OCRv6_tiny は日本語非対応 | 最小構成を狙う際の注意点。small 以上を使うこと |
| 韓国語は読めない | 辞書にハングルが **0 字**。既定の認識モデルは 英・中・日 まで。**2026-09-26 に初期対応から除外**（SPEC.md FR-OCR-02）。専用モデル（+13MB 程度）を足せば対応できる |
| 認識結果を Y 座標だけで並べない | 同じ行の要素が 1px のずれで入れ替わり、文字列比較が壊れる。縦位置が近いものを行にまとめ、行内を X で並べること。2026-09-13 にこれで「前処理が精度を下げた」という誤った結論を出した |
