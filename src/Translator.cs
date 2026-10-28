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

    private readonly object _loadLock = new();
    // BlockingService 不是线程安全的：并发翻译和退出时的 Dispose 都会在原生层踩到同一份
    // 状态，表现为 coreclr 里的 0xc0000005。所有原生调用必须串行。
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private BlockingService? _service;
    private Task? _load;
    private string _status = "idle";
    private string _error = "";
    private bool _disposed;

    public string Status
    {
        get
        {
            lock (_loadLock)
            {
                return _status;
            }
        }
    }

    public string Error
    {
        get
        {
            lock (_loadLock)
            {
                return _error;
            }
        }
    }

    /// <summary>后台预热本地引擎，只应在确定不会使用云端时调用。</summary>
    /// <param name="modelDir">含 config.txt 与模型文件的目录。</param>
    /// <returns>无返回值。</returns>
    public void StartWarmup(string modelDir) => _ = EnsureLoadedAsync(modelDir, CancellationToken.None);

    /// <summary>
    /// 本地英译中。模型未加载时先加载；输入以中文为主时跳过；过长时截断并在译文末尾标注。
    /// </summary>
    /// <param name="text">选中或键入的原文。</param>
    /// <param name="modelDir">模型目录。</param>
    /// <param name="token">取消等待的令牌；不会中断已经在跑的原生翻译。</param>
    /// <returns>翻译结果；引擎未就绪或加载失败时给出对应说明。</returns>
    public async Task<TranslationOutcome> TranslateAsync(string text, string modelDir, CancellationToken token)
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
        TranslationOutcome? loadFailure = await EnsureLoadedAsync(modelDir, token).ConfigureAwait(false);
        if (loadFailure is { } failure)
        {
            return failure;
        }

        string input = trimmed.Length > MaxInput ? trimmed[..MaxInput] : trimmed;
        await _engineGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            BlockingService? service;
            lock (_loadLock)
            {
                service = _service;
            }
            if (service is null)
            {
                return new TranslationOutcome(null, TranslationStatus.NotReady, null);
            }
            string translated = service.Translate(input);
            string result = trimmed.Length > MaxInput
                ? $"{translated}\n\n（原文过长，仅翻译前 {MaxInput} 字符）"
                : translated;
            return new TranslationOutcome(result, TranslationStatus.Translated, null);
        }
        catch (Exception e)
        {
            return new TranslationOutcome($"翻译失败：{e.Message}", TranslationStatus.Failed, e.Message);
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>单飞加载：多个翻译请求同时到达时只初始化一次模型。</summary>
    /// <returns>null 表示模型已就绪；否则返回可直接展示的失败结果。</returns>
    private async Task<TranslationOutcome?> EnsureLoadedAsync(string modelDir, CancellationToken token)
    {
        Task load;
        lock (_loadLock)
        {
            if (_disposed)
            {
                return new TranslationOutcome("翻译引擎不可用：程序正在退出", TranslationStatus.Failed, "disposed");
            }
            if (_service is not null)
            {
                return null;
            }
            if (_status == "error")
            {
                return new TranslationOutcome($"翻译引擎不可用：{_error}", TranslationStatus.Failed, _error);
            }
            _load ??= Task.Run(() => Load(modelDir));
            load = _load;
        }
        await load.WaitAsync(token).ConfigureAwait(false);
        lock (_loadLock)
        {
            if (_service is not null)
            {
                return null;
            }
            return _status == "error"
                ? new TranslationOutcome($"翻译引擎不可用：{_error}", TranslationStatus.Failed, _error)
                : new TranslationOutcome(null, TranslationStatus.NotReady, null);
        }
    }

    private void Load(string modelDir)
    {
        lock (_loadLock)
        {
            if (_disposed)
            {
                return;
            }
            _status = "loading";
        }
        BlockingService? service = null;
        string error = "";
        try
        {
            string config = Path.Combine(modelDir, "config.txt");
            if (!File.Exists(config))
            {
                throw new FileNotFoundException("缺少翻译模型配置", config);
            }
            service = new BlockingService(config);
        }
        catch (Exception e)
        {
            error = e.Message;
        }
        lock (_loadLock)
        {
            if (_disposed)
            {
                service?.Dispose();
                return;
            }
            _service = service;
            _status = service is null ? "error" : "ready";
            _error = error;
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
        BlockingService? service;
        lock (_loadLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            service = _service;
            _service = null;
            _status = "idle";
        }
        if (service is null)
        {
            return;
        }
        // 等正在进行的原生翻译结束再释放，避免 translator_free 与 translator_translate 交叉。
        _engineGate.Wait();
        try
        {
            service.Dispose();
        }
        finally
        {
            _engineGate.Release();
        }
    }
}
