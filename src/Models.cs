namespace QLaunch;

/// <summary>
/// 搜索栏中的一条结果。Kind 决定图标与回车行为，Actions 描述除回车外的次要操作。
/// </summary>
internal sealed class Result
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Icon { get; init; } = "";
    /// <summary>少数行需要覆盖默认字形，例如译文切换为原文时显示“原”。</summary>
    public string Glyph { get; init; } = "";
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string Payload { get; init; } = "";
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public double Score { get; init; }
}

/// <summary>
/// 已索引的本地应用条目。
/// </summary>
internal sealed class AppEntry
{
    public required string Name { get; init; }
    public string Initials { get; init; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Icon { get; set; } = "";
    public bool IsUwp { get; set; }
    public bool IsPortable { get; set; }
}
