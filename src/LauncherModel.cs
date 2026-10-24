namespace QLaunch;

internal enum MoveDirection
{
    Up,
    Down,
    Left,
    Right
}

/// <summary>右键菜单的动作类型。</summary>
internal enum MenuCommand
{
    Open,
    Reveal,
    Copy,
    Terminal,
    VsCode,
    Remove,
    ToggleTranslation
}

/// <summary>
/// 右键菜单里的一条操作。Copy 用 CopyText 写剪贴板，其余命令由宿主执行。
/// </summary>
/// <param name="Label">菜单文字。</param>
/// <param name="Command">动作类型。</param>
/// <param name="CopyText">复制命令要写入剪贴板的内容。</param>
internal sealed record MenuAction(string Label, MenuCommand Command = MenuCommand.Open, string? CopyText = null);

/// <summary>
/// 搜索栏的界面状态：首页网格、查询结果、划词译文三种模式共用一个行列表，选中项与右键菜单项
/// 都在这里决定。绘制层只读取，不产生状态，便于无头验证。
/// </summary>
internal sealed class LauncherModel
{
    public const string DefaultPlaceholder = "应用名 / 文件关键词 / 英文句子";
    public const string SelectionPlaceholder = "已捕获选中文本，继续输入可改搜应用/文件";
    public const string EnginePendingText = "（正在翻译…）";
    public const string EngineNotReadyText = "（离线引擎未就绪，只取到原文）";
    public const int GridColumns = 6;

    private List<Result> _frequent = [];
    private List<Result> _results = [];
    private string? _translation;
    private string _source = "";
    private bool _showOriginal;
    private bool _transPending;
    private bool _picked;

    public IReadOnlyList<Result> Rows { get; private set; } = [];
    public string Query { get; private set; } = "";
    public string Placeholder { get; private set; } = DefaultPlaceholder;
    public string Note { get; private set; } = "";
    public int Active { get; private set; } = -1;

    /// <summary>自增的查询序号；结果回填时只接受仍然等于当前值的那一包，过期包整批丢弃。</summary>
    public int QueryId { get; private set; }

    public bool HasTranslation => _translation is not null;
    public string TranslationText => _translation ?? "";
    public string SourceText => _source;
    public bool ShowingOriginal => _showOriginal;

    public bool IsHome => !_picked && Matcher.NormalizeQuery(Query).Length == 0;

    /// <summary>检测到 VS Code 时文件夹菜单才给编辑器入口。</summary>
    public bool VsCodeAvailable { get; set; }

    public Result? ActiveRow => Active >= 0 && Active < Rows.Count ? Rows[Active] : null;

    public string EmptyText => IsHome ? "还没有启动记录，输入应用名或拼音即可" : "没有匹配结果";

    public void Reset() => SetQuery("");

