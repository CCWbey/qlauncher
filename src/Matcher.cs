namespace QLaunch;

internal static class Matcher
{
    public static string NormalizeQuery(string query) => query.Replace("'", "").Trim();

    /// <summary>
    /// 给单个应用打分。完全匹配 > 前缀 > 拼音首字母前缀 > 包含 > 子序列，未命中返回 0。
    /// </summary>
    /// <param name="query">用户输入，已去首尾空白。</param>
    /// <param name="app">索引中的应用条目。</param>
    /// <returns>排序用分值，0 表示不显示。</returns>
    public static double ScoreApp(string query, AppEntry app)
    {
        if (query.Length == 0)
        {
            return 0;
        }
        string name = app.Name.ToLowerInvariant();
        string q = query.ToLowerInvariant();
        string initials = app.Initials.ToLowerInvariant();

        if (name == q)
        {
            return 1000;
        }
        if (name.StartsWith(q, StringComparison.Ordinal))
        {
            return 820 - Math.Min(name.Length - q.Length, 100) * 0.5;
        }
        if (initials.Length > 0 && initials.StartsWith(q, StringComparison.Ordinal))
        {
            return 780 - Math.Min(initials.Length - q.Length, 100) * 0.5;
        }
        foreach (string token in name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(q, StringComparison.Ordinal))
            {
                return 700;
            }
        }
        if (name.Contains(q, StringComparison.Ordinal))
        {
            return 560;
        }
        if (initials.Length > 0 && initials.Contains(q, StringComparison.Ordinal))
        {
            return 520;
        }
        return IsSubsequence(q, name) ? 220 : 0;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        int i = 0;
        foreach (char c in haystack)
        {
            if (i < needle.Length && c == needle[i])
            {
                i++;
            }
        }
        return i == needle.Length;
    }
}
