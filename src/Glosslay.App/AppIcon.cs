using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glosslay;

/// <summary>
/// アプリのアイコンを描く（タスクトレイと操作画面のタイトルバー）。
/// </summary>
/// <remarks>
/// <para><b>仮のアイコン。</b>まだアイコンの画像が無いため、コードで描いている。
/// 正式な画像を用意したら差し替えること（配布物の見た目に関わるため、デザインは人間が決める）。</para>
/// <para><b>濃い色の地に白い文字。</b>タスクバーは明るい色にも暗い色にもなるため、
/// 地の色だけで見分けられる形にしてある。</para>
/// </remarks>
internal static class AppIcon
{
    private static readonly Color Background = Color.FromRgb(0x1F, 0x6F, 0xD1);

    /// <summary>指定した画素の正方形で描き、PNG にする。</summary>
    public static byte[] CreatePng(int pixels)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(pixels)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>ウィンドウのアイコンに使う画像。</summary>
    public static BitmapSource CreateBitmap(int pixels) => Render(pixels);

    private static RenderTargetBitmap Render(int pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels, 8);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var size = (double)pixels;
            var radius = size * 0.22;
            var brush = new SolidColorBrush(Background);
            brush.Freeze();
            context.DrawRoundedRectangle(brush, null, new Rect(0, 0, size, size), radius, radius);

            var text = new FormattedText(
                "G",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.72,
                Brushes.White,
                1.0);

            context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
