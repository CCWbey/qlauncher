using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QLaunch;

/// <summary>
/// 百度翻译开放平台通用文本翻译。标准版单次 1000 字符、QPS 1，
/// 所以长文按句子拆成 900 字符以内的段，段间串行并留出节流间隔。
/// 凭据来自程序内配置的百度 APP ID 与密钥。
/// </summary>
internal sealed class BaiduCloudTranslator : ICloudTranslationProvider
{
    private const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";
    private const int ChunkSize = 900;
    private const int ChunkDelayMs = 1100;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly string _appId;
    private readonly string _key;

    private BaiduCloudTranslator(string appId, string key)
    {
        _appId = appId;
        _key = key;
    }

    public bool Enabled => _appId.Length > 0 && _key.Length > 0;

    public string Description => Enabled ? "baidu" : "disabled";

    public int MaxInput => 5000;

    public static BaiduCloudTranslator FromCredentials(CloudCredentials credentials) =>
        new(credentials.BaiduAppId.Trim(), credentials.BaiduKey.Trim());

    public async Task<TranslationOutcome> TranslateAsync(string text, CancellationToken token)
    {
        List<string> chunks = SplitChunks(text, ChunkSize);
        StringBuilder translated = new();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(ChunkDelayMs, token).ConfigureAwait(false);
            }
            TranslationOutcome part = await TranslateChunkAsync(chunks[i], token).ConfigureAwait(false);
            if (!part.HasText)
            {
                return part;
            }
            if (translated.Length > 0)
            {
                translated.Append('\n');
            }
            translated.Append(part.Text);
        }
        return new TranslationOutcome(translated.ToString(), TranslationStatus.Translated, null);
    }

    private async Task<TranslationOutcome> TranslateChunkAsync(string text, CancellationToken token)
    {
        string salt = Random.Shared.NextInt64(1000000000L, 9999999999L).ToString();
        string sign = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(_appId + text + salt + _key))).ToLowerInvariant();
        Dictionary<string, string> form = new()
        {
            ["q"] = text,
            ["from"] = "en",
            ["to"] = "zh",
            ["appid"] = _appId,
            ["salt"] = salt,
            ["sign"] = sign
        };

        try
        {
            using HttpResponseMessage response = await Http
                .PostAsync(Endpoint, new FormUrlEncodedContent(form), token)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[baidu] HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
                return new TranslationOutcome($"云端翻译失败（HTTP {(int)response.StatusCode}）", TranslationStatus.Failed, null);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error_code", out JsonElement errorCode))
            {
                string code = errorCode.ValueKind == JsonValueKind.String ? errorCode.GetString() ?? "" : errorCode.ToString();
                string message = root.TryGetProperty("error_msg", out JsonElement errorMsg) ? errorMsg.ToString() : "";
                Debug.WriteLine($"[baidu] {code}: {message}");
                return new TranslationOutcome($"云端翻译失败（百度 {code}）", TranslationStatus.Failed, code);
            }
            if (!root.TryGetProperty("trans_result", out JsonElement results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                return new TranslationOutcome("云端返回格式异常", TranslationStatus.Failed, null);
            }

            StringBuilder chunk = new();
            foreach (JsonElement item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("dst", out JsonElement dst))
                {
                    continue;
                }
                string? line = dst.GetString();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (chunk.Length > 0)
                {
                    chunk.Append('\n');
                }
                chunk.Append(line);
            }
            return chunk.Length == 0
                ? new TranslationOutcome("云端返回空译文", TranslationStatus.Failed, null)
                : new TranslationOutcome(chunk.ToString(), TranslationStatus.Translated, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new TranslationOutcome("云端翻译超时", TranslationStatus.Failed, null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException
            or UriFormatException or NotSupportedException)
        {
            Debug.WriteLine($"[baidu] {e.GetType().Name}: {e.Message}");
            return new TranslationOutcome("云端翻译不可用", TranslationStatus.Failed, null);
        }
    }

    /// <summary>优先在换行与句末标点处断开；断点太靠前则硬切，避免段太小浪费额度。</summary>
    private static List<string> SplitChunks(string text, int max)
    {
        List<string> chunks = new();
        int start = 0;
        while (start < text.Length)
        {
            int length = Math.Min(max, text.Length - start);
            if (start + length < text.Length)
            {
                int cut = LastBreak(text, start, length);
                if (cut > max / 2)
                {
                    length = cut;
                }
            }
            chunks.Add(text.Substring(start, length));
            start += length;
        }
        return chunks;
    }

    private static int LastBreak(string text, int start, int length)
    {
        for (int i = start + length - 1; i > start; i--)
        {
            char c = text[i];
            if (c is '\n' or '。' or '！' or '？' or '；' or '.' or '!' or '?' or ';')
            {
                return i - start + 1;
            }
        }
        return 0;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
