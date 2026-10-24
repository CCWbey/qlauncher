using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace QLaunch;

/// <summary>
/// 结果区。首页是 6 列网格，查询态是带分组标题的行列表，译文整段换行并随列表滚动。
/// 布局在数据或尺寸变化时算一次，绘制与命中测试读同一份分段表，避免出现两套坐标。
/// </summary>
internal sealed class ResultPanel : Control
{
    private enum SegKind { Heading, Grid, Row, Empty }

    private const int HeadingPx = 22;
    private const int RowPx = 44;
    private const int TitlePx = 19;
    private const int SubPx = 16;
    private const int GridCellPx = 88;

    /// <summary>Inner 只给译文行用：正文换行后的高度，绘制时据此摆放原文。</summary>
    private readonly record struct Segment(SegKind Kind, int Top, int Height, int FirstRow, int Count, string Text, int Inner);

    private static readonly Color Surface = Color.FromArgb(0x18, 0x1a, 0x1f);
    private static readonly Color Foreground = Color.FromArgb(0xe8, 0xea, 0xed);
    private static readonly Color Muted = Color.FromArgb(0x9a, 0xa0, 0xa6);
    private static readonly Color Accent = Color.FromArgb(0x4d, 0x8d, 0xff);
    private static readonly Color Hover = Color.FromArgb(0x23, 0x27, 0x2e);
    private static readonly Color GlyphBack = Color.FromArgb(0x2b, 0x30, 0x38);
    private static readonly Color TransBack = Color.FromArgb(0x24, 0x40, 0x6b);
    private static readonly Color TransFore = Color.FromArgb(0xd6, 0xe6, 0xff);

    private static readonly SolidBrush AccentBrush = new(Accent);
    private static readonly SolidBrush HoverBrush = new(Hover);
    private static readonly SolidBrush GlyphBackBrush = new(GlyphBack);
    private static readonly SolidBrush TransBackBrush = new(TransBack);

