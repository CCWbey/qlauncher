using System.Runtime.InteropServices;
using System.Text;

namespace QLaunch;

internal sealed class EverythingSearch
{
    private const string DllName = "Everything64.dll";

    private const uint RequestFileName = 0x00000001;
    private const uint RequestPath = 0x00000002;
    private const uint RequestSize = 0x00000010;
    private const uint RequestDateModified = 0x00000040;
    private const uint RequestFullPath = 0x00000004;

    private const uint ErrorIpc = 2;
    private const uint ErrorMemory = 1;
    // 名称升序是 Everything 标为免费（无需 fast sort 额外开销）的排序，只用来截断候选池
    private const uint SortNameAscending = 1;

    private static readonly object Lock = new();

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern void Everything_SetSearchW(string search);

    [DllImport(DllName)]
    private static extern void Everything_SetMax(uint max);

    [DllImport(DllName)]
    private static extern void Everything_SetOffset(uint offset);

    [DllImport(DllName)]
    private static extern void Everything_SetRequestFlags(uint flags);

    [DllImport(DllName)]
    private static extern void Everything_SetSort(uint sort);

    [DllImport(DllName)]
    private static extern bool Everything_QueryW(bool wait);

    [DllImport(DllName)]
    private static extern uint Everything_GetNumResults();

    [DllImport(DllName)]
    private static extern uint Everything_GetLastError();

    [DllImport(DllName)]
    private static extern bool Everything_IsFolderResult(uint index);

    [DllImport(DllName)]
    private static extern bool Everything_GetResultDateModified(uint index, out long time);

    [DllImport(DllName)]
    private static extern void Everything_GetResultSize(uint index, out long size);

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetResultFullPathNameW(uint index, StringBuilder buffer, uint size);

    [DllImport(DllName)]
    private static extern void Everything_Reset();

    /// <summary>
    /// 通过 Everything 的 IPC 查询文件。Everything 未安装或未运行时会安全降级为空结果。
    /// </summary>
    /// <param name="keyword">传给 Everything 的搜索表达式，支持 ext: 等原生语法。</param>
    /// <param name="max">候选池上限；调用方重排后再截取实际显示条数。</param>
    /// <param name="error">失败时给出面向用户的中文原因，成功为 null。</param>
    /// <returns>匹配到的文件与文件夹候选，按 Everything 名称升序。</returns>
    public List<Result> Query(string keyword, int max, out string? error)
    {
        error = null;
        List<Result> items = [];
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return items;
        }
        lock (Lock)
        {
            try
            {
                Everything_SetSearchW(keyword);
                Everything_SetOffset(0);
                // 系统目录条目会在后面被滤掉，先按名称序过量抓取，否则过滤后候选池会缺口
                Everything_SetMax((uint)Math.Max(max * 2, 40));
                Everything_SetRequestFlags(RequestFileName | RequestPath | RequestFullPath | RequestSize | RequestDateModified);
                Everything_SetSort(SortNameAscending);
                if (!Everything_QueryW(true))
                {
                    error = DescribeError(Everything_GetLastError());
                    return items;
                }
                uint count = Everything_GetNumResults();
                StringBuilder path = new(4096);
                for (uint i = 0; i < count && items.Count < max; i++)
                {
                    path.Clear();
                    if (Everything_GetResultFullPathNameW(i, path, (uint)path.Capacity) == 0)
                    {
                        continue;
                    }
                    string full = path.ToString();
                    if (IsNoise(full))
                    {
                        continue;
                    }
                    bool folder = Everything_IsFolderResult(i);
                    Everything_GetResultSize(i, out long size);
                    Everything_GetResultDateModified(i, out long modified);
                    string trimmed = full.TrimEnd('\\');
                    items.Add(new Result
                    {
                        Kind = folder ? "folder" : "file",
                        Title = Path.GetFileName(trimmed),
                        Subtitle = folder ? trimmed : Path.GetDirectoryName(trimmed) ?? trimmed,
                        Path = full,
                        Size = folder ? 0 : size,
                        Modified = FromFileTime(modified)
                    });
                }
            }
            catch (DllNotFoundException)
            {
                error = "未找到 Everything64.dll";
            }
            catch (EntryPointNotFoundException e)
            {
                error = $"Everything SDK 入口缺失: {e.Message}";
            }
        }
        return items;
    }

    /// <summary>
    /// 释放 SDK 内部缓存的 IPC 窗口与监听线程。
    /// </summary>
    /// <returns>无返回值。</returns>
    public void Reset()
    {
        try
        {
            Everything_Reset();
        }
        catch (DllNotFoundException)
        {
        }
    }

    private static string DescribeError(uint code) => code switch
    {
        ErrorIpc => "Everything 未运行，请先启动 Everything",
        ErrorMemory => "Everything 内存不足",
        _ => $"Everything 返回错误码 {code}"
    };

    private static DateTime FromFileTime(long value)
    {
        if (value <= 0)
        {
            return default;
        }
        try
        {
            return DateTime.FromFileTimeUtc(value).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }

    private static bool IsNoise(string path) =>
        path.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
        || path.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase)
        || path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
}
