using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QLaunch;

/// <summary>
/// 微软 Translator（Azure 认知服务）。凭据来自 QLAUNCH_CLOUD_KEY / QLAUNCH_CLOUD_REGION / QLAUNCH_CLOUD_ENDPOINT。
/// </summary>
internal sealed class MicrosoftCloudTranslator : ICloudTranslationProvider
{
    private const string DefaultEndpoint = "https://api.cognitive.microsofttranslator.com";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly string _key;
    private readonly string? _region;
    private readonly string _endpoint;

    private MicrosoftCloudTranslator(string key, string? region, string endpoint)
    {
        _key = key;
        _region = region;
        _endpoint = endpoint;
    }

    public bool Enabled => _key.Length > 0;

    public string Description => Enabled ? (_region is null ? "microsoft" : $"microsoft/{_region}") : "disabled";

    public int MaxInput => 5000;

    public static MicrosoftCloudTranslator FromEnvironment()
    {
        string key = (Environment.GetEnvironmentVariable("QLAUNCH_CLOUD_KEY") ?? "").Trim();
        string? region = Environment.GetEnvironmentVariable("QLAUNCH_CLOUD_REGION");
        string endpoint = (Environment.GetEnvironmentVariable("QLAUNCH_CLOUD_ENDPOINT") ?? DefaultEndpoint).Trim();
        return new MicrosoftCloudTranslator(
            key,
            string.IsNullOrWhiteSpace(region) ? null : region.Trim(),
            endpoint.TrimEnd('/'));
    }

    public async Task<TranslationOutcome> TranslateAsync(string text, CancellationToken token)
    {
        try
        {
            string url = $"{_endpoint}/translate?api-version=3.0&from=en&to=zh-Hans";
            using HttpRequestMessage request = new(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _key);
            if (_region is not null)
            {
                request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", _region);
            }
            string payload = JsonSerializer.Serialize(new[] { new { Text = text } });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[microsoft] HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
                return new TranslationOutcome($"云端翻译失败（HTTP {(int)response.StatusCode}）", TranslationStatus.Failed, null);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return new TranslationOutcome("云端返回格式异常", TranslationStatus.Failed, null);
            }
            JsonElement item = document.RootElement[0];
            if (!item.TryGetProperty("translations", out JsonElement translations)
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
            Debug.WriteLine($"[microsoft] {e.GetType().Name}: {e.Message}");
            return new TranslationOutcome("云端翻译不可用", TranslationStatus.Failed, null);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
