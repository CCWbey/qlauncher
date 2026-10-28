namespace QLaunch;

/// <summary>
/// 云端英译中门面：按程序内保存的翻译方式选一个服务商，统一拦截空文本、中文原文与超长原文。
/// 选本地或凭据缺失时整条链路禁用，界面只走本地引擎；请求失败不抛给界面，调用方保留本地结果。
/// </summary>
internal sealed class CloudTranslator
{
    private readonly ICloudTranslationProvider? _provider;

    private CloudTranslator(ICloudTranslationProvider? provider)
    {
        _provider = provider;
    }

    public bool Enabled => _provider is { Enabled: true };

    public string Description => _provider?.Description ?? "disabled";

    /// <summary>
    /// 按凭据里保存的翻译方式构造：baidu / microsoft / deepl 对应云端服务商，
    /// 其他值（本地 Bergamot 或旧配置）一律禁用云端。
    /// </summary>
    /// <param name="credentials">已解密或刚编辑过的凭据。</param>
    /// <returns>门面实例；未选云端服务商或凭据不全时 Enabled 为 false。</returns>
    public static CloudTranslator FromCredentials(CloudCredentials credentials)
    {
        ICloudTranslationProvider? provider = credentials.Provider.Trim().ToLowerInvariant() switch
        {
            "baidu" => BaiduCloudTranslator.FromCredentials(credentials),
            "microsoft" => MicrosoftCloudTranslator.FromCredentials(credentials),
            "deepl" => DeepLCloudTranslator.FromCredentials(credentials),
            _ => null
        };
        return new CloudTranslator(provider);
    }

    /// <summary>云端是否值得发起请求：未配置、中文原文或超长时直接跳过。</summary>
    public bool CanTranslate(string text)
    {
        string trimmed = text.Trim();
        return _provider is not null
            && Enabled
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
        if (_provider is null || !Enabled)
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
