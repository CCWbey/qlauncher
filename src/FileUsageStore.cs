using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QLaunch;

/// <summary>
/// 文件/文件夹的使用习惯。items 记录精确路径，folders 记录打开动作经过的容器目录链；
/// 查询只读内存表，写盘只发生在成功打开时。
/// </summary>
internal sealed class FileUsageStore
{
    public const int MaxItems = 400;
    public const int MaxFolders = 200;
    public const int MaxCount = 32;

    private const int FolderChainDepth = 3;
    private const double HalfLifeDays = 30;
    private const double PruneDays = 180;
    private const double PruneFloor = 0.25;
    private const long PruneSeconds = (long)(PruneDays * 86400);
    private const long RecentSeconds = 7 * 86400;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _file;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _folders = new(StringComparer.OrdinalIgnoreCase);

    private FileUsageStore(string file) => _file = file;

    /// <summary>一次习惯查询的精确项分与文件夹亲和分。</summary>
    public readonly record struct UsageBoost(double Item, double Folder);

    private sealed class Entry
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("lastUsed")] public long LastUsed { get; set; }
    }

    private sealed class Document
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("items")] public List<Entry> Items { get; set; } = [];
        [JsonPropertyName("folders")] public List<Entry> Folders { get; set; } = [];
    }

    /// <summary>
    /// 读取 %APPDATA%\QLaunch\usage.json。文件缺失、损坏或版本不认识时按空记录启动，
    /// 顺带合并大小写重复、清理过期低频项并按上限裁剪。
    /// </summary>
    /// <param name="dataDir">设置所在的数据目录。</param>
    /// <returns>可用的习惯库。</returns>
    public static FileUsageStore Load(string dataDir)
    {
        FileUsageStore store = new(System.IO.Path.Combine(dataDir, "usage.json"));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            if (!File.Exists(store._file))
            {
                return store;
            }
            Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllText(store._file), JsonOptions);
            if (document is null || document.Version != 1)
            {
                return store;
            }
            Merge(store._items, document.Items, now);
            Merge(store._folders, document.Folders, now);
            Prune(store._items, now);
            Prune(store._folders, now);
            Trim(store._items, MaxItems, now);
            Trim(store._folders, MaxFolders, now);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Debug.WriteLine($"[usage] 读取 {store._file} 失败，按空记录启动: {e.Message}");
            store._items.Clear();
            store._folders.Clear();
        }
        return store;
    }

    /// <summary>
    /// 记录一次真正的打开：精确项加一，再从容器目录向上记最多三层文件夹；
    /// 盘符根和 UNC 共享根不记录。
    /// </summary>
    /// <param name="path">打开的文件或文件夹路径。</param>
    /// <param name="isFolder">路径是否是文件夹。</param>
    /// <returns>无返回值。</returns>
    public void Record(string path, bool isFolder)
    {
        string target = Normalize(path);
        if (target.Length == 0 || IsRoot(target))
        {
            return;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            Bump(_items, target, now);
            string? folder = isFolder ? target : System.IO.Path.GetDirectoryName(target);
            for (int depth = 0; depth < FolderChainDepth && !string.IsNullOrEmpty(folder); depth++)
            {
                string dir = Normalize(folder);
                if (dir.Length == 0 || IsRoot(dir))
                {
                    break;
                }
                Bump(_folders, dir, now);
                folder = System.IO.Path.GetDirectoryName(dir);
            }
            Trim(_items, MaxItems, now);
            Trim(_folders, MaxFolders, now);
            Save(now);
        }
    }

    /// <summary>
    /// 计算一个候选路径的习惯分：精确项只认完全相同的路径；文件夹亲和从候选的容器目录
    /// 向上找三层，按距离取最强的一层，不叠加。
    /// </summary>
    /// <param name="path">候选路径。</param>
    /// <param name="isFolder">候选是否是文件夹。</param>
    /// <param name="nowUnix">可选的当前时间，便于测试衰减。</param>
    /// <returns>精确项分与文件夹亲和分。</returns>
    public UsageBoost Boost(string path, bool isFolder, long? nowUnix = null)
    {
        string target = Normalize(path);
        if (target.Length == 0)
        {
            return default;
        }
        long now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
            double item = _items.TryGetValue(target, out Entry? entry) ? ItemScore(entry, now) : 0;
            return new UsageBoost(item, FolderAffinity(target, isFolder, now));
        }
    }

    private double FolderAffinity(string target, bool isFolder, long now)
    {
        // 文件从父目录开始算距离 1；文件夹以自身为容器，打开该文件夹时距离也是 1
        string? folder = isFolder ? target : System.IO.Path.GetDirectoryName(target);
        double best = 0;
        for (int distance = 1; distance <= FolderChainDepth && !string.IsNullOrEmpty(folder); distance++)
        {
            string dir = Normalize(folder);
            if (dir.Length == 0 || IsRoot(dir))
            {
                break;
            }
            if (_folders.TryGetValue(dir, out Entry? entry))
            {
                best = Math.Max(best, FolderRaw(entry, now) / distance);
            }
            folder = System.IO.Path.GetDirectoryName(dir);
        }
        return best;
    }

    private static double ItemScore(Entry entry, long now)
    {
        double score = Math.Min(Effective(entry.Count, entry.LastUsed, now), 6) * 15;
        if (now - entry.LastUsed <= RecentSeconds)
        {
            score += 20;
        }
        return score;
    }

    private static double FolderRaw(Entry entry, long now)
    {
        double score = Math.Min(Effective(entry.Count, entry.LastUsed, now), 6) * 8;
        if (now - entry.LastUsed <= RecentSeconds)
        {
            score += 10;
        }
        return score;
    }

    private static double Effective(int count, long lastUsed, long now)
    {
        double days = Math.Max(0, now - lastUsed) / 86400.0;
        return Math.Min(count, MaxCount) * Math.Pow(0.5, days / HalfLifeDays);
    }

    private static void Bump(Dictionary<string, Entry> table, string path, long now)
    {
        if (table.TryGetValue(path, out Entry? entry))
        {
            entry.Count = Math.Min(entry.Count + 1, MaxCount);
            entry.LastUsed = now;
            entry.Path = path;
            return;
        }
        table[path] = new Entry { Path = path, Count = 1, LastUsed = now };
    }

    private static void Merge(Dictionary<string, Entry> table, List<Entry>? source, long now)
    {
        if (source is null)
        {
            return;
        }
        foreach (Entry? raw in source)
        {
            if (raw is null)
            {
                continue;
            }
            string path = Normalize(raw.Path);
            if (path.Length == 0 || IsRoot(path) || raw.Count <= 0)
            {
                continue;
            }
            long lastUsed = Math.Clamp(raw.LastUsed, 0, now);
            if (table.TryGetValue(path, out Entry? entry))
            {
                entry.Count = Math.Min(entry.Count + Math.Min(raw.Count, MaxCount), MaxCount);
                entry.LastUsed = Math.Max(entry.LastUsed, lastUsed);
                continue;
            }
            table[path] = new Entry
            {
                Path = path,
                Count = Math.Min(raw.Count, MaxCount),
                LastUsed = lastUsed
            };
        }
    }

    private static void Prune(Dictionary<string, Entry> table, long now)
    {
        List<string> dead = [];
        foreach ((string key, Entry entry) in table)
        {
            if (now - entry.LastUsed > PruneSeconds && Effective(entry.Count, entry.LastUsed, now) < PruneFloor)
            {
                dead.Add(key);
            }
        }
        foreach (string key in dead)
        {
            table.Remove(key);
        }
    }

    private static void Trim(Dictionary<string, Entry> table, int limit, long now)
    {
        if (table.Count <= limit)
        {
            return;
        }
        // 从最低有效分开始淘汰；同分按路径排序，保证结果可复现
        List<Entry> doomed = [.. table.Values
            .OrderBy(entry => Effective(entry.Count, entry.LastUsed, now))
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .Take(table.Count - limit)];
        foreach (Entry entry in doomed)
        {
            table.Remove(entry.Path);
        }
    }

    private void Save(long now)
    {
        try
        {
            Document document = new()
            {
                Version = 1,
                Items = Ordered(_items, now),
                Folders = Ordered(_folders, now)
            };
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Debug.WriteLine($"[usage] 保存 {_file} 失败: {e.Message}");
        }
    }

    private static List<Entry> Ordered(Dictionary<string, Entry> table, long now) =>
        [.. table.Values
            .OrderByDescending(entry => Effective(entry.Count, entry.LastUsed, now))
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)];

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }
        string trimmed = path.Trim().TrimEnd('\\');
        // 盘符根去掉反斜杠会变成驱动器相对路径，保留原样交给 IsRoot 丢弃
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return trimmed + "\\";
        }
        return trimmed;
    }

    private static bool IsRoot(string path)
    {
        string trimmed = path.TrimEnd('\\');
        if (trimmed.Length == 0)
        {
            return true;
        }
        string? root = System.IO.Path.GetPathRoot(trimmed);
        return !string.IsNullOrEmpty(root)
            && string.Equals(root.TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase);
    }
}
