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

## 既知の落とし穴

| 事象 | 内容 |
|---|---|
| oneDNN が使えない | paddlepaddle 3.3.1 で `enable_mkldnn=True` にすると `NotImplementedError: ConvertPirAttribute2RuntimeAttribute not support` でクラッシュする。案B の計測値はこの高速化を切った状態のもの |
| `paddle2onnx` が動かない | リリース版 paddlepaddle と DLL のシンボルが合わず、nightly 版を要求される。**公式が ONNX 版を配布しているため自前変換は不要** |
| `onnx` が Python 3.13 に非対応 | ソースビルドに cmake が必要。`ocr_eval.py` / `bench_runtime.py` は onnx 本体を使わないため 3.13 でも動く |
| PP-OCRv6_tiny は日本語非対応 | 最小構成を狙う際の注意点。small 以上を使うこと |
| 韓国語は別モデル | 既定の認識モデルは 英・中・日 まで。韓国語は専用モデル（+13MB 程度）が要る |
