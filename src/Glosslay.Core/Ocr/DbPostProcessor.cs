namespace Glosslay.Ocr;

/// <summary>
/// 検出モデルが出す確率マップから、文字領域の矩形を取り出す（DB の後処理）。
/// </summary>
/// <remarks>
/// <para>PaddleOCR の <c>DBPostProcess</c> に相当する。既定のパラメータはモデル同梱の
/// <c>inference.yml</c> の値（thresh 0.2 / box_thresh 0.45 / unclip_ratio 1.4）。</para>
/// <para><b>本家との違い</b>: 本家は輪郭追跡と最小面積の回転矩形を求めるが、
/// ここでは連結成分の外接矩形（軸に平行）で代用している。
/// 対象が横書きのゲーム UI テキストに限られ（SPEC.md §1 で縦書きは対象外）、
/// 回転矩形を扱うには輪郭追跡・凸包・回転キャリパー・多角形オフセットが必要で、
/// それに見合う利点がないため。斜めのテキストを扱う必要が出たら作り直すこと。</para>
/// </remarks>
public sealed class DbPostProcessor
{
    /// <summary>二値化の閾値。これを超えた画素を文字領域の候補とする。</summary>
    public float Threshold { get; init; } = 0.2f;

    /// <summary>領域の平均確率がこれ未満なら捨てる。</summary>
    public float BoxThreshold { get; init; } = 0.45f;

    /// <summary>矩形を広げる比率。文字の縁が切れるのを防ぐ。</summary>
    public float UnclipRatio { get; init; } = 1.4f;

    /// <summary>短辺がこれ未満の領域は捨てる（ノイズ除去）。</summary>
    public int MinSize { get; init; } = 3;

    /// <summary>取り出す領域数の上限。</summary>
    public int MaxCandidates { get; init; } = 3000;

    /// <summary>
    /// 確率マップから矩形を取り出す。
    /// </summary>
    /// <param name="probabilities">形状 <c>[mapHeight, mapWidth]</c> の確率マップ。</param>
    /// <param name="mapWidth">確率マップの幅（＝検出モデルへの入力幅）。</param>
    /// <param name="mapHeight">確率マップの高さ。</param>
    /// <param name="sourceWidth">元画像の幅。結果はこの座標系へ戻す。</param>
    /// <param name="sourceHeight">元画像の高さ。</param>
    public IReadOnlyList<(OcrBox Box, float Score)> Extract(
        ReadOnlySpan<float> probabilities,
        int mapWidth,
        int mapHeight,
        int sourceWidth,
        int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapHeight);

        var pixelCount = mapWidth * mapHeight;
        if (probabilities.Length < pixelCount)
        {
            throw new ArgumentException(
                $"確率マップの要素数が足りません。期待 {pixelCount}、実際 {probabilities.Length}。",
                nameof(probabilities));
        }

        var results = new List<(OcrBox, float)>();
        var visited = new bool[pixelCount];
        var stack = new Stack<int>();

        var scaleX = (float)sourceWidth / mapWidth;
        var scaleY = (float)sourceHeight / mapHeight;

        for (var seed = 0; seed < pixelCount; seed++)
        {
            if (visited[seed] || probabilities[seed] <= Threshold)
            {
                continue;
            }

            // 連結成分を塗りつぶしながら外接矩形を求める（8近傍）。
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            stack.Push(seed);
            visited[seed] = true;

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var x = index % mapWidth;
                var y = index / mapWidth;

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= mapHeight)
                    {
                        continue;
                    }

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= mapWidth)
                        {
                            continue;
                        }

                        var neighbour = (ny * mapWidth) + nx;
                        if (!visited[neighbour] && probabilities[neighbour] > Threshold)
                        {
                            visited[neighbour] = true;
                            stack.Push(neighbour);
                        }
                    }
                }
            }

            var width = maxX - minX + 1;
            var height = maxY - minY + 1;
            if (Math.Min(width, height) < MinSize)
            {
                continue;
            }

            var score = MeanProbability(probabilities, mapWidth, minX, minY, maxX, maxY);
            if (score < BoxThreshold)
            {
                continue;
            }

            var expanded = Unclip(minX, minY, width, height, mapWidth, mapHeight);
            results.Add((ToSourceCoordinates(expanded, scaleX, scaleY, sourceWidth, sourceHeight), score));

            if (results.Count >= MaxCandidates)
            {
                break;
            }
        }

        return results;
    }

    private static float MeanProbability(
        ReadOnlySpan<float> probabilities, int mapWidth, int minX, int minY, int maxX, int maxY)
    {
        var sum = 0.0;
        var count = 0;
        for (var y = minY; y <= maxY; y++)
        {
            var rowStart = y * mapWidth;
            for (var x = minX; x <= maxX; x++)
            {
                sum += probabilities[rowStart + x];
                count++;
            }
        }

        return count == 0 ? 0f : (float)(sum / count);
    }

    /// <summary>
    /// 矩形を広げる。本家の多角形オフセットを矩形向けに簡略化したもの。
    /// 広げる距離 = 面積 × 比率 ÷ 周長。
    /// </summary>
    private (int X, int Y, int Width, int Height) Unclip(
        int x, int y, int width, int height, int mapWidth, int mapHeight)
    {
        var area = (double)width * height;
        var perimeter = 2.0 * (width + height);
        var distance = (int)Math.Round(area * UnclipRatio / perimeter);

        var left = Math.Max(0, x - distance);
        var top = Math.Max(0, y - distance);
        var right = Math.Min(mapWidth - 1, x + width - 1 + distance);
        var bottom = Math.Min(mapHeight - 1, y + height - 1 + distance);

        return (left, top, right - left + 1, bottom - top + 1);
    }

    private static OcrBox ToSourceCoordinates(
        (int X, int Y, int Width, int Height) box,
        float scaleX,
        float scaleY,
        int sourceWidth,
        int sourceHeight)
    {
        var left = (int)Math.Floor(box.X * scaleX);
        var top = (int)Math.Floor(box.Y * scaleY);
        var right = (int)Math.Ceiling((box.X + box.Width) * scaleX);
        var bottom = (int)Math.Ceiling((box.Y + box.Height) * scaleY);

        left = Math.Clamp(left, 0, sourceWidth);
        top = Math.Clamp(top, 0, sourceHeight);
        right = Math.Clamp(right, left, sourceWidth);
        bottom = Math.Clamp(bottom, top, sourceHeight);

        return new OcrBox(left, top, right - left, bottom - top);
    }
}
