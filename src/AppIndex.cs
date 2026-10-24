using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QLaunch;

internal sealed class AppIndex
{
    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;
    private const uint SHGFI_PIDL = 0x00000008;
    private const int IconSize = 32;

    private readonly List<AppEntry> _entries = [];
    private readonly List<AppEntry> _scanned = [];
    private readonly List<AppEntry> _portable = [];
    private readonly Dictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private string _iconDir = "";
    private string _portableFile = "";

    public IReadOnlyList<AppEntry> Entries => _entries;

    public IReadOnlyList<AppEntry> FrequentApps(AppSettings settings, int limit) =>
        _entries.Where(app => settings.GetLaunchCount(app.Path, app.Arguments) > 0)
            .DistinctBy(app => AppSettings.AppKey(app.Path, app.Arguments))
            .OrderByDescending(app => settings.GetLaunchCount(app.Path, app.Arguments))
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();

    /// <summary>
    /// 加载应用索引：缓存有效时直接读盘，否则重新扫描开始菜单并重建图标。
    /// 从文件搜索启动过的便携 exe 单独持久化，不随开始菜单缓存一起被覆盖。
    /// </summary>
    /// <param name="dataDir">%APPDATA%\QLaunch 目录。</param>
    /// <param name="forceRebuild">忽略缓存强制重扫。</param>
    /// <returns>无返回值。</returns>
    public void Load(string dataDir, bool forceRebuild)
    {
        _iconDir = Path.Combine(dataDir, "icons");
        Directory.CreateDirectory(_iconDir);
        _portableFile = Path.Combine(dataDir, "portable.json");
        LoadPortable();
        string cacheFile = Path.Combine(dataDir, "apps.json");
        if (!forceRebuild && File.Exists(cacheFile) && TryLoadCache(cacheFile))
        {
            RebuildEntries();
            return;
        }
        Scan();
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(_scanned, CacheOptions));
        RebuildEntries();
    }

    private static readonly JsonSerializerOptions CacheOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private bool TryLoadCache(string file)
    {
        try
        {
            List<AppEntry>? loaded = JsonSerializer.Deserialize<List<AppEntry>>(File.ReadAllText(file));
            if (loaded is null || loaded.Count == 0)
            {
                return false;
            }
            _scanned.Clear();
            _scanned.AddRange(loaded);
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[appindex] 缓存不可用: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 枚举当前用户与全体用户的开始菜单项，解析 .lnk 目标并抽取图标。
    /// </summary>
    /// <returns>无返回值。</returns>
    private void Scan()
    {
        _scanned.Clear();
        List<string> roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];
        Dictionary<string, AppEntry> byKey = new(StringComparer.OrdinalIgnoreCase);
        // 开始菜单里有受 ACL 保护的子目录，逐个容错，不能让单个目录把整次扫描打断
        foreach (string link in roots.Where(Directory.Exists).SelectMany(WalkFiles))
        {
            string ext = Path.GetExtension(link).ToLowerInvariant();
            if (ext is not (".lnk" or ".appref-ms" or ".url"))
            {
                continue;
            }
            AppEntry? entry = BuildEntry(link, ext);
            if (entry is null)
            {
                continue;
            }
            string key = $"{entry.Name}|{entry.Path}|{entry.Arguments}";
            if (byKey.TryAdd(key, entry))
            {
                _scanned.Add(entry);
            }
        }
        AppendUwpApps(byKey);
    }

    /// <summary>
    /// 追加商店应用。Win11 的记事本等只有 AppsFolder 入口，不落到开始菜单 .lnk，
    /// 用 Get-StartApps 拿全量后只保留 UWP 形态（AppID 含 !）的项。
    /// </summary>
    /// <param name="byKey">已收录项的键表，用于跳过与 .lnk 重复的 Win32 应用。</param>
    /// <returns>无返回值。</returns>
    private void AppendUwpApps(Dictionary<string, AppEntry> byKey)
    {
        string output;
        try
        {
            ProcessStartInfo psi = new("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -Command \"Get-StartApps | ForEach-Object { $_.Name + [char]9 + $_.AppID }\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"[appindex] Get-StartApps 启动失败: {e.Message}");
            return;
        }

        foreach (string line in output.Split('\n'))
        {
            string[] parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 2)
            {
                continue;
            }
            string name = parts[0].Trim();
            string appId = parts[1].Trim();
            if (name.Length == 0 || !appId.Contains('!'))
            {
                continue;
            }
            string path = $"shell:AppsFolder\\{appId}";
            string key = $"{name}|{path}|";
            if (byKey.ContainsKey(key))
            {
                continue;
            }
            AppEntry entry = new()
            {
                Name = name,
                Initials = Pinyin.GetInitials(name),
                Path = path,
                Arguments = "",
                Icon = WriteIcon(appId, name, uwp: true),
                IsUwp = true
            };
            byKey[key] = entry;
            _scanned.Add(entry);
        }
    }

    /// <summary>
    /// 把开始菜单扫描结果与便携 exe 合并成对外索引，同一路径且无参数的项只保留扫描版本。
    /// </summary>
    /// <returns>无返回值。</returns>
    private void RebuildEntries()
    {
        _entries.Clear();
        _entries.AddRange(_scanned);
        HashSet<string> keys = new(_entries.Select(EntryKey), StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry entry in _portable)
        {
            if (keys.Add(EntryKey(entry)))
            {
                _entries.Add(entry);
            }
        }
    }

    private static string EntryKey(AppEntry entry) => entry.Path.ToUpperInvariant() + "\n" + entry.Arguments;

    private void LoadPortable()
    {
        if (!File.Exists(_portableFile))
        {
            return;
        }
        try
        {
            List<AppEntry>? loaded = JsonSerializer.Deserialize<List<AppEntry>>(File.ReadAllText(_portableFile), CacheOptions);
            if (loaded is null)
            {
                return;
            }
            _portable.Clear();
            foreach (AppEntry? entry in loaded)
            {
                if (entry is null || string.IsNullOrEmpty(entry.Path))
                {
                    continue;
                }
                entry.IsPortable = true;
                _portable.Add(entry);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Debug.WriteLine($"[appindex] 便携索引不可用: {e.Message}");
            _portable.Clear();
        }
    }

    private void SavePortable()
    {
        try
        {
            File.WriteAllText(_portableFile, JsonSerializer.Serialize(_portable, CacheOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Debug.WriteLine($"[appindex] 保存便携索引失败: {e.Message}");
        }
    }

    /// <summary>
    /// 把从文件搜索启动的 exe 登记为应用：抽取图标并写入便携索引，下次空输入时能进常用应用。
    /// 路径已在开始菜单索引里时直接返回原条目，不重复登记。
    /// </summary>
    /// <param name="exePath">exe 完整路径。</param>
    /// <returns>用于记录启动计数的应用条目。</returns>
    public AppEntry RegisterPortable(string exePath)
    {
        string path = exePath;
        try
        {
            path = Path.GetFullPath(exePath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Debug.WriteLine($"[appindex] 便携路径无效 {exePath}: {e.Message}");
        }
        foreach (AppEntry entry in _entries)
        {
            if (entry.Arguments.Length == 0 && entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        string name = Path.GetFileNameWithoutExtension(path);
        AppEntry portable = new()
        {
            Name = name,
            Initials = Pinyin.GetInitials(name),
            Path = path,
            Arguments = "",
            Icon = WriteIcon(path, name, fileKey: PortableKey(path, name)),
            IsPortable = true
        };
        _portable.Add(portable);
        _entries.Add(portable);
        SavePortable();
        return portable;
    }

    /// <summary>
    /// 移除便携 exe 的索引条目与图标。开始菜单扫描出来的应用不在这里处理，清掉计数即可。
    /// </summary>
    /// <param name="path">应用路径。</param>
    /// <param name="arguments">启动参数，携带参数的不属于便携登记。</param>
    /// <returns>无返回值。</returns>
    public void ForgetPortable(string path, string arguments)
    {
        if (arguments.Length != 0)
        {
            return;
        }
        AppEntry? entry = _portable.FirstOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }
        _portable.Remove(entry);
        _entries.Remove(entry);
        SavePortable();
        DeleteIcon(entry.Icon);
    }

    private void DeleteIcon(string file)
    {
        if (file.Length == 0)
        {
            return;
        }
        string? key = _iconCache.FirstOrDefault(pair => pair.Value.Equals(file, StringComparison.OrdinalIgnoreCase)).Key;
        if (key is not null)
        {
            _iconCache.Remove(key);
        }
        try
        {
            string full = Path.Combine(_iconDir, file);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[appindex] 删除图标 {file} 失败: {e.Message}");
        }
    }

    /// <summary>
    /// 便携条目按路径哈希生成图标文件名，避免不同目录下同名 exe 互相覆盖图标。
    /// </summary>
    private static string PortableKey(string path, string name)
    {
        uint hash = 2166136261;
        foreach (char c in path.ToUpperInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return $"{name}.{hash:x8}";
    }

    /// <summary>
    /// 深度遍历目录并产出文件路径，遇到无权限或已消失的目录时跳过该分支。
    /// </summary>
    /// <param name="root">遍历起点。</param>
    /// <returns>可访问到的文件完整路径。</returns>
    private static IEnumerable<string> WalkFiles(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] files;
            string[] subDirs;
            try
            {
                files = Directory.GetFiles(current);
                subDirs = Directory.GetDirectories(current);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                Debug.WriteLine($"[appindex] 跳过 {current}: {e.Message}");
                continue;
            }
            foreach (string dir in subDirs)
            {
                pending.Push(dir);
            }
            foreach (string file in files)
            {
                yield return file;
            }
        }
    }

    private AppEntry? BuildEntry(string link, string ext)
    {
        string name = Path.GetFileNameWithoutExtension(link);
        if (name.Equals("programs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("start menu programs", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~", StringComparison.Ordinal))
        {
            return null;
        }
        string target = "";
        string args = "";
        string workingDir = "";
        if (ext == ".lnk")
        {
            ResolveShortcut(link, out target, out args, out workingDir);
        }
        else if (ext == ".url")
        {
            target = link;
        }
        else
        {
            // .appref-ms 是 ClickOnce 入口，必须交给 shell 处理
            target = link;
        }
        if (string.IsNullOrEmpty(target))
        {
            target = link;
        }
        string icon = WriteIcon(string.IsNullOrEmpty(workingDir) ? target : link, name);
        return new AppEntry
        {
            Name = name,
            Initials = Pinyin.GetInitials(name),
            Path = target,
            Arguments = args,
            Icon = icon,
            IsUwp = target.Contains("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) || ext == ".appref-ms"
        };
    }

    private static void ResolveShortcut(string link, out string target, out string args, out string workingDir)
    {
        target = args = workingDir = "";
        object? shellObj = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
        if (shellObj is null)
        {
            return;
        }
        try
        {
            dynamic sc = shellObj;
            dynamic shortcut = sc.CreateShortcut(link);
            target = (string)shortcut.TargetPath;
            args = (string)shortcut.Arguments;
            workingDir = (string)shortcut.WorkingDirectory;
        }
        catch (COMException e)
        {
            Debug.WriteLine($"[appindex] 解析 {link} 失败: {e.Message}");
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellObj);
        }
    }

    /// <summary>
    /// 从 shell 提取图标并写成 PNG。UWP 条目只有 AppsFolder 里的 AppID，
    /// Shell 必须解析成 PIDL 才能取到磁贴图标，直接传 shell: 字符串拿不到。
    /// </summary>
    /// <param name="source">可执行文件/.lnk 路径，UWP 时是 AppID。</param>
    /// <param name="name">用于生成图标文件名的显示名。</param>
    /// <param name="uwp">true 表示按 AppsFolder 应用解析。</param>
    /// <param name="fileKey">图标缓存键；便携应用用路径哈希避免同名覆盖。</param>
    /// <returns>图标文件名；失败时为空串。</returns>
    private string WriteIcon(string source, string name, bool uwp = false, string? fileKey = null)
    {
        string key = fileKey ?? name;
        string safe = string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (_iconCache.TryGetValue(safe, out string? cached))
        {
            return cached;
        }
        string file = $"{safe}.png";
        string full = Path.Combine(_iconDir, file);
        if (File.Exists(full))
        {
            _iconCache[safe] = file;
            return file;
        }
        try
        {
            IntPtr hIcon = uwp ? UwpIcon(source) : ShellIcon(source);
            if (hIcon == IntPtr.Zero)
            {
                return "";
            }
            try
            {
                using System.Drawing.Icon icon = System.Drawing.Icon.FromHandle(hIcon);
                using System.Drawing.Bitmap raw = icon.ToBitmap();
                using System.Drawing.Bitmap bmp = new(raw, IconSize, IconSize);
                bmp.Save(full, System.Drawing.Imaging.ImageFormat.Png);
            }
            finally
            {
                Native.DestroyIcon(hIcon);
            }
            _iconCache[safe] = file;
            return file;
        }
        catch (Exception e) when (e is ExternalException or InvalidOperationException or ArgumentException)
        {
            Debug.WriteLine($"[appindex] 图标失败 {name}: {e.Message}");
            return "";
        }
    }

    private static IntPtr ShellIcon(string path)
    {
        WinShell.Info info = new();
        WinShell.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<WinShell.Info>(), SHGFI_ICON | SHGFI_LARGEICON);
        return info.hIcon;
    }

    private static IntPtr UwpIcon(string appId)
    {
        int hr = WinShell.SHParseDisplayName($"shell:AppsFolder\\{appId}", IntPtr.Zero, out IntPtr pidl, 0, out _);
        if (pidl == IntPtr.Zero)
        {
            Debug.WriteLine($"[appindex] AppsFolder 解析失败 {appId}: hr={hr}");
            return IntPtr.Zero;
        }
        try
        {
            WinShell.Info info = new();
            WinShell.SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<WinShell.Info>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_PIDL);
            return info.hIcon;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>
    /// 图标 PNG 所在目录，结果面板按 Entry.Icon 文件名从这里读取。
    /// </summary>
    /// <returns>图标所在绝对路径。</returns>
    public string IconDirectory => _iconDir;
}

internal static class WinShell
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Info
    {
        public Info()
        {
        }

        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName = "";
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szTypeName = "";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref Info psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    public static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, ref Info psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    public static IntPtr SHGetFileInfo(string path, uint attrs, ref Info info, uint size, uint flags)
        => SHGetFileInfoW(path, attrs, ref info, size, flags);
}
