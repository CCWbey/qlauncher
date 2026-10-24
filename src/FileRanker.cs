namespace QLaunch;

/// <summary>
/// 把 Everything 返回的名称序候选池重排成"匹配度 + 路径上下文 + 使用习惯"的顺序。
/// 强匹配单独设为第一关键字，保证习惯分再高也压不过文件名精确命中。
/// </summary>
internal static class FileRanker
{
    /// <summary>每次查询取的候选数；超过这个量的宽泛查询按 Everything 名称序截断。</summary>
    public const int CandidatePool = 400;

    private const double Neutral = 450;
    private const double HabitCap = 140;

    private static readonly string[] LowValueSegments =
    [
        "\\appdata\\",
        "\\program files\\",
        "\\program files (x86)\\",
        "\\windows\\",
        "\\node_modules\\",
        "\\.git\\"
    ];

    private readonly record struct Scored(Result Row, bool Strong, double Score, int Index);

    /// <summary>
    /// 重排候选并按类型分组。分组以总排名第一名的类型在前，组内保持排名，
    /// 避免结果面板出现"文件 / 文件夹 / 文件"的重复标题。
    /// </summary>
    /// <param name="candidates">Everything 返回的候选池。</param>
    /// <param name="query">已归一化的查询文本，可含 Everything 过滤语法。</param>
    /// <param name="usage">习惯库，只读。</param>
    /// <param name="limit">最终显示条数。</param>
    /// <returns>重排并分组后的结果。</returns>
    public static List<Result> Rank(IReadOnlyList<Result> candidates, string query, FileUsageStore usage, int limit)
    {
        string q = Matcher.NormalizeQuery(query).ToLowerInvariant();
        if (q.Length == 0 || limit <= 0 || candidates.Count == 0)
        {
            return [];
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<Scored> scored = new(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            Result row = candidates[i];
            string name = FileName(row).ToLowerInvariant();
            FileUsageStore.UsageBoost habit = usage.Boost(row.Path, row.Kind == "folder", now);
            double score = MatchScore(name, q)
                + ContextScore(row)
                + Math.Min(habit.Item + habit.Folder, HabitCap);
            scored.Add(new Scored(row, IsStrong(name, q), score, i));
        }

        // LINQ 的 OrderBy 是稳定排序；Index 再兜底一次，规则完全同分时保持 Everything 原顺序
        List<Result> ranked = [.. scored
            .OrderByDescending(item => item.Strong)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => CleanPath(item.Row.Path).Length)
            .ThenBy(item => item.Index)
            .Take(limit)
            .Select(item => item.Row)];
        if (ranked.Count < 2)
        {
            return ranked;
        }
        string firstKind = ranked[0].Kind;
        return [.. ranked.Where(row => row.Kind == firstKind), .. ranked.Where(row => row.Kind != firstKind)];
    }

    private static bool IsStrong(string name, string query) =>
        name.Equals(query, StringComparison.Ordinal) || name.StartsWith(query, StringComparison.Ordinal);

    private static double MatchScore(string name, string query)
    {
        // ext:pdf 这类词是 Everything 的过滤语法，不是文件名的一部分，不参与文本打分
        List<string> terms = [.. query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(term => !term.Contains(':'))];
        if (terms.Count == 0)
        {
            return Neutral;
        }
        double score = terms.Sum(term => TermScore(name, term)) / terms.Count;
        if (terms.Count > 1 && name.Contains(string.Join(' ', terms), StringComparison.Ordinal))
        {
            score += 40;
        }
        return score;
    }

    private static double TermScore(string name, string term)
    {
        if (name.Equals(term, StringComparison.Ordinal))
        {
            return 1000;
        }
        if (name.StartsWith(term, StringComparison.Ordinal))
        {
            return 850;
        }
        foreach (string token in name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(term, StringComparison.Ordinal))
            {
                return 700;
            }
        }
        return name.Contains(term, StringComparison.Ordinal) ? 550 : Neutral;
    }

    private static double ContextScore(Result row)
    {
        string path = CleanPath(row.Path);
        if (path.Length == 0)
        {
            return 0;
        }
        double value = 0;
        string? root = Path.GetPathRoot(path);
        // 非 C 盘（含 UNC）优先；C 盘内再按目录深浅拉开
        if (!string.IsNullOrEmpty(root) && !root.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
        {
            value += 50;
        }
        value -= Math.Min(Depth(path, row.Kind == "folder") * 8, 64);
        string lower = (path + "\\").ToLowerInvariant();
        foreach (string segment in LowValueSegments)
        {
            if (lower.Contains(segment, StringComparison.Ordinal))
            {
                value -= 100;
                break;
            }
        }
        return Math.Clamp(value, -140, 140);
    }

    private static int Depth(string path, bool isFolder)
    {
        string container = isFolder ? path : Path.GetDirectoryName(path) ?? "";
        string? root = Path.GetPathRoot(container);
        if (string.IsNullOrEmpty(root) || root.Length >= container.Length)
        {
            return 0;
        }
        string rest = container[root.Length..].Trim('\\');
        return rest.Length == 0 ? 0 : rest.Count(c => c == '\\') + 1;
    }

    private static string FileName(Result row) =>
        row.Title.Length > 0 ? row.Title : Path.GetFileName(CleanPath(row.Path));

    private static string CleanPath(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;
}
