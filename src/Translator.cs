using BergamotTranslatorSharp;

namespace QLaunch;

internal enum TranslationStatus
{
    Translated,
    Skipped,
    NotReady,
    Failed
}

/// <summary>一次翻译的结果。Skipped/Failed 时 Text 也可能带可展示的说明。</summary>
internal readonly record struct TranslationOutcome(string? Text, TranslationStatus Status, string? Error)
{
    public bool HasText => Status == TranslationStatus.Translated && !string.IsNullOrEmpty(Text);
}

internal sealed class Translator : IDisposable
{
    // Bergamot 会按 token 长度自动切块，2 万字符约 1~2 秒；再长就截断并明确告知，
    // 避免整本书被误选后界面长时间没有反馈。
    private const int MaxInput = 20000;

    private BlockingService? _service;
    private Task? _warmup;
    private string _status = "idle";
    private string _error = "";

    public string Status => _status;
    public string Error => _error;

    /// <summary>
    /// 后台预热离线引擎，避免首次划词时同步加载数十 MB 模型阻塞界面。
    /// </summary>
    /// <param name="modelDir">含 config.txt 与模型文件的目录。</param>
    /// <returns>无返回值。</returns>
    public void StartWarmup(string modelDir)
    {
        if (_warmup is not null)
        {
            return;
        }
        _status = "loading";
        _warmup = Task.Run(() =>
        {
            try
            {
                string config = Path.Combine(modelDir, "config.txt");
                if (!File.Exists(config))
                {
                    throw new FileNotFoundException("缺少翻译模型配置", config);
                }
                _service = new BlockingService(config);
                _status = "ready";
            }
            catch (Exception e)
            {
                _status = "error";
                _error = e.Message;
            }
        });
    }

    /// <summary>
    /// 本地英译中。输入以中文为主时跳过；过长时截断并在译文末尾标注。
    /// </summary>
    /// <param name="text">选中或键入的原文。</param>
    /// <returns>翻译结果；引擎未就绪时 Status 为 NotReady。</returns>
    public TranslationOutcome Translate(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return new TranslationOutcome(null, TranslationStatus.Skipped, null);
        }
        if (IsMostlyChinese(trimmed))
        {
            return new TranslationOutcome("（原文已是中文，未翻译）", TranslationStatus.Skipped, null);
        }
        if (_service is null)
        {
            return _status == "error"
                ? new TranslationOutcome($"翻译引擎不可用：{_error}", TranslationStatus.Failed, _error)
                : new TranslationOutcome(null, TranslationStatus.NotReady, null);
        }
        string input = trimmed.Length > MaxInput ? trimmed[..MaxInput] : trimmed;
        try
        {
            string translated = _service.Translate(input);
            string result = trimmed.Length > MaxInput
                ? $"{translated}\n\n（原文过长，仅翻译前 {MaxInput} 字符）"
                : translated;
            return new TranslationOutcome(result, TranslationStatus.Translated, null);
        }
        catch (Exception e)
        {
            return new TranslationOutcome($"翻译失败：{e.Message}", TranslationStatus.Failed, e.Message);
        }
    }

    internal static bool IsMostlyChinese(string text)
    {
        int cjk = 0;
        int meaningful = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            meaningful++;
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿')
            {
                cjk++;
            }
        }
        return meaningful > 0 && cjk * 2 > meaningful;
    }

    public void Dispose()
    {
        _service?.Dispose();
        _service = null;
    }
}
