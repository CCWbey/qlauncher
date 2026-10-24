namespace QLaunch;

/// <summary>云端翻译服务商：一个实现对应一家厂商的协议与鉴权方式，门面只按配置选一个。</summary>
internal interface ICloudTranslationProvider
{
    /// <summary>凭据是否齐全，可发起请求。</summary>
    bool Enabled { get; }

    /// <summary>日志里的服务商标识，如 baidu、microsoft/eastasia。</summary>
    string Description { get; }

    /// <summary>本服务商接受的原文上限；更长的原文由门面直接跳过。</summary>
    int MaxInput { get; }

    Task<TranslationOutcome> TranslateAsync(string text, CancellationToken token);
}
