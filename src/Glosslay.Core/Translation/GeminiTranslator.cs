using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Glosslay.Configuration;

namespace Glosslay.Translation;

/// <summary>
/// Gemini API による翻訳（FR-TRN-04 BYOK / PLAN.md P0-5）。
/// </summary>
/// <remarks>
/// <para><b>これは既定のバックエンドではない。</b>既定はローカルNMT（RULES.md 🟡-1）で、
/// 実装は v1.0。PoC では自分用のためキー登録の障壁が問題にならず、
/// 先に一連の流れを通すためにこちらを実装している。</para>
/// <para>キーは <see cref="ApiKeyStore"/> から<b>呼び出しごとに読む</b>。
/// 設定画面で登録した直後に、アプリを再起動せず使えるようにするため。</para>
/// </remarks>
public sealed class GeminiTranslator : ITranslator
{
    private readonly GeminiOptions _options;
    private readonly ApiKeyStore _keyStore;
    private readonly HttpClient _client;

    public GeminiTranslator(GeminiOptions? options = null, ApiKeyStore? keyStore = null)
    {
        _options = options ?? GeminiOptions.Load();
        _keyStore = keyStore ?? ApiKeyStore.Gemini;
        _client = new HttpClient { Timeout = _options.Timeout };
    }

    /// <summary>FR-TRN-13 のバッジに出す名前。どのモデルで動いているかまで見せる。</summary>
    public string Name => $"Gemini ({_options.Model})";