    private static readonly Dictionary<string, string> GroupNames = new()
    {
        ["trans"] = "翻译",
        ["app"] = "应用",
        ["folder"] = "文件夹",
        ["file"] = "文件"
    };

    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["trans"] = "译",
        ["app"] = "应",
        ["folder"] = "D",
        ["file"] = "F"
    };

    private readonly LauncherModel _model;
    private readonly string _iconDir;
    private readonly Dictionary<string, Image?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Segment> _segments = [];

    private Font _title = null!, _sub = null!, _heading = null!, _glyph = null!, _glyphBig = null!;
    private int _dpi;
    private int _contentHeight;
    private int _scroll;

    public event Action<int, bool>? Fire;
    public event Action<int, Point>? MenuRequested;

    public ResultPanel(LauncherModel model, string iconDir)
    {
        _model = model;
        _iconDir = iconDir;
        BackColor = Surface;
        ForeColor = Foreground;
        // 不可获得焦点：点行、点右键都不能把输入框的焦点抢走，否则用户点完就不能继续打字
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw, true);
    }

    private int S(float pixels) => (int)MathF.Round(pixels * Math.Max(96, _dpi) / 96f);

    /// <summary>数据变化后重算布局并整块重绘。</summary>
    public void SyncContent()
    {
        EnsureFonts();
        Relayout();
        Invalidate();
    }

    /// <summary>只改了选中项：把它滚进可视区并重绘。</summary>
    public void SyncSelection()
    {
        EnsureFonts();
        EnsureActiveVisible();
        Invalidate();
    }

    /// <summary>滚轮增量，由宿主转发：面板不可获得焦点，WM_MOUSEWHEEL 只会发给获得焦点的输入框。</summary>
    public void ScrollBy(int delta)
    {
        int step = S(40) * Math.Max(1, SystemInformation.MouseWheelScrollLines);
        SetScroll(_scroll - delta / 120 * step);
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        SyncContent();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (EnsureFonts())
        {
            Relayout();
        }
        Graphics g = e.Graphics;
        g.Clear(Surface);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        foreach (Segment seg in _segments)
        {
            int y = seg.Top - _scroll;
            if (y + seg.Height < 0 || y > Height)
            {
                continue;
            }
            switch (seg.Kind)
            {
                case SegKind.Heading:
                    TextRenderer.DrawText(g, seg.Text, _heading, new Rectangle(S(16), y, Width - S(32), seg.Height),
                        Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    break;
                case SegKind.Grid:
                    PaintGrid(g, seg, y);
                    break;
                case SegKind.Row:
                    PaintRow(g, seg, y);
                    break;
                case SegKind.Empty:
                    TextRenderer.DrawText(g, seg.Text, _title, new Rectangle(S(16), y, Width - S(32), seg.Height),
                        Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    break;
            }
        }
    }

    private void PaintRow(Graphics g, Segment seg, int y)
    {
        Result row = _model.Rows[seg.FirstRow];
        bool prose = row.Kind == "trans";
        if (seg.FirstRow == _model.Active)
        {
            g.FillRectangle(HoverBrush, 0, y, Width, seg.Height);
            g.FillRectangle(AccentBrush, 0, y, S(3), seg.Height);
        }

        int iconSize = S(22);
        PaintIcon(g, row, S(16), prose ? y + S(8) : y + (seg.Height - iconSize) / 2, iconSize);

        string meta = MetaText(row);
        int metaWidth = meta.Length == 0 ? 0 : TextRenderer.MeasureText(g, meta, _sub, Size.Empty,
            TextFormatFlags.NoPadding).Width;
        int metaGap = meta.Length == 0 ? 0 : S(12);
        int textLeft = S(16) + iconSize + S(11);
        int textWidth = Math.Max(S(40), Width - textLeft - S(16) - metaWidth - metaGap);

        if (prose)
        {
            TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, row.Title, _title, new Rectangle(textLeft, y + S(8), textWidth, seg.Inner),
                Foreground, wrap);
            if (row.Subtitle.Length > 0)
            {
                int subHeight = seg.Height - seg.Inner - S(16) - S(4);
                TextRenderer.DrawText(g, row.Subtitle, _sub,
                    new Rectangle(textLeft, y + S(8) + seg.Inner + S(4), textWidth, Math.Max(S(14), subHeight)), Muted, wrap);
            }
        }
        else
        {
            int titleTop = y + Math.Max(0, (seg.Height - S(TitlePx) - S(SubPx)) / 2);
            const TextFormatFlags clip = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, row.Title, _title, new Rectangle(textLeft, titleTop, textWidth, S(TitlePx)),
                Foreground, clip | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, row.Subtitle, _sub, new Rectangle(textLeft, titleTop + S(TitlePx), textWidth, S(SubPx)),
                Muted, clip | TextFormatFlags.VerticalCenter);
        }

        if (meta.Length > 0)
        {
            TextRenderer.DrawText(g, meta, _sub, new Rectangle(Width - S(16) - metaWidth, y, metaWidth, seg.Height),
                Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    private void PaintGrid(Graphics g, Segment seg, int y)
    {
        int gap = S(8);
        int cellHeight = S(GridCellPx);
        int cellWidth = CellWidth();
        int columns = LauncherModel.GridColumns;
        int iconSize = S(32);
        int titleHeight = S(TitlePx);
        int block = iconSize + S(8) + titleHeight;
        for (int i = 0; i < seg.Count; i++)
        {
            int index = seg.FirstRow + i;
            Result row = _model.Rows[index];
            int x = S(16) + (i % columns) * (cellWidth + gap);
            int top = y + (i / columns) * (cellHeight + gap);
            if (index == _model.Active)
            {
                using GraphicsPath path = RoundedPath(new Rectangle(x, top, cellWidth, cellHeight), S(8));
                g.FillPath(HoverBrush, path);
                using var pen = new Pen(Accent);
                g.DrawPath(pen, path);
            }
            int iconTop = top + Math.Max(0, (cellHeight - block) / 2);
            PaintIcon(g, row, x + (cellWidth - iconSize) / 2, iconTop, iconSize);
            TextRenderer.DrawText(g, row.Title, _title,
                new Rectangle(x + S(4), iconTop + iconSize + S(8), cellWidth - S(8), titleHeight),
                Foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    private void PaintIcon(Graphics g, Result row, int x, int y, int size)
    {
        Image? image = row.Kind == "app" ? IconFor(row.Icon) : null;
        if (image is not null)
        {
            g.DrawImage(image, x, y, size, size);
            return;
        }
        bool prose = row.Kind == "trans";
        using GraphicsPath path = RoundedPath(new Rectangle(x, y, size, size), S(5));
        g.FillPath(prose ? TransBackBrush : GlyphBackBrush, path);
        string glyph = row.Glyph.Length > 0 ? row.Glyph : Glyphs.GetValueOrDefault(row.Kind, "F");
        TextRenderer.DrawText(g, glyph, size > S(24) ? _glyphBig : _glyph,
            new Rectangle(x, y, size, size), prose ? TransFore : Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private int HitTest(Point point)
    {
        int y = point.Y + _scroll;
        foreach (Segment seg in _segments)
        {
            if (y < seg.Top || y >= seg.Top + seg.Height)
            {
                continue;
            }
            if (seg.Kind == SegKind.Row)
            {
                return seg.FirstRow;
            }
            if (seg.Kind != SegKind.Grid || point.X < S(16) || point.X > Width - S(16))
            {
                return -1;
            }
            int gap = S(8);
            int cellHeight = S(GridCellPx);
            int column = (point.X - S(16)) / (CellWidth() + gap);
            int line = (y - seg.Top) / (cellHeight + gap);
            if (column < 0 || column >= LauncherModel.GridColumns || line < 0)
            {
                return -1;
            }
            int index = line * LauncherModel.GridColumns + column;
            return index < seg.Count ? seg.FirstRow + index : -1;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = HitTest(e.Location);
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        if (index < 0)
        {
            // 掠过分组标题或留白时保持原来的选中项，只有指针真的落在某一行才改选
            return;
        }
        if (index != _model.Active)
        {
            _model.SetHover(index);
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int index = HitTest(e.Location);
        if (index < 0)
        {
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            Fire?.Invoke(index, (ModifierKeys & Keys.Control) == Keys.Control);
        }
        else if (e.Button == MouseButtons.Right)
        {
            _model.SetHover(index);
            Invalidate();
            MenuRequested?.Invoke(index, e.Location);
        }
    }

    private void Relayout()
    {
        EnsureFonts();
        _segments.Clear();
        if (Height <= 0 || Width <= 0)
        {
            _contentHeight = 0;
            _scroll = 0;
            return;
        }
        IReadOnlyList<Result> rows = _model.Rows;
        int top = 0;
        if (_model.IsHome)
        {
            int headingHeight = S(HeadingPx);
            _segments.Add(new Segment(SegKind.Heading, top, headingHeight, 0, 0, "常用应用", 0));
            top += headingHeight;
            if (rows.Count > 0)
            {
                int gap = S(8);
                int cellHeight = S(GridCellPx);
                int lines = (rows.Count + LauncherModel.GridColumns - 1) / LauncherModel.GridColumns;
                int gridHeight = lines * cellHeight + (lines - 1) * gap;
                _segments.Add(new Segment(SegKind.Grid, top, gridHeight, 0, rows.Count, "", 0));
                top += gridHeight;
            }
            else
            {
                top += AddEmpty(top);
            }
        }
        else
        {
            string lastGroup = "";
            for (int i = 0; i < rows.Count; i++)
            {
                string group = GroupNames.GetValueOrDefault(rows[i].Kind, rows[i].Kind);
                if (group != lastGroup)
                {
                    int headingHeight = S(HeadingPx);
                    _segments.Add(new Segment(SegKind.Heading, top, headingHeight, 0, 0, group, 0));
                    top += headingHeight;
                    lastGroup = group;
                }
                bool prose = rows[i].Kind == "trans";
                int inner = prose ? WrapHeight(rows[i].Title, _title) : 0;
                int sub = prose && rows[i].Subtitle.Length > 0 ? WrapHeight(rows[i].Subtitle, _sub) : 0;
                int height = prose ? Math.Max(S(RowPx), S(16) + inner + (sub > 0 ? S(4) + sub : 0)) : S(RowPx);
                _segments.Add(new Segment(SegKind.Row, top, height, i, 1, "", inner));
                top += height;
            }
            if (rows.Count == 0)
            {
                top += AddEmpty(top);
            }
        }
        _contentHeight = top + S(12);
        SetScroll(_scroll);
    }

    private int AddEmpty(int top)
    {
        int height = S(60);
        _segments.Add(new Segment(SegKind.Empty, top, height, 0, 0, _model.EmptyText, 0));
        return height;
    }

    private int CellWidth()
    {
        int gap = S(8);
        int columns = LauncherModel.GridColumns;
        return Math.Max(S(24), (Width - S(16) * 2 - gap * (columns - 1)) / columns);
    }

    private int WrapHeight(string text, Font font)
    {
        int textWidth = Math.Max(S(40), Width - S(16) * 2 - S(3) - S(22) - S(11));
        return TextRenderer.MeasureText(text, font, new Size(textWidth, int.MaxValue / 4),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
    }

    private void EnsureActiveVisible()
    {
        int index = _model.Active;
        if (index < 0)
        {
            return;
        }
        int gap = S(8);
        int cellHeight = S(GridCellPx);
        foreach (Segment seg in _segments)
        {
            int itemTop = seg.Top;
            int itemHeight = seg.Height;
            if (seg.Kind == SegKind.Grid)
            {
                if (index < seg.FirstRow || index >= seg.FirstRow + seg.Count)
                {
                    continue;
                }
                int line = (index - seg.FirstRow) / LauncherModel.GridColumns;
                itemTop = seg.Top + line * (cellHeight + gap);
                itemHeight = cellHeight;
            }
            else if (seg.Kind == SegKind.Row)
            {
                if (seg.FirstRow != index)
                {
                    continue;
                }
            }
            else
            {
                continue;
            }
            if (itemTop < _scroll)
            {
                SetScroll(itemTop);
            }
            else if (itemTop + itemHeight > _scroll + Height)
            {
                SetScroll(itemTop + itemHeight - Height);
            }
            return;
        }
    }

    private void SetScroll(int value) => _scroll = Math.Max(0, Math.Min(value, Math.Max(0, _contentHeight - Height)));

    private Image? IconFor(string file)
    {
        if (file.Length == 0)
        {
            return null;
        }
        if (_icons.TryGetValue(file, out Image? cached))
        {
            return cached;
        }
        Image? loaded = null;
        try
        {
            string path = Path.Combine(_iconDir, file);
            if (File.Exists(path))
            {
                // 读进内存再复制一份：直接按文件构造 Bitmap 会一直锁住文件，索引重建覆盖同名图标时会失败
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                using var source = new Bitmap(stream);
                loaded = new Bitmap(source);
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            Debug.WriteLine($"[icon] {file} 读取失败: {e.Message}");
            loaded = null;
        }
        _icons[file] = loaded;
        return loaded;
    }

    internal static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        int diameter = Math.Max(1, radius * 2);
        GraphicsPath path = new();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.#} GB"
    };

    private static string MetaText(Result row)
    {
        string size = row.Size > 0 ? HumanSize(row.Size) : "";
        string when = row.Modified == default ? "" : row.Modified.ToString("yyyy-MM-dd HH:mm");
        if (size.Length == 0)
        {
            return when;
        }
        return when.Length == 0 ? size : $"{when}  {size}";
    }

    /// <returns>true 表示字体因 DPI 变化被重建，调用方需要重算布局。</returns>
    private bool EnsureFonts()
    {
        if (_title is not null && _dpi == DeviceDpi)
        {
            return false;
        }
        _title?.Dispose();
        _sub?.Dispose();
        _heading?.Dispose();
        _glyph?.Dispose();
        _glyphBig?.Dispose();
        _dpi = DeviceDpi;
        // 按像素定字号，DPI 变化时随 S() 一起重算，避免 GDI 再把字体缩放一次。
        // 首页宫格标题与结果行标题属于同一级，必须同字号；分组标题、路径、时间、大小统一用次级字号。
        _title = PixelFont(14);
        _sub = PixelFont(11);
        _heading = PixelFont(11);
        _glyph = PixelFont(12);
        _glyphBig = PixelFont(18);
        return true;
    }

    private static Font PixelFont(float pixels) => new("Microsoft YaHei UI", pixels, FontStyle.Regular, GraphicsUnit.Pixel);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Image? image in _icons.Values)
            {
                image?.Dispose();
            }
            _title.Dispose();
            _sub.Dispose();
            _heading.Dispose();
            _glyph.Dispose();
            _glyphBig.Dispose();
        }
        base.Dispose(disposing);
    }
}
