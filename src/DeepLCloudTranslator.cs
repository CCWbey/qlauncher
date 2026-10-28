using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QLaunch;

/// <summary>
/// DeepL 翻译 API（v2）。密钥以 :fx 结尾是 Free 版，走 api-free.deepl.com；
/// 其余走 api.deepl.com。也支持在凭据里填自定义端点（走代理时用）。
/// </summary>
internal sealed class DeepLCloudTranslator : ICloudTranslationProvider
{
    private const string FreeEndpoint = "https://api-free.deepl.com/v2/translate";
    private const string ProEndpoint = "https://api.deepl.com/v2/translate";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly string _key;
    private readonly string _endpoint;
    private readonly bool _free;

    private DeepLCloudTranslator(string key, string endpoint, bool free)
    {
        _key = key;
        _endpoint = endpoint;
        _free = free;
    }

    public bool Enabled => _key.Length > 0;

    public string Description => Enabled ? (_free ? "deepl/free" : "deepl") : "disabled";

    public int MaxInput => 5000;

    public static DeepLCloudTranslator FromCredentials(CloudCredentials credentials)
    {
        string key = credentials.DeepLKey.Trim();
        bool free = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase);
        string endpoint = credentials.DeepLEndpoint.Trim();
        if (endpoint.Length == 0)
        {
            endpoint = free ? FreeEndpoint : ProEndpoint;
        }
        return new DeepLCloudTranslator(key, endpoint.TrimEnd('/'), free);
    }

    public async Task<TranslationOutcome> TranslateAsync(string text, CancellationToken token)
    {
        try
        {
            string url = $"{_endpoint}?source_lang=EN&target_lang=ZH";
            using HttpRequestMessage request = new(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {_key}");
            string payload = JsonSerializer.Serialize(new { text = new[] { text } });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[deepl] HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
                return new TranslationOutcome(Explain((int)response.StatusCode), TranslationStatus.Failed, null);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("translations", out JsonElement translations)
                || translations.ValueKind != JsonValueKind.Array
                || translations.GetArrayLength() == 0
                || !translations[0].TryGetProperty("text", out JsonElement textElement))
            {
                return new TranslationOutcome("云端返回格式异常", TranslationStatus.Failed, null);
            }
            string? translated = textElement.GetString();
            return string.IsNullOrWhiteSpace(translated)
                ? new TranslationOutcome("云端返回空译文", TranslationStatus.Failed, null)
                : new TranslationOutcome(translated, TranslationStatus.Translated, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new TranslationOutcome("云端翻译超时", TranslationStatus.Failed, null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException
            or UriFormatException or NotSupportedException)
        {
            Debug.WriteLine($"[deepl] {e.GetType().Name}: {e.Message}");
            return new TranslationOutcome("云端翻译不可用", TranslationStatus.Failed, null);
        }
    }

    private static string Explain(int status) => status switch
    {
        403 => "云端翻译失败（DeepL 密钥无效）",
        456 => "云端翻译失败（DeepL 额度已用尽）",
        429 => "云端翻译失败（DeepL 请求过于频繁）",
        _ => $"云端翻译失败（HTTP {status}）"
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
