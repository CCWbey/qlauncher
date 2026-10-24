using System.Text.Json;
using System.Text.Json.Serialization;

namespace QLaunch;

internal sealed class AppSettings
{
    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; } = 20;

    [JsonPropertyName("fileResults")]
    public int FileResults { get; set; } = 12;

    [JsonPropertyName("launchAtStartup")]
    public bool LaunchAtStartup { get; set; }

    [JsonPropertyName("launchCounts")]
    public Dictionary<string, long> LaunchCounts { get; set; } = [];

    [JsonIgnore]
    public string DataDir { get; set; } = "";

    public static string AppKey(string path, string arguments) =>
        path.ToUpperInvariant() + "\n" + arguments;

    public long GetLaunchCount(string path, string arguments) =>
        LaunchCounts.GetValueOrDefault(AppKey(path, arguments));

    public void RecordLaunch(string path, string arguments)
    {
        string key = AppKey(path, arguments);
        LaunchCounts[key] = LaunchCounts.GetValueOrDefault(key) + 1;
        Save();
    }

    /// <summary>
    /// 只清掉这一项的启动计数：不记"已移除"名单，下次真正启动时计数会重新累积。
    /// </summary>
    /// <param name="path">应用路径。</param>
    /// <param name="arguments">启动参数。</param>
    /// <returns>无返回值。</returns>
    public void RemoveLaunch(string path, string arguments)
    {
        if (LaunchCounts.Remove(AppKey(path, arguments)))
        {
            Save();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 从 %APPDATA%\QLaunch\settings.json 读取设置，缺失或损坏时返回内置默认值。
    /// </summary>
    /// <returns>可用的设置对象，目录路径一并带回供保存使用。</returns>
    public static AppSettings Load()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QLaunch");
        System.IO.Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "settings.json");
        AppSettings settings = new() { DataDir = dir };
        if (File.Exists(file))
        {
            try
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file));
                if (loaded is not null)
                {
                    loaded.DataDir = dir;
                    return loaded;
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                Console.WriteLine($"[settings] 读取失败，使用默认值: {e.Message}");
            }
        }
        return settings;
    }

    /// <summary>
    /// 将当前设置写回磁盘。
    /// </summary>
    /// <returns>无返回值。</returns>
    public void Save()
    {
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), JsonSerializer.Serialize(this, JsonOptions));
    }
}
