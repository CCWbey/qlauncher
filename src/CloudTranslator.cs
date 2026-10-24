namespace QLaunch;

/// <summary>
/// 云端英译中门面：按环境变量选一个服务商，统一拦截空文本、中文原文与超长原文。
/// 未配置任何凭据时整条链路禁用，界面只走本地引擎；请求失败不抛给界面，调用方保留本地结果。
/// </summary>
internal sealed class CloudTranslator
{
    private readonly ICloudTranslationProvider _provider;

    private CloudTranslator(ICloudTranslationProvider provider)
    {
        _provider = provider;
    }

    public bool Enabled => _provider.Enabled;

    public string Description => _provider.Description;

    /// <summary>
    /// QLAUNCH_CLOUD_PROVIDER 可强制 baidu 或 microsoft；留空时优先用已配好凭据的百度，其次微软。
    /// Key 不落盘，改了环境变量后需要重启程序。
    /// </summary>
    public static CloudTranslator FromEnvironment()
    {
        MicrosoftCloudTranslator microsoft = MicrosoftCloudTranslator.FromEnvironment();
        BaiduCloudTranslator baidu = BaiduCloudTranslator.FromEnvironment();
        ICloudTranslationProvider provider = (Environment.GetEnvironmentVariable("QLAUNCH_CLOUD_PROVIDER") ?? "")
            .Trim()
            .ToLowerInvariant() switch
        {
            "baidu" => baidu,
            "microsoft" => microsoft,
            _ => baidu.Enabled ? baidu : microsoft
        };
        return new CloudTranslator(provider);
    }

    /// <summary>云端是否值得发起请求：未配置、中文原文或超长时直接跳过。</summary>
    public bool CanTranslate(string text)
    {
        string trimmed = text.Trim();
        return Enabled
            && trimmed.Length > 0
            && trimmed.Length <= _provider.MaxInput
            && !Translator.IsMostlyChinese(trimmed);
    }

    public async Task<TranslationOutcome> TranslateAsync(string text, CancellationToken token)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return new TranslationOutcome(null, TranslationStatus.Skipped, null);
        }
        if (Translator.IsMostlyChinese(trimmed))
        {
            return new TranslationOutcome("（原文已是中文，未翻译）", TranslationStatus.Skipped, null);
        }
        if (!Enabled)
        {
            return new TranslationOutcome(null, TranslationStatus.NotReady, null);
        }
        if (trimmed.Length > _provider.MaxInput)
        {
            return new TranslationOutcome(null, TranslationStatus.Skipped, $"云端单次上限 {_provider.MaxInput} 字符");
        }
        return await _provider.TranslateAsync(trimmed, token).ConfigureAwait(false);
    }
}