    public void SetQuery(string text)
    {
        _picked = false;
        Placeholder = DefaultPlaceholder;
        Query = text;
        QueryId++;
        _translation = null;
        _source = "";
        _showOriginal = false;
        _transPending = false;
        _results = IsHome ? _frequent : [];
        Rebuild();
        Active = Rows.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// 回填一次查询的结果。
    /// </summary>
    /// <returns>false 表示这次响应已经过期（用户在此期间继续输入），界面不采用。</returns>
    public bool ApplyResults(int id, List<Result> results, string? translation, string? error, bool pending = false)
    {
        if (id != QueryId)
        {
            return false;
        }
        _results = results;
        if (IsHome)
        {
            _frequent = results;
        }
        _translation = string.IsNullOrEmpty(translation) ? null : translation;
        _source = Query.Trim();
        _showOriginal = false;
        _transPending = pending && _translation is null;
        Rebuild();
        Active = Rows.Count > 0 ? 0 : -1;
        if (!string.IsNullOrEmpty(error))
        {
            Note = error;
        }
        return true;
    }

    /// <summary>把选中并翻译好的文本作为唯一内容展示，输入框留空以便用户改搜。</summary>
    /// <param name="pending">true 表示云端结果仍在路上，先显示等待提示。</param>
    public void SetSelection(string text, string? translated, bool pending)
    {
        if (text.Length == 0)
        {
            return;
        }
        _picked = true;
        QueryId++;
        Query = "";
        Placeholder = SelectionPlaceholder;
        _translation = string.IsNullOrEmpty(translated) ? null : translated;
        _source = text;
        _showOriginal = false;
        _transPending = pending;
        _results = [];
        Rebuild();
        Active = 0;
    }

    /// <summary>
    /// 云端结果晚到时替换本地译文并结束等待状态。序号已变（用户改了输入）则丢弃。
    /// </summary>
    /// <returns>true 表示已更新界面。</returns>
    public bool FinishTranslation(int id, string translation)
    {
        if (id != QueryId || translation.Length == 0)
        {
            return false;
        }
        _translation = translation;
        _transPending = false;
        if (_source.Length == 0)
        {
            _source = Query.Trim();
        }
        Rebuild();
        return true;
    }

    /// <summary>左键点译文行时在原文与译文之间切换。</summary>
    /// <returns>true 表示已切换；没有译文时返回 false。</returns>
    public bool ToggleTranslation()
    {
        if (_translation is null)
        {
            return false;
        }
        _showOriginal = !_showOriginal;
        Rebuild();
        return true;
    }

    public void SetNote(string? message) => Note = message ?? "";

    /// <summary>
    /// 键盘移动选中项。首页是 6 列网格，上下按整行跨 6 格；非首页的左右键留给输入框移动光标。
    /// </summary>
    public void Move(MoveDirection direction)
    {
        int step = IsHome ? GridColumns : 1;
        int? next = direction switch
        {
            MoveDirection.Down => Active < 0 ? 0 : Active + step,
            MoveDirection.Up => Active - step,
            MoveDirection.Right when IsHome => Active + 1,
            MoveDirection.Left when IsHome => Active - 1,
            _ => null
        };
        if (next is null)
        {
            return;
        }
        Active = Rows.Count == 0 ? -1 : Math.Clamp(next.Value, 0, Rows.Count - 1);
    }

    public void SetHover(int index) => Active = Rows.Count == 0 ? -1 : Math.Clamp(index, 0, Rows.Count - 1);

    public IReadOnlyList<MenuAction> MenuFor(Result row) => row.Kind switch
    {
        "trans" => TransMenu(),
        "app" => AppMenu(row),
        "folder" => FolderMenu(row),
        _ => [new("打开"), new("打开所在位置", MenuCommand.Reveal), new("复制完整路径", MenuCommand.Copy, row.Path), new("复制名称", MenuCommand.Copy, row.Title)]
    };

    private List<MenuAction> TransMenu()
    {
        List<MenuAction> actions = [];
        if (_translation is not null)
        {
            actions.Add(new(_showOriginal ? "显示译文" : "显示原文", MenuCommand.ToggleTranslation));
            actions.Add(new("复制译文", MenuCommand.Copy, _translation));
        }
        actions.Add(new("复制原文", MenuCommand.Copy, _source));
        return actions;
    }

    private List<MenuAction> AppMenu(Result row)
    {
        List<MenuAction> actions = [new("打开"), new("复制文件路径", MenuCommand.Copy, row.Path)];
        if (IsHome)
        {
            actions.Add(new("从常用中移除", MenuCommand.Remove));
        }
        return actions;
    }

    private List<MenuAction> FolderMenu(Result row)
    {
        List<MenuAction> actions =
        [
            new("打开"),
            new("在终端中打开", MenuCommand.Terminal)
        ];
        if (VsCodeAvailable)
        {
            actions.Add(new("用 VS Code 打开", MenuCommand.VsCode));
        }
        actions.Add(new("打开所在位置", MenuCommand.Reveal));
        actions.Add(new("复制完整路径", MenuCommand.Copy, row.Path));
        actions.Add(new("复制名称", MenuCommand.Copy, row.Title));
        return actions;
    }

    private void Rebuild()
    {
        if (_translation is not null)
        {
            Rows =
            [
                new Result
                {
                    Kind = "trans",
                    Title = _showOriginal ? _source : _translation,
                    Subtitle = _showOriginal ? _translation : _source,
                    Glyph = _showOriginal ? "原" : "译"
                },
                .. _results
            ];
            return;
        }
        if (_transPending)
        {
            Rows = [new Result { Kind = "trans", Title = EnginePendingText, Subtitle = _source, Glyph = "译" }, .. _results];
            return;
        }
        Rows = _results;
    }
}
