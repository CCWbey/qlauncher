namespace QLaunch;

/// <summary>
/// 搜索输入框。占位文案由控件自己画：原生 cue banner 在拿到焦点后就消失，而划词模式下必须在
/// 聚焦时仍然提示"已捕获选中文本，继续输入可改搜应用/文件"。
/// </summary>
internal sealed class SearchBox : System.Windows.Forms.TextBox
{
    private const int WM_PAINT = 0x000F;
    private static readonly Color PlaceholderColor = System.Drawing.Color.FromArgb(0x56, 0x5c, 0x66);

    private string _placeholder = "";

    public Func<MoveDirection, bool>? MoveKey;
    public Func<bool, bool>? Commit;
    public Action? Cancelled;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            if (_placeholder != value)
            {
                _placeholder = value;
                Invalidate();
            }
        }
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT && Text.Length == 0 && _placeholder.Length > 0)
        {
            using System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(Handle);
            TextRenderer.DrawText(g, _placeholder, Font, ClientRectangle, PlaceholderColor,
                BackColor, System.Windows.Forms.TextFormatFlags.Left
                    | System.Windows.Forms.TextFormatFlags.VerticalCenter
                    | System.Windows.Forms.TextFormatFlags.EndEllipsis
                    | System.Windows.Forms.TextFormatFlags.NoPrefix);
        }
    }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, System.Windows.Forms.Keys keyData)
    {
        // 输入法组合期间的回车与方向键是候选窗在用的，交给输入法，不能当成功键
        if (Native.IsComposing(Handle))
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }
        bool handled = keyData switch
        {
            System.Windows.Forms.Keys.Escape => Take(Cancelled),
            System.Windows.Forms.Keys.Return | System.Windows.Forms.Keys.Control => Commit?.Invoke(true) == true,
            System.Windows.Forms.Keys.Return => Commit?.Invoke(false) == true,
            System.Windows.Forms.Keys.Up => MoveKey?.Invoke(MoveDirection.Up) == true,
            System.Windows.Forms.Keys.Down => MoveKey?.Invoke(MoveDirection.Down) == true,
            System.Windows.Forms.Keys.Left => MoveKey?.Invoke(MoveDirection.Left) == true,
            System.Windows.Forms.Keys.Right => MoveKey?.Invoke(MoveDirection.Right) == true,
            _ => false
        };
        return handled || base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool Take(Action? action)
    {
        action?.Invoke();
        return true;
    }
}