    public bool IsConfigured => _keyStore.Exists;

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return new TranslationResult(string.Empty, TranslationOutcome.Succeeded, stopwatch.Elapsed);
        }

        var apiKey = _keyStore.Load();
        if (apiKey is null)
        {
            return TranslationResult.Failure(
                TranslationOutcome.NotConfigured,
                "Gemini の APIキーが登録されていません。設定から登録してください。",
                stopwatch.Elapsed);
        }

        try
        {
            return await SendAsync(request, apiKey, stopwatch, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout による打ち切り。利用者の中断とは区別する。
            // 「通信できない」とは言い切らない。要求は届いていて、応答が遅いだけのことがある。
            return TranslationResult.Failure(
                TranslationOutcome.Timeout,
                $"Gemini が {_options.Timeout.TotalSeconds:F0} 秒以内に応答しませんでした。"
                + " 混雑していると時間がかかることがあります。"
                + $" 待てる場合は {GeminiOptions.FileName} の Timeout を伸ばしてください。",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            return TranslationResult.Failure(
                TranslationOutcome.NetworkError,
                $"Gemini に接続できませんでした: {exception.Message}",
                stopwatch.Elapsed);
        }
    }

    private async Task<TranslationResult> SendAsync(
        TranslationRequest request, string apiKey, Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var url = new Uri(
            $"{_options.Endpoint.TrimEnd('/')}/models/{_options.Model}:generateContent");

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(BuildPayload(request)),
        };

        // キーはヘッダで渡す。クエリ文字列に入れるとプロキシやサーバのアクセスログに残る。
        message.Headers.Add("x-goog-api-key", apiKey);

        using var response = await _client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return await FailureFromResponseAsync(response, stopwatch, cancellationToken)
                .ConfigureAwait(false);
        }

        var payload = await response.Content
            .ReadFromJsonAsync<GenerateContentResponse>(cancellationToken)
            .ConfigureAwait(false);

        return Interpret(payload, stopwatch.Elapsed);
    }

    private Dictionary<string, object> BuildPayload(TranslationRequest request)
    {
        var instruction = _options.SystemInstruction
            .Replace("{target}", LanguageName(request.TargetLanguage), StringComparison.Ordinal);

        // thinkingLevel は 2.5 系が解釈しないため、設定されているときだけ載せる。
        var generationConfig = _options.ThinkingLevel is { Length: > 0 } level
            ? new Dictionary<string, object>
            {
                ["temperature"] = _options.Temperature,
                ["maxOutputTokens"] = _options.MaxOutputTokens,
                ["thinkingLevel"] = level,
            }
            : new Dictionary<string, object>
            {
                ["temperature"] = _options.Temperature,
                ["maxOutputTokens"] = _options.MaxOutputTokens,
            };

        return new Dictionary<string, object>
        {
            ["systemInstruction"] = new { parts = new[] { new { text = instruction } } },
            ["contents"] = new[]
            {
                new { role = "user", parts = new[] { new { text = request.Text } } },
            },
            ["generationConfig"] = generationConfig,
        };
    }

    private static async Task<TranslationResult> FailureFromResponseAsync(
        HttpResponseMessage response, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        // 本文にモデル名の誤りやキーの失効といった原因が入っている。握り潰さず利用者に見せる（RULES.md 🔵）。
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = body.Length > 300 ? body[..300] : body;

        // 429 と 503 を分けるのは、どちらも「こちらに直すところが無い」状態でありながら、
        // 原因も待つべき時間も違うため。まとめて「サービスエラー」にすると原因が追えない。
        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => TranslationResult.Failure(
                TranslationOutcome.RateLimited,
                "Gemini の利用上限に達しました。しばらく待つと戻ります。",
                stopwatch.Elapsed),

            // 503 は Google 側の混雑。2026-09-24 に無料枠で頻発した（PLAN.md 課題7）。
            // 本文に "This model is currently experiencing high demand." が入る。
            HttpStatusCode.ServiceUnavailable => TranslationResult.Failure(
                TranslationOutcome.Busy,
                "Gemini が混雑していて応答できません。"
                + " 利用者側の問題ではなく、時間をおくと戻ります。"
                + $" 詳細: {detail}",
                stopwatch.Elapsed),

            _ => TranslationResult.Failure(
                TranslationOutcome.ServiceError,
                $"Gemini がエラーを返しました（HTTP {(int)response.StatusCode}）: {detail}",
                stopwatch.Elapsed),
        };
    }

    private static TranslationResult Interpret(GenerateContentResponse? payload, TimeSpan elapsed)
    {
        if (payload?.PromptFeedback?.BlockReason is { Length: > 0 } blockReason)
        {
            return TranslationResult.Failure(
                TranslationOutcome.Blocked,
                $"Gemini が応答を拒否しました（{blockReason}）。",
                elapsed);
        }

        var candidate = payload?.Candidates is { Count: > 0 } candidates ? candidates[0] : null;

        // 思考モデルは思考の断片も parts に載せてくる。訳文ではないので除く。
        var text = string.Concat(
            candidate?.Content?.Parts?
                .Where(part => part.Thought != true && part.Text is not null)
                .Select(part => part.Text) ?? []);

        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Failure(
                TranslationOutcome.Blocked,
                $"Gemini が訳文を返しませんでした（finishReason: {candidate?.FinishReason ?? "不明"}）。",
                elapsed);
        }

        var result = new TranslationResult(text.Trim(), TranslationOutcome.Succeeded, elapsed)
        {
            TotalTokens = payload?.UsageMetadata?.TotalTokenCount,
        };

        // 途中で切れた訳文をそのまま見せると、数値が欠けたことに気づけない（RULES.md 🟡-7）。
        return candidate?.FinishReason == "MAX_TOKENS"
            ? result with { Message = "訳文が上限トークン数で打ち切られています。" }
            : result;
    }

    /// <summary>プロンプトに載せる言語名。<c>ja</c> → <c>日本語</c>。</summary>
    private static string LanguageName(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    public void Dispose() => _client.Dispose();

    // --- 応答の受け皿。必要な項目だけ拾う ---

    private sealed record GenerateContentResponse(
        [property: JsonPropertyName("candidates")] IReadOnlyList<Candidate>? Candidates,
        [property: JsonPropertyName("promptFeedback")] PromptFeedback? PromptFeedback,
        [property: JsonPropertyName("usageMetadata")] UsageMetadata? UsageMetadata);

    private sealed record Candidate(
        [property: JsonPropertyName("content")] Content? Content,
        [property: JsonPropertyName("finishReason")] string? FinishReason);

    private sealed record Content(
        [property: JsonPropertyName("parts")] IReadOnlyList<Part>? Parts);

    private sealed record Part(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("thought")] bool? Thought);

    private sealed record PromptFeedback(
        [property: JsonPropertyName("blockReason")] string? BlockReason);

    private sealed record UsageMetadata(
        [property: JsonPropertyName("totalTokenCount")] int? TotalTokenCount);
}
