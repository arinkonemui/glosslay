using System.Windows;
using System.Windows.Controls;

namespace Glosslay;

/// <summary>
/// 初回起動時にメイン言語を選ぶ画面（FR-SYS-03 / FR-SYS-05）。
/// </summary>
/// <remarks>
/// <para><b>閉じても保存しない。</b>次に起動したときにもう一度聞く。うっかり閉じた人に、
/// 読めない言語の画面を出し続けないため。その回は既定（日本語）で動く。</para>
/// </remarks>
public partial class LanguageSetupWindow : Window
{
    public LanguageSetupWindow()
    {
        InitializeComponent();
        Icon = AppIcon.CreateBitmap(64);

        // 4 言語を並べて出すので、要素ごとにその言語の字体で出す（簡体字が日本語の字形と混ざらないように）。
        UiLanguage.ApplyFontsByElementLanguage(this);
    }

    /// <summary>選ばれたメイン言語（BCP 47）。閉じただけなら <c>null</c>。</summary>
    public string? Selected { get; private set; }

    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            Selected = tag;
            DialogResult = true;
        }
    }
}
