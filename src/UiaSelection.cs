using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace QLaunch;

internal static class UiaSelection
{
    public static string? Read(IntPtr focus, IntPtr foreground)
    {
        try
        {
            if (Native.GetForegroundWindow() == foreground)
            {
                AutomationElement? element = AutomationElement.FocusedElement;
                if (Native.GetForegroundWindow() != foreground)
                {
                    return null;
                }
                // Chromium 的选区可能由焦点元素的 Document 祖先提供 TextPattern。
                for (int depth = 0; element is not null && depth < 8; depth++)
                {
                    string? text = ReadPattern(element);
                    if (text is not null)
                    {
                        return text;
                    }
                    if (element.Current.NativeWindowHandle == foreground.ToInt64())
                    {
                        break;
                    }
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
            }
            return ReadPattern(AutomationElement.FromHandle(focus));
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException
            or InvalidOperationException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"[uia] Selection unavailable: {e.Message}");
            return null;
        }
    }

    private static string? ReadPattern(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object? pattern))
        {
            return null;
        }
        StringBuilder text = new();
        foreach (TextPatternRange range in ((TextPattern)pattern).GetSelection())
        {
            text.Append(range.GetText(-1));
            if (text.Length >= SelectionCapture.MaxChars)
            {
                break;
            }
        }
        string selected = text.ToString(0, Math.Min(text.Length, SelectionCapture.MaxChars)).Trim();
        return selected.Length == 0 ? null : selected;
    }
}
