namespace QLaunch;

/// <summary>
/// 翻译设置对话框：选翻译方式（本地 Bergamot 或某个云端服务商）、填对应凭据、
/// 本地方式下可勾预加载。保存只回填字段，落盘与重建门面由调用方完成。
/// </summary>
internal sealed class TranslationSettingsForm : Form
{
    private readonly ComboBox _provider = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 300
    };

    private readonly CheckBox _preload = new() { Text = "启动时预加载本地模型", AutoSize = true };
    private readonly Label _modelStatus = new() { AutoSize = true };
    private readonly Label _providerLabel = new() { Text = "翻译方式", AutoSize = true };

    private readonly TextBox _baiduAppId = new() { PlaceholderText = "百度翻译开放平台的 APP ID" };
    private readonly TextBox _baiduKey = new() { UseSystemPasswordChar = true, PlaceholderText = "百度翻译开放平台的密钥" };
    private readonly TextBox _microsoftKey = new() { UseSystemPasswordChar = true, PlaceholderText = "Azure Translator 订阅密钥" };
    private readonly TextBox _microsoftRegion = new() { PlaceholderText = "可选，多区域资源填例如 eastasia" };
    private readonly TextBox _microsoftEndpoint = new() { PlaceholderText = "可选，默认 https://api.cognitive.microsofttranslator.com" };
    private readonly TextBox _deeplKey = new() { UseSystemPasswordChar = true, PlaceholderText = "DeepL API 密钥，Free 版以 :fx 结尾" };
    private readonly TextBox _deeplEndpoint = new() { PlaceholderText = "可选，默认按密钥自动选 Free / Pro 端点" };

    private readonly GroupBox _localBox = new() { Text = "本地模型" };
    private readonly GroupBox _baiduBox = new() { Text = "百度翻译开放平台" };
    private readonly GroupBox _microsoftBox = new() { Text = "微软 Translator（Azure）" };
    private readonly GroupBox _deeplBox = new() { Text = "DeepL" };
    private readonly Button _save = new() { Text = "保存", AutoSize = true };
    private readonly Button _cancel = new() { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };

    private readonly bool _initialPreload;

    public TranslationSettingsForm(CloudCredentials credentials, bool preload, string modelStatus)
    {
        Credentials = credentials;
        _initialPreload = preload;
        Text = "翻译设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 320);

        _provider.Items.AddRange(["本地 Bergamot", "百度", "微软", "DeepL"]);
        _provider.SelectedIndex = credentials.Provider.Trim().ToLowerInvariant() switch
        {
            "baidu" => 1,
            "microsoft" => 2,
            "deepl" => 3,
            _ => 0
        };
        _baiduAppId.Text = credentials.BaiduAppId;
        _baiduKey.Text = credentials.BaiduKey;
        _microsoftKey.Text = credentials.MicrosoftKey;
        _microsoftRegion.Text = credentials.MicrosoftRegion;
        _microsoftEndpoint.Text = credentials.MicrosoftEndpoint;
        _deeplKey.Text = credentials.DeepLKey;
        _deeplEndpoint.Text = credentials.DeepLEndpoint;
        _preload.Checked = preload;
        _modelStatus.Text = "当前状态：" + modelStatus;

        Controls.Add(_providerLabel);
        Controls.Add(_provider);
        Controls.Add(_localBox);
        Controls.Add(_baiduBox);
        Controls.Add(_microsoftBox);
        Controls.Add(_deeplBox);
        Controls.Add(_save);
        Controls.Add(_cancel);
        _localBox.Controls.Add(_preload);
        _localBox.Controls.Add(_modelStatus);
        _preload.Location = new Point(14, 24);
        _modelStatus.Location = new Point(14, 52);
        Fill(_baiduBox, ("APP ID", _baiduAppId), ("密钥", _baiduKey));
        Fill(_microsoftBox, ("密钥", _microsoftKey), ("区域", _microsoftRegion), ("Endpoint", _microsoftEndpoint));
        Fill(_deeplBox, ("密钥", _deeplKey), ("Endpoint", _deeplEndpoint));

        _provider.SelectedIndexChanged += (_, _) => LayoutBoxes();
        _save.Click += (_, _) => Commit();
        AcceptButton = _save;
        CancelButton = _cancel;
        LayoutBoxes();
    }

    /// <summary>编辑中的凭据；对话框返回 OK 时调用方可直接保存。</summary>
    public CloudCredentials Credentials { get; }

    /// <summary>本地方式下的预加载偏好；云端方式保持原值。</summary>
    public bool PreloadLocalModel { get; private set; }

    private static void Fill(GroupBox box, params (string Text, TextBox Field)[] rows)
    {
        TableLayoutPanel table = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(10, 8, 10, 8)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rows.Length; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows.Length));
            table.Controls.Add(new Label { Text = rows[i].Text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, i);
            rows[i].Field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            table.Controls.Add(rows[i].Field, 1, i);
        }
        box.Controls.Add(table);
    }

    private void LayoutBoxes()
    {
        int index = _provider.SelectedIndex;
        _localBox.Visible = index == 0;
        _baiduBox.Visible = index == 1;
        _microsoftBox.Visible = index == 2;
        _deeplBox.Visible = index == 3;

        int top = 56;
        _providerLabel.Location = new Point(16, 18);
        _provider.Location = new Point(80, 14);
        _provider.Width = ClientSize.Width - 96;
        // 按索引挑分组，不能用 Visible 回读：构造时父窗体还没显示，Visible 恒为 false。
        (GroupBox box, int height) = index switch
        {
            0 => (_localBox, 88),
            2 => (_microsoftBox, 140),
            3 => (_deeplBox, 104),
            _ => (_baiduBox, 104)
        };
        box.SetBounds(16, top, ClientSize.Width - 32, height);
        top += height + 12;
        ClientSize = new Size(ClientSize.Width, top + 48);
        _save.Location = new Point(ClientSize.Width - 16 - _save.Width - 8 - _cancel.Width,
            ClientSize.Height - 14 - _save.Height);
        _cancel.Location = new Point(ClientSize.Width - 16 - _cancel.Width, _save.Top);
    }

    private void Commit()
    {
        (string provider, bool local) = _provider.SelectedIndex switch
        {
            1 => ("baidu", false),
            2 => ("microsoft", false),
            3 => ("deepl", false),
            _ => ("local", true)
        };
        string appId = _baiduAppId.Text.Trim();
        string baiduKey = _baiduKey.Text.Trim();
        string microsoftKey = _microsoftKey.Text.Trim();
        string deeplKey = _deeplKey.Text.Trim();
        if (provider == "baidu" && (appId.Length == 0 || baiduKey.Length == 0))
        {
            Warn("填完百度的 APP ID 和密钥，或改用本地 Bergamot。");
            return;
        }
        if (provider == "microsoft" && microsoftKey.Length == 0)
        {
            Warn("微软服务商需要订阅密钥，或改用本地 Bergamot。");
            return;
        }
        if (provider == "deepl" && deeplKey.Length == 0)
        {
            Warn("DeepL 服务商需要 API 密钥，或改用本地 Bergamot。");
            return;
        }

        Credentials.Provider = provider;
        Credentials.BaiduAppId = appId;
        Credentials.BaiduKey = baiduKey;
        Credentials.MicrosoftKey = microsoftKey;
        Credentials.MicrosoftRegion = _microsoftRegion.Text.Trim();
        Credentials.MicrosoftEndpoint = _microsoftEndpoint.Text.Trim();
        Credentials.DeepLKey = deeplKey;
        Credentials.DeepLEndpoint = _deeplEndpoint.Text.Trim();
        PreloadLocalModel = local ? _preload.Checked : _initialPreload;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "翻译设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
