using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Glosslay.Localization;

namespace Glosslay;

/// <summary>
/// 画面の言語に合う字体（FR-SYS-03）。
/// </summary>
/// <remarks>
/// <para><b><see cref="FrameworkElement.Language"/> だけでは字体は変わらない。</b>WPF の既定の字体は
/// Windows の表示言語のもの（日本語の Windows なら Yu Gothic UI）で、画面の言語を変えてもそのまま使われる。
/// 日本語の字体では「\」が「¥」に見え、簡体字は日本語の字形と混ざる。</para>
/// <para>どれも Windows 11 に最初から入っている字体。無い字は後ろに並べた字体で補う
/// （英語の画面でも、対象の一覧には日本語の題名が出る）。</para>
/// </remarks>
internal static class UiLanguage
{
    /// <summary>いまの画面の言語。</summary>
    public static XmlLanguage Language => XmlLanguage.GetLanguage(UiText.Language);

    /// <summary>いまの画面の言語に合う字体。</summary>
    public static FontFamily Font => FontFor(UiText.Language);

    /// <summary>言語に合う字体。</summary>
    public static FontFamily FontFor(string tag) => tag switch
    {
        "en" => new FontFamily("Segoe UI, Yu Gothic UI"),
        "zh-Hans" => new FontFamily("Microsoft YaHei UI, Segoe UI"),
        "ko" => new FontFamily("Malgun Gothic, Segoe UI"),
        _ => new FontFamily("Yu Gothic UI, Segoe UI"),
    };

    /// <summary>窓の言語と字体を、いまの画面の言語に合わせる。訳文を重ねる窓も同じ（訳文はメイン言語）。</summary>
    public static void ApplyTo(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Language = Language;
        window.FontFamily = Font;
    }

    /// <summary>
    /// 言語を指定してある要素に、その言語の字体を付ける（4 言語を並べて出す初回の画面）。
    /// </summary>
    public static void ApplyFontsByElementLanguage(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child.ReadLocalValue(FrameworkElement.LanguageProperty) is XmlLanguage language)
            {
                child.SetValue(TextElement.FontFamilyProperty, FontFor(language.IetfLanguageTag));
            }

            ApplyFontsByElementLanguage(child);
        }
    }
}

/// <summary>
/// XAML で画面の文言を引く（FR-SYS-03）。<c>Text="{l:T '日本語の文'}"</c> と書く。
/// </summary>
/// <remarks>
/// <para><b>言語を変えると、その場で文言が変わる。</b>文言そのものではなく
/// <see cref="UiTextSource.Version"/> への結び付けを返し、変換の段で <see cref="UiText.T(string)"/> を引く。
/// 言語が変わると版が上がり、結び付けが引き直される。</para>
/// <para>鍵（日本語の文）に読点や括弧が入っても壊れないよう、結び付けの経路には入れず変換の引数で渡す。</para>
/// <para>XAML の書き方の注意: 文はシングルクォートで囲む。バックスラッシュは <c>\\</c> と書く。</para>
/// </remarks>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    /// <summary>日本語の文（鍵）。</summary>
    [ConstructorArgument("text")]
    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(UiTextSource.Version))
        {
            Source = UiTextSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = UiTextConverter.Instance,
            ConverterParameter = Text,
        };

        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>画面の言語が変わったことを XAML の結び付けに伝える。</summary>
public sealed class UiTextSource : INotifyPropertyChanged
{
    private UiTextSource() => UiText.LanguageChanged += (_, _) =>
    {
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    };

    public static UiTextSource Instance { get; } = new();

    /// <summary>言語が変わるたびに増える。</summary>
    public int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>鍵（日本語の文）を、いまの言語の文言にする。</summary>
internal sealed class UiTextConverter : IValueConverter
{
    public static UiTextConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        parameter is string text ? UiText.T(text) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
