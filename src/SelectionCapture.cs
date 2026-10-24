using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QLaunch;

internal sealed class SelectionCapture
{
    // 选整章、整页时文本会远超日常划词；上限只用来挡住整本文档级别的意外全选，
    // 正常长文本要截断后继续翻译，不能直接丢掉，否则看起来就是划词没反应。
    internal const int MaxChars = 20000;
    private static Task<string?>? _uiaWorker;

    private readonly IntPtr _foreground;
    private readonly IntPtr _focus;
    private readonly Task<string?> _uia;
    private readonly bool _canCapture;

    private SelectionCapture(IntPtr foreground, IntPtr focus, Task<string?> uia, bool canCapture)
    {
        _foreground = foreground;
        _focus = focus;
        _uia = uia;
        _canCapture = canCapture;
    }

    public bool TargetStillForeground => Native.GetForegroundWindow() == _foreground;

    public static SelectionCapture Start()
    {
        IntPtr foreground = Native.GetForegroundWindow();
        uint thread = Native.GetWindowThreadProcessId(foreground, out uint pid);
        IntPtr focus = Native.GetFocusWindow(thread);
        if (focus == IntPtr.Zero)
        {
            focus = foreground;
        }
        Task<string?> uia = Task.FromResult<string?>(null);
        // 卡住的 UIA 提供程序不能在每次唤出时再占用一个线程。
        if (foreground != IntPtr.Zero && pid != Environment.ProcessId
            && (_uiaWorker is null || _uiaWorker.IsCompleted))
        {
            _uiaWorker = Task.Run(() => UiaSelection.Read(focus, foreground));
            uia = _uiaWorker;
        }
        return new SelectionCapture(foreground, focus, uia, foreground != IntPtr.Zero && pid != Environment.ProcessId);
    }

    public async Task<string?> FinishAsync(CancellationToken cancellationToken)
    {
        if (!_canCapture)
        {
            return null;
        }
        Task completed = await Task.WhenAny(_uia, Task.Delay(60, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        string? direct = completed == _uia && _uia.IsCompletedSuccessfully ? Limit(_uia.Result) : null;
        if (direct is not null)
        {
            return direct;
        }

        Stopwatch release = Stopwatch.StartNew();
        while (Native.ModifiersHeld())
        {
            // 等 Alt 抬起再发 Ctrl+C，但上限压到 500ms：搜索栏迟迟拿不到焦点比取词更难受
            if (!TargetStillForeground || release.ElapsedMilliseconds >= 500)
            {
                return null;
            }
            await Task.Delay(15, cancellationToken);
        }
        if (!TargetStillForeground)
        {
            return null;
        }
        if (_uia.IsCompletedSuccessfully && Limit(_uia.Result) is string selected)
        {
            return selected;
        }

        string? originalText;
        StringCollection? originalFiles;
        System.Drawing.Image? originalImage;
        try
        {
            originalText = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            originalFiles = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList() : null;
            originalImage = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        }
        catch (Exception e) when (e is ExternalException or InvalidOperationException)
        {
            Debug.WriteLine($"[selection] Clipboard snapshot failed: {e.Message}");
            return null;
        }

        uint sequence = Native.ClipboardSequence();
        uint capturedSequence = sequence;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Native.SendCtrlC(_foreground))
            {
                return null;
            }
            Stopwatch budget = Stopwatch.StartNew();
            bool copyPosted = false;
            while (budget.ElapsedMilliseconds < 280)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TargetStillForeground)
                {
                    return null;
                }
                uint current = Native.ClipboardSequence();
                if (current != sequence)
                {
                    capturedSequence = current;
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            string text = Clipboard.GetText();
                            capturedSequence = Native.ClipboardSequence();
                            return Limit(text);
                        }
                    }
                    catch (Exception e) when (e is ExternalException or InvalidOperationException)
                    {
                        Debug.WriteLine($"[selection] Clipboard busy: {e.Message}");
                    }
                }
                if (!TargetStillForeground)
                {
                    return null;
                }
                // WM_COPY 仅作为按键无结果时的标准编辑控件兜底，不能与 Ctrl+C 同时排队。
                if (budget.ElapsedMilliseconds >= 150 && !copyPosted)
                {
                    copyPosted = true;
                    Native.PostCopy(_focus);
                }
                await Task.Delay(30, cancellationToken);
            }
            return _uia.IsCompletedSuccessfully ? Limit(_uia.Result) : null;
        }
        finally
        {
            // 不覆盖用户在取词结束后主动复制的新内容。
            if (capturedSequence != sequence && Native.ClipboardSequence() == capturedSequence)
            {
                try
                {
                    if (originalText is null && originalFiles is null && originalImage is null)
                    {
                        Clipboard.Clear();
                    }
                    else
                    {
                        DataObject data = new();
                        if (originalText is not null)
                        {
                            data.SetData(DataFormats.UnicodeText, originalText);
                        }
                        if (originalFiles is not null)
                        {
                            data.SetData(DataFormats.FileDrop, originalFiles.Cast<string>().ToArray());
                        }
                        if (originalImage is not null)
                        {
                            data.SetData(DataFormats.Bitmap, originalImage);
                        }
                        Clipboard.SetDataObject(data, true);
                    }
                }
                catch (Exception e) when (e is ExternalException or InvalidOperationException)
                {
                    Debug.WriteLine($"[selection] Clipboard restore failed: {e.Message}");
                }
            }
            originalImage?.Dispose();
        }
    }

    private static string? Limit(string? text)
    {
        if (text is null)
        {
            return null;
        }
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }
        return trimmed.Length > MaxChars ? trimmed[..MaxChars] : trimmed;
    }
}
