using System.Diagnostics;

namespace QLaunch;

internal sealed class LauncherForm : Form
{
    private const int HotkeyId = 1;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int HeaderPx = 52;
    private const int NotePx = 28;

    private static readonly Color Surface = Color.FromArgb(0x18, 0x1a, 0x1f);
    private static readonly Color Foreground = Color.FromArgb(0xe8, 0xea, 0xed);
    private static readonly Color Muted = Color.FromArgb(0x9a, 0xa0, 0xa6);
    private static readonly Color Divider = Color.FromArgb(0x2a, 0x2e, 0x36);
    private static readonly Color NoteColor = Color.FromArgb(0xd0, 0xa2, 0x4c);
    private static readonly Color MenuBack = Color.FromArgb(0x23, 0x27, 0x2e);
    private static readonly Color MenuLine = Color.FromArgb(0x3a, 0x3f, 0x47);
    private static readonly Color MenuPick = Color.FromArgb(0x2f, 0x35, 0x3e);
    private static readonly Color Accent = Color.FromArgb(0x4d, 0x8d, 0xff);

    private readonly AppSettings _settings;
    private readonly AppIndex _index;
    private readonly string? _vsCode;
    private readonly EverythingSearch _everything = new();
    private readonly FileUsageStore _fileUsage;
    private readonly Translator _translator = new();
    private readonly CloudTranslator _cloud = CloudTranslator.FromEnvironment();
    private readonly LauncherModel _model = new();
    private readonly NotifyIcon _tray;
    private readonly SearchBox _input;
    private readonly ResultPanel _panel;
    private readonly Label _clock;
    private readonly Label _note;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 60 };
    private readonly System.Windows.Forms.Timer _flash = new() { Interval = 1200 };
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _blur = new() { Interval = 60 };
    private ContextMenuStrip? _menu;
    private Font? _inputFont, _clockFont, _noteFont, _menuFont;
    private int _dpi;
    private bool _awaitingFocus;
    private bool _hotkeyTaken;
    private readonly bool _showOnStartup;
    private CancellationTokenSource? _summonCancellation;
    private CancellationTokenSource? _queryCancellation;

    /// <summary>
    /// 组装搜索栏宿主窗口：无边框置顶、原生绘制结果列表、负责全局热键与托盘。
    /// </summary>
    /// <param name="settings">已加载的设置。</param>
    /// <param name="index">应用索引。</param>
    /// <param name="showOnStartup">true 时启动即弹出，用于热键被占用时的界面自检。</param>
    public LauncherForm(AppSettings settings, AppIndex index, bool showOnStartup = false)
    {
        _settings = settings;
        _index = index;
        _fileUsage = FileUsageStore.Load(settings.DataDir);
        _showOnStartup = showOnStartup;
        _vsCode = FindVsCode();
        _model.VsCodeAvailable = _vsCode is not null;
        Text = "QLaunch";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Surface;
        // 尺寸与字号全部按 DeviceDpi 手算，再让 WinForms 自动缩放一次就会双重放大
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // 启动时先以全透明显示一次：句柄建好才能注册热键，又不闪一下
        Opacity = 0;

        _input = new SearchBox
        {
            ForeColor = Foreground,
            BackColor = Surface,
            BorderStyle = BorderStyle.None
        };
        _panel = new ResultPanel(_model, index.IconDirectory);
        _clock = new Label
        {
            ForeColor = Muted,
            BackColor = Surface,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false
        };
        _note = new Label
        {
            ForeColor = NoteColor,
            BackColor = Surface,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            Visible = false
        };
        Controls.Add(_panel);
        Controls.Add(_note);
        Controls.Add(_clock);
        Controls.Add(_input);
        _clock.MouseDown += OnDragHandleMouseDown;
        _note.MouseDown += OnDragHandleMouseDown;
        EnsureFonts();
        PlaceAtPreferredPosition();

        _translator.StartWarmup(Path.Combine(AppContext.BaseDirectory, "models", "enzh"));

        _tray = new NotifyIcon
        {
            Text = "QLaunch",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => Toggle();

        _input.MoveKey = Navigate;
        _input.Commit = Commit;
        _input.Cancelled = Hide;
        _input.TextChanged += OnTextChanged;
        _panel.Fire += OnFire;
        _panel.MenuRequested += OnMenuRequested;
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RunQueryAsync(_input.Text, _model.QueryId);
        };
        _flash.Tick += (_, _) =>
        {
            _flash.Stop();
            ShowNote(_model.Note);
        };
        _clockTimer.Tick += (_, _) => PaintClock();
        _blur.Tick += (_, _) => Begin();

        HandleCreated += (_, _) =>
        {
            Native.RoundCorners(Handle);
            if (!Native.RegisterHotKey(Handle, HotkeyId, Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE))
            {
                // 热键被别的启动器（如 ZTools）占用时，界面仍可用托盘图标唤出
                _hotkeyTaken = true;
                Debug.WriteLine("[hotkey] Alt+Space 注册失败，可能被其他程序占用");
            }
        };
        Deactivate += (_, _) => Begin();
        Shown += async (_, _) =>
        {
            Opacity = 0.995;
            if (_showOnStartup)
            {
                await SummonAsync(SelectionCapture.Start());
            }
            else
            {
                Hide();
            }
        };
    }

    protected override bool ShowWithoutActivation => true;

    private int S(float pixels) => (int)MathF.Round(pixels * Math.Max(96, DeviceDpi) / 96f);

    private void Begin()
    {
        // 右键菜单是独立弹出窗口，展开时不算丢失焦点；取词期间窗口本来就在用户那边，
        // 要等抢焦点这一步有结果之后再判断。
        BeginInvoke(new Action(() =>
        {
            if (!_awaitingFocus && _menu is not { Visible: true } && Native.GetForegroundWindow() != Handle)
            {
                Hide();
            }
        }));
    }

    /// <summary>
    /// 托盘图标。随程序一起发布的 app.ico 缺失或损坏时退回系统默认图标，不能让图标问题挡住启动。
    /// </summary>
    /// <returns>可用的图标。</returns>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "app.ico");
        try
        {
            return File.Exists(file) ? new System.Drawing.Icon(file) : System.Drawing.SystemIcons.Application;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine($"[tray] 图标加载失败: {e.Message}");
            return System.Drawing.SystemIcons.Application;
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        ContextMenuStrip menu = new();
        menu.Items.Add("显示搜索栏 (Alt+Space)", null, (_, _) => Toggle());
        menu.Items.Add("重建应用索引", null, (_, _) => RebuildIndex());
        ToolStripMenuItem startup = new("开机自启") { Checked = _settings.LaunchAtStartup };
        startup.Click += (_, _) =>
        {
            bool enabled = !startup.Checked;
            try
            {
                Startup.Register(enabled);
                _settings.LaunchAtStartup = enabled;
                startup.Checked = enabled;
                _settings.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                MessageBox.Show($"无法更改开机自启：{e.Message}", "QLaunch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        menu.Items.Add(startup);
        menu.Items.Add("-");
        menu.Items.Add("退出", null, (_, _) =>
        {
            _tray.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= (int)Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            Toggle();
            return;
        }
        // 顶栏留白当作标题栏：按住即可拖动窗口。输入框和结果区仍保持各自的鼠标行为。
        if (m.Msg == Native.WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)Native.HTCLIENT)
            {
                long packed = m.LParam.ToInt64();
                Point screen = new(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
                Point p = PointToClient(screen);
                if (p.Y >= 0 && p.Y < S(HeaderPx) && p.X >= 0 && p.X < Width)
                {
                    m.Result = (IntPtr)Native.HTCAPTION;
                }
            }
            return;
        }
        // 结果面板刻意不参与焦点，滚轮消息只会发给获得焦点的输入框，这里按指针位置转发
        if (m.Msg == WM_MOUSEWHEEL && _panel.ClientRectangle.Contains(_panel.PointToClient(MousePosition)))
        {
            _panel.ScrollBy(unchecked((short)((m.WParam.ToInt64() >> 16) & 0xFFFF)));
            return;
        }
        base.WndProc(ref m);
    }

    private void OnDragHandleMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Native.DragWindow(Handle);
        }
    }

    /// <summary>
    /// 唤出或收起搜索栏。取词必须在弹窗之前启动：Ctrl+C 与 WM_COPY 只能发给此刻仍持有焦点的
    /// 用户窗口，我们的窗口一旦拿到焦点就再也取不到选区。取词不阻塞弹窗，窗口立刻可见。
    /// </summary>
    /// <returns>无返回值。</returns>
    public void Toggle()
    {
        if (Visible)
        {
            Hide();
            return;
        }
        SelectionCapture capture = SelectionCapture.Start();
        PlaceAtPreferredPosition();
        _awaitingFocus = true;
        Show();
        _ = SummonAsync(capture);
    }

    private async Task SummonAsync(SelectionCapture capture)
    {
        _summonCancellation?.Cancel();
        _summonCancellation?.Dispose();
        _summonCancellation = new CancellationTokenSource();
        CancellationToken token = _summonCancellation.Token;
        try
        {
            _debounce.Stop();
            _model.SetNote(_hotkeyTaken ? "Alt+Space 已被其他程序占用，暂时只能用托盘图标唤出" : "");
            _model.Reset();
            // model.Query 已经是空串，这次赋值不会再触发一次查询
            _input.Text = "";
            ShowState();
            _ = RunQueryAsync("", _model.QueryId);
            string? selection;
            try
            {
                selection = await capture.FinishAsync(token);
            }
            finally
            {
                // 取词有了结果（或被中途打断），失焦判断从这一刻起才有效
                _awaitingFocus = false;
            }
            token.ThrowIfCancellationRequested();
            // 取词结束后才抢焦点，否则用户窗口已经拿不到 Ctrl+C
            Native.ForceForeground(Handle);
            _input.Focus();
            _input.SelectAll();
            if (selection is not null && Visible)
            {
                await PushSelectionAsync(selection, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (Visible)
        {
            PaintClock();
            _clockTimer.Start();
            _blur.Start();
        }
        else
        {
            _summonCancellation?.Cancel();
            _queryCancellation?.Cancel();
            _debounce.Stop();
            _clockTimer.Stop();
            _blur.Stop();
        }
        base.OnVisibleChanged(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_panel is not null)
        {
            Arrange();
        }
    }

    private async Task PushSelectionAsync(string selection, CancellationToken token)
    {
        TranslationRace race;
        try
        {
            race = await TranslateBothAsync(selection, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // 用户已经在输入框打字时不覆盖，取词结果让位给正在进行的搜索
        if (!Visible || _input.Text.Length > 0)
        {
            return;
        }
        _model.SetSelection(selection, race.Text ?? (race.Pending ? null : LauncherModel.EngineNotReadyText), race.Pending);
        if (selection.Length >= SelectionCapture.MaxChars)
        {
            _model.SetNote($"选中文本超过 {SelectionCapture.MaxChars} 字符，只翻译了前 {SelectionCapture.MaxChars} 字符");
        }
        ShowState();
        int id = _model.QueryId;
        if (race.Cloud is not null && await CloudTextAsync(race) is { } final && _model.FinishTranslation(id, final))
        {
            ShowState();
        }
    }

    /// <summary>
    /// 本地与云端同时开跑：本地通常先回来，先显示本地结果，云端完成后再覆盖；云端先回来就直接用云端。
    /// </summary>
    /// <param name="text">待翻译原文。</param>
    /// <param name="token">取消本次翻译的令牌。</param>
    /// <returns>首个可展示的文本、是否仍在等云端，以及待等待的云端任务。</returns>
    private async Task<TranslationRace> TranslateBothAsync(string text, CancellationToken token)
    {
        Task<TranslationOutcome>? cloud = _cloud.CanTranslate(text) ? _cloud.TranslateAsync(text, token) : null;
        TranslationOutcome local = await Task.Run(() => _translator.Translate(text), token);
        if (cloud is { IsCompletedSuccessfully: true } && cloud.Result.HasText)
        {
            return new TranslationRace(cloud.Result.Text, false, null, local.Text);
        }
        if (local.HasText || local.Status == TranslationStatus.Skipped)
        {
            return new TranslationRace(local.Text, cloud is not null, cloud, local.Text);
        }
        if (cloud is { IsCompletedSuccessfully: true })
        {
            return new TranslationRace(local.Text ?? cloud.Result.Text, false, null, local.Text);
        }
        return new TranslationRace(null, cloud is not null, cloud, local.Text);
    }

    /// <summary>等云端结果；失败时退回本地文本，保证等待提示不会永远挂着。</summary>
    private static async Task<string?> CloudTextAsync(TranslationRace race)
    {
        try
        {
            TranslationOutcome cloud = await race.Cloud!;
            return cloud.HasText ? cloud.Text : race.LocalText ?? "（翻译失败）";
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private readonly record struct TranslationRace(string? Text, bool Pending, Task<TranslationOutcome>? Cloud, string? LocalText);

    private void PlaceAtPreferredPosition()
    {
        Rectangle area = Screen.PrimaryScreen!.WorkingArea;
        int width = S(720);
        int height = S(460);
        Size = new Size(width, height);
        Location = new Point(area.Left + (area.Width - width) / 2, area.Top + area.Height / 5);
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        // 程序性的清空不算用户输入，不能再走一遍查询
        if (_model.Query == _input.Text)
        {
            return;
        }
        _debounce.Stop();
        _model.SetQuery(_input.Text);
        ShowState();
        _debounce.Start();
    }

    private bool Navigate(MoveDirection direction)
    {
        if (_model.Rows.Count == 0)
        {
            return false;
        }
        _model.Move(direction);
        _panel.SyncSelection();
        return true;
    }

    private bool Commit(bool reveal)
    {
        Result? row = _model.ActiveRow;
        if (row is null)
        {
            return false;
        }
        Activate(row, reveal);
        return true;
    }

    private void OnFire(int index, bool reveal)
    {
        Result row = _model.Rows[index];
        if (row.Kind == "trans")
        {
            // 左键点译文行只在原文与译文之间切换，不复制也不关闭窗口
            if (_model.ToggleTranslation())
            {
                ShowState();
            }
            return;
        }
        Activate(row, reveal);
    }

    private void Activate(Result row, bool reveal)
    {
        if (row.Kind == "trans")
        {
            if (!_model.HasTranslation)
            {
                Flash("正在翻译…");
                return;
            }
            CopyToClipboard(_model.TranslationText);
            Flash("已复制译文");
            return;
        }
        Launch(row.Path, row.Arguments, row.Kind, reveal);
        Hide();
    }

    private void OnMenuRequested(int index, Point location)
    {
        Result row = _model.Rows[index];
        ContextMenuStrip menu = new()
        {
            BackColor = MenuBack,
            ForeColor = Foreground,
            Font = _menuFont!,
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false
        };
        foreach (MenuAction action in _model.MenuFor(row))
        {
            MenuAction captured = action;
            ToolStripItem item = menu.Items.Add(captured.Label);
            item.ForeColor = Foreground;
            item.Click += (_, _) =>
            {
                switch (captured.Command)
                {
                    case MenuCommand.Copy:
                        CopyToClipboard(captured.CopyText ?? "");
                        Flash("已复制到剪贴板");
                        return;
                    case MenuCommand.Terminal:
                        OpenTerminal(row.Path);
                        break;
                    case MenuCommand.VsCode:
                        OpenEditor(row.Path);
                        break;
                    case MenuCommand.Reveal:
                        Launch(row.Path, row.Arguments, row.Kind, true);
                        break;
                    case MenuCommand.Remove:
                        RemoveFromFrequent(row);
                        return;
                    case MenuCommand.ToggleTranslation:
                        if (_model.ToggleTranslation())
                        {
                            ShowState();
                        }
                        return;
                    default:
                        Launch(row.Path, row.Arguments, row.Kind, false);
                        break;
                }
                Hide();
            };
        }
        _menu = menu;
        menu.Closed += (_, _) =>
        {
            _menu = null;
            // 关闭回调还在 ToolStripManager 的模态过滤栈上，此刻 Dispose 会让下一次鼠标按下
            // 访问已释放的菜单并直接终止进程，所以推到下一轮消息循环再释放。
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(menu.Dispose));
            }
            else
            {
                menu.Dispose();
            }
        };
        menu.Show(_panel, location);
    }

    private void ShowState()
    {
        EnsureFonts();
        _input.Placeholder = _model.Placeholder;
        ShowNote(_model.Note);
        Arrange();
        _panel.SyncContent();
    }

    private void ShowNote(string text)
    {
        _flash.Stop();
        _note.Text = text;
        _note.Visible = text.Length > 0;
    }

    private void Flash(string message)
    {
        _note.Text = message;
        _note.Visible = true;
        Arrange();
        _flash.Start();
    }

    /// <summary>输入框、时钟、提示条与结果区按固定像素高度手工排布，省掉容器嵌套。</summary>
    private void Arrange()
    {
        int header = S(HeaderPx);
        int pad = S(16);
        int clockWidth = S(46);
        int inputHeight = S(24);
        _input.SetBounds(pad, (header - inputHeight) / 2, Width - pad * 2 - clockWidth - S(10), inputHeight);
        _input.Padding = new Padding(S(1), 0, 0, 0);
        _clock.SetBounds(Width - pad - clockWidth, (header - S(18)) / 2, clockWidth, S(18));
        int noteHeight = _note.Visible ? S(NotePx) : 0;
        _note.SetBounds(pad, header, Width - pad * 2, noteHeight);
        _panel.SetBounds(0, header + noteHeight, Width, Math.Max(0, Height - header - noteHeight));
    }

    private void PaintClock() => _clock.Text = DateTime.Now.ToString("HH:mm");

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        EnsureFonts();
        e.Graphics.FillRectangle(DividerBrush, 0, S(HeaderPx) - 1, Width, 1);
    }

    private static readonly SolidBrush DividerBrush = new(Divider);

    /// <returns>true 表示字体因 DPI 变化被重建。</returns>
    private bool EnsureFonts()
    {
        if (_inputFont is not null && _dpi == DeviceDpi)
        {
            return false;
        }
        _dpi = DeviceDpi;
        // 按像素定字号，DPI 变化时整批重建，GDI 不会再缩放一次
        _inputFont = PixelFont(17);
        _clockFont = PixelFont(12);
        _noteFont = PixelFont(12);
        _menuFont = PixelFont(12);
        _input.Font = _inputFont;
        _clock.Font = _clockFont;
        _note.Font = _noteFont;
        return true;
    }

    private static Font PixelFont(float pixels) => new("Microsoft YaHei UI", pixels, FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>
    /// 查询一次：空输入回常用应用网格，有输入则应用打分加 Everything 文件，英文句子另附翻译。
    /// </summary>
    /// <param name="text">输入框原文。</param>
    /// <param name="id">本次查询序号，回填时用于丢弃过期响应。</param>
    /// <returns>无返回值。</returns>
    private async Task RunQueryAsync(string text, int id)
    {
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = new CancellationTokenSource();
        CancellationToken token = _queryCancellation.Token;

        List<Result> results = [];
        string q = Matcher.NormalizeQuery(text);
        string prose = text.Trim();
        bool translate = q.Length > 0 && LooksLikeProse(prose);
        Task<TranslationRace>? raceTask = translate ? TranslateBothAsync(prose, token) : null;
        string? error = null;
        if (q.Length == 0)
        {
            results.AddRange(_index.FrequentApps(_settings, LauncherModel.GridColumns * 3).Select(app => new Result
            {
                Kind = "app",
                Title = app.Name,
                Subtitle = app.Path,
                Icon = app.Icon,
                Path = app.Path,
                Arguments = app.Arguments
            }));
        }
        else
        {
            foreach (AppEntry app in _index.Entries)
            {
                double score = Matcher.ScoreApp(q, app);
                if (score > 0)
                {
                    results.Add(new Result
                    {
                        Kind = "app",
                        Title = app.Name,
                        Subtitle = app.Path,
                        Icon = app.Icon,
                        Path = app.Path,
                        Arguments = app.Arguments,
                        Score = score
                    });
                }
            }
            results = [.. results.OrderByDescending(r => r.Score).Take(_settings.MaxResults)];

            List<Result> files = await Task.Run(() =>
            {
                // 用户已经继续输入时不必再查再排；最终是否采用仍由 ApplyResults 的序号判断
                if (_model.QueryId != id)
                {
                    return [];
                }
                List<Result> pool = _everything.Query(q, FileRanker.CandidatePool, out string? queryError);
                error = queryError;
                return _model.QueryId == id
                    ? FileRanker.Rank(pool, q, _fileUsage, _settings.FileResults)
                    : [];
            });
            results.AddRange(files);
        }

        TranslationRace race = default;
        if (raceTask is not null)
        {
            try
            {
                race = await raceTask;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (!_model.ApplyResults(id, results, race.Text, error, race.Pending))
        {
            return;
        }
        ShowState();
        if (race.Cloud is not null && await CloudTextAsync(race) is { } final && _model.FinishTranslation(id, final))
        {
            ShowState();
        }
    }

    private static bool LooksLikeProse(string q) =>
        q.Length >= 4 && q.Contains(' ') && q.All(c => c < 0x2E80);

    /// <summary>把右键菜单或译文行选中的文本写入剪贴板；写入失败只记录，不打断界面。</summary>
    /// <param name="text">要复制的文本。</param>
    /// <returns>无返回值。</returns>
    private static void CopyToClipboard(string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Debug.WriteLine($"[copy] 写入剪贴板失败: {e.Message}");
        }
    }

    /// <summary>在标准安装目录和 PATH 里的 code.cmd 旁查找 VS Code 主程序。</summary>
    /// <returns>Code.exe 的完整路径；未安装时为 null。</returns>
    private static string? FindVsCode()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] known =
        [
            Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe")
        ];
        foreach (string candidate in known)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        string[] entries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string entry in entries)
        {
            try
            {
                string dir = entry.Trim('"');
                if (!File.Exists(Path.Combine(dir, "code.cmd")))
                {
                    continue;
                }
                string exe = Path.GetFullPath(Path.Combine(dir, "..", "Code.exe"));
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// 在文件夹打开终端：优先 Windows Terminal，未安装时退回 PowerShell。
    /// 路径末尾的反斜杠要先去掉，否则命令行解析会把结尾的 \" 当成转义引号。
    /// </summary>
    /// <param name="folder">目标文件夹完整路径。</param>
    /// <returns>无返回值。</returns>
    private static void OpenTerminal(string folder)
    {
        string target = folder.TrimEnd('\\');
        if (target.EndsWith(':'))
        {
            target += "\\";
        }
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        try
        {
            ProcessStartInfo info = File.Exists(wt)
                ? new ProcessStartInfo(wt) { UseShellExecute = true, Arguments = $"-d \"{target}\"" }
                : new ProcessStartInfo("powershell.exe") { UseShellExecute = true, Arguments = "-NoExit", WorkingDirectory = target };
            Process.Start(info);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[terminal] {folder} 失败: {e.Message}");
        }
    }

    private void OpenEditor(string folder)
    {
        if (_vsCode is null)
        {
            return;
        }
        string target = folder.TrimEnd('\\');
        if (target.EndsWith(':'))
        {
            target += "\\";
        }
        try
        {
            Process.Start(new ProcessStartInfo(_vsCode) { UseShellExecute = true, Arguments = $"\"{target}\"" });
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[vscode] {folder} 失败: {e.Message}");
        }
    }

    private void Launch(string path, string args, string kind, bool reveal)
    {
        if (path.Length == 0)
        {
            return;
        }
        try
        {
            if (reveal)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }
            ProcessStartInfo info = new(path)
            {
                UseShellExecute = true,
                Arguments = args
            };
            if (path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            {
                // AppsFolder 命名空间只有 explorer 能解析
                info.FileName = "explorer.exe";
                info.Arguments = $"\"{path}\"";
            }
            Process.Start(info);
            if (kind == "file" && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // 从文件搜索启动的 exe 也当成应用记住，下次空输入就能在常用里直接点
                AppEntry portable = _index.RegisterPortable(path);
                _settings.RecordLaunch(portable.Path, portable.Arguments);
            }
            else if (kind is "file" or "folder")
            {
                _fileUsage.Record(path, kind == "folder");
            }
            else if (kind == "app" && _index.Entries.Any(app =>
                app.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && app.Arguments == args))
            {
                _settings.RecordLaunch(path, args);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[launch] {path} 失败: {e.Message}");
        }
    }

    /// <summary>
    /// 从常用应用移除：只清启动计数，不记"已移除"，下次启动会重新出现；
    /// 便携 exe 同时删掉索引条目，下次从文件搜索启动时重新登记。
    /// </summary>
    /// <param name="row">被移除的应用行。</param>
    /// <returns>无返回值。</returns>
    private void RemoveFromFrequent(Result row)
    {
        _settings.RemoveLaunch(row.Path, row.Arguments);
        _index.ForgetPortable(row.Path, row.Arguments);
        // 空查询走同步分支，刷新后列表立刻少掉这一项，窗口保持打开
        _ = RunQueryAsync("", _model.QueryId);
    }

    private void RebuildIndex()
    {
        _index.Load(_settings.DataDir, true);
        _model.Reset();
        ShowState();
        _ = RunQueryAsync("", _model.QueryId);
    }

    protected override void OnFormClosed(System.Windows.Forms.FormClosedEventArgs e)
    {
        _summonCancellation?.Cancel();
        _summonCancellation?.Dispose();
        _summonCancellation = null;
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = null;
        _debounce.Dispose();
        _flash.Dispose();
        _clockTimer.Dispose();
        _blur.Dispose();
        _menu?.Dispose();
        _tray.Dispose();
        _translator.Dispose();
        _everything.Reset();
        _inputFont?.Dispose();
        _clockFont?.Dispose();
        _noteFont?.Dispose();
        _menuFont?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>结果右键菜单的深色配色；系统渲染器的默认渐变在深色背景上会留白边。</summary>
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => MenuLine;

        public override Color MenuItemBorder => Accent;

        public override Color MenuItemSelected => MenuPick;

        public override Color MenuItemSelectedGradientBegin => MenuPick;

        public override Color MenuItemSelectedGradientEnd => MenuPick;

        public override Color MenuStripGradientBegin => MenuBack;

        public override Color MenuStripGradientEnd => MenuBack;

        public override Color ToolStripDropDownBackground => MenuBack;

        public override Color ImageMarginGradientBegin => MenuBack;

        public override Color ImageMarginGradientMiddle => MenuBack;

        public override Color ImageMarginGradientEnd => MenuBack;

        public override Color SeparatorDark => MenuLine;

        public override Color SeparatorLight => MenuLine;
    }
}
