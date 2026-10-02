using System;
using System.Drawing;
using System.Windows.Forms;

namespace WhisperClock
{
    /// <summary>单个闹钟的设置界面（新建或编辑共用）。</summary>
    public class AlarmEditForm : Form
    {
        private readonly AppSettings _settings;
        private readonly Alarm? _source;

        private DateTimePicker _dtpTime = null!;
        private NumericUpDown _numRandomOffset = null!;
        private TextBox _txtTitle = null!;
        private TextBox _txtSubtitle = null!;
        private TextBox _txtSnoozeTitle = null!;
        private TextBox _txtSnoozeSubtitle = null!;
        private TextBox _txtAudio = null!;
        private TextBox _txtOpenTarget = null!;
        private RadioButton _rbOnce = null!;
        private RadioButton _rbLoop = null!;
        private CheckBox _chkOneShot = null!;
        private ComboBox _cmbMode = null!;
        private NumericUpDown _numWait = null!;
        private ComboBox _cmbWaitUnit = null!;
        private CheckBox _chkTriggerAtLogin = null!;
        private CheckBox _chkEnabled = null!;

        /// <summary>载入数据期间为 true：此时 RadioButton 赋值触发的 CheckedChanged 不算“用户切换播放模式”。</summary>
        private bool _loadingValues;

        /// <summary>对话框确定后保存/新建的闹钟。</summary>
        public Alarm Alarm { get; private set; } = null!;

        public AlarmEditForm(AppSettings settings, Alarm? source)
        {
            _settings = settings;
            _source = source;

            Text = source == null ? "新建闹钟" : "编辑闹钟";
            ClientSize = new Size(430, 524);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            BuildUi();
            LoadValues();

            Load += (_, _) =>
            {
                ThemeManager.Apply(this);
                Icon = AppIcon.Load(ThemeManager.IsDark, SystemInformation.IconSize); // 标题栏图标跟随主题
            };
            FormClosed += (_, _) => Icon?.Dispose();
        }

        private void BuildUi()
        {
            var lblTime = new Label { Text = "闹钟时间", Location = new Point(16, 22), AutoSize = true };
            _dtpTime = new DateTimePicker
            {
                Location = new Point(110, 18),
                Size = new Size(200, 25),
                Format = DateTimePickerFormat.Time,
                ShowUpDown = true
            };

            var lblRandom = new Label { Text = "随机 ±(秒)", Location = new Point(16, 54), AutoSize = true };
            _numRandomOffset = new NumericUpDown
            {
                Location = new Point(110, 50),
                Size = new Size(90, 23),
                Minimum = 0,
                Maximum = 3600,
                Increment = 10,
                Value = 0
            };
            var lblRandomHint = new Label { Text = "0 = 到点即响；>0 = 提前/延后最多该秒数", Location = new Point(208, 54), AutoSize = true };

            var lblTitle = new Label { Text = "主标题", Location = new Point(16, 86), AutoSize = true };
            _txtTitle = new TextBox { Location = new Point(110, 82), Size = new Size(290, 23) };

            var lblSubtitle = new Label { Text = "副标题", Location = new Point(16, 118), AutoSize = true };
            _txtSubtitle = new TextBox { Location = new Point(110, 114), Size = new Size(290, 23) };

            var lblAudio = new Label { Text = "音频文件", Location = new Point(16, 156), AutoSize = true };
            _txtAudio = new TextBox { Location = new Point(110, 152), Size = new Size(128, 23), ReadOnly = true };
            var btnBrowseFile = new Button { Text = "文件…", Location = new Point(244, 150), Size = new Size(56, 26) };
            btnBrowseFile.Click += (_, _) => BrowseAudioFile();
            var btnBrowseFolder = new Button { Text = "文件夹…", Location = new Point(304, 150), Size = new Size(64, 26) };
            btnBrowseFolder.Click += (_, _) => BrowseAudioFolder();
            // 清除：文本框只读，原来只能选不能清，设了铃声就没法改回“不响铃、只发通知”。
            var btnClearAudio = new Button { Text = "清除", Location = new Point(372, 150), Size = new Size(54, 26) };
            btnClearAudio.Click += (_, _) => _txtAudio.Text = "";

            var lblOpen = new Label { Text = "打开目标", Location = new Point(16, 188), AutoSize = true };
            _txtOpenTarget = new TextBox { Location = new Point(110, 184), Size = new Size(200, 23) };
            var btnBrowseOpen = new Button { Text = "浏览…", Location = new Point(318, 182), Size = new Size(82, 26) };
            btnBrowseOpen.Click += (_, _) => BrowseOpenTarget();

            var lblSnoozeTitle = new Label { Text = "贪睡主标题", Location = new Point(16, 222), AutoSize = true };
            _txtSnoozeTitle = new TextBox { Location = new Point(110, 218), Size = new Size(290, 23) };

            var lblSnoozeSubtitle = new Label { Text = "贪睡副标题", Location = new Point(16, 254), AutoSize = true };
            _txtSnoozeSubtitle = new TextBox { Location = new Point(110, 250), Size = new Size(290, 23) };

            // 闹钟模式：普通 / 仅通知 / 纯提醒（beta）。下拉选择式（点击展开）。
            var lblAlarmMode = new Label { Text = "闹钟模式", Location = new Point(16, 286), AutoSize = true };
            _cmbMode = new ComboBox
            {
                Location = new Point(110, 282),
                Size = new Size(160, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbMode.Items.AddRange(new object[] { "普通模式", "仅通知", "纯提醒（beta）" });
            _cmbMode.SelectedIndex = 0; // 默认普通模式

            var lblMode = new Label { Text = "播放模式", Location = new Point(16, 318), AutoSize = true };
            _rbOnce = new RadioButton { Text = "单次播放", Location = new Point(110, 314), AutoSize = true, Checked = true };
            _rbLoop = new RadioButton { Text = "循环播放", Location = new Point(212, 314), AutoSize = true };

            // 响铃等待时间：音频开始播放后等这么久就停止响铃（不再写死）。单位可选秒/分钟，
            // 仅“仅通知 / 纯提醒”模式使用；普通模式走确认期 + 自动贪睡，不看这个值。
            var lblWait = new Label { Text = "响铃等待", Location = new Point(16, 350), AutoSize = true };
            _numWait = new NumericUpDown { Location = new Point(110, 346), Size = new Size(70, 23) };
            _cmbWaitUnit = new ComboBox { Location = new Point(186, 346), Size = new Size(62, 25) };
            WaitTimeInput.Bind(_numWait, _cmbWaitUnit);
            var lblWaitHint = new Label { Text = "仅通知/纯提醒有效", Location = new Point(256, 350), AutoSize = true };

            _chkOneShot = new CheckBox { Text = "单次闹钟", Location = new Point(110, 382), AutoSize = true };
            // 登录时触发：程序启动（开机自启）即触发一次，不按设定时间；勾选后时间/随机设置无效。
            _chkTriggerAtLogin = new CheckBox { Text = "登录时触发（不按时间，启动即触发）", Location = new Point(110, 414), AutoSize = true };
            _chkTriggerAtLogin.CheckedChanged += (_, _) => UpdateTimeControlsEnabled();
            _chkEnabled = new CheckBox { Text = "启用此闹钟", Location = new Point(110, 446), AutoSize = true, Checked = true };

            var btnOk = new Button { Text = "确定", Location = new Point(110, 488), Size = new Size(90, 30), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Location = new Point(212, 488), Size = new Size(90, 30), DialogResult = DialogResult.Cancel };
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            btnOk.Click += (_, _) => Save();

            Controls.AddRange(new Control[]
            {
                lblTime, _dtpTime,
                lblRandom, _numRandomOffset, lblRandomHint,
                lblTitle, _txtTitle,
                lblSubtitle, _txtSubtitle,
                lblAudio, _txtAudio, btnBrowseFile, btnBrowseFolder, btnClearAudio,
                lblOpen, _txtOpenTarget, btnBrowseOpen,
                lblSnoozeTitle, _txtSnoozeTitle,
                lblSnoozeSubtitle, _txtSnoozeSubtitle,
                lblAlarmMode, _cmbMode,
                lblMode, _rbOnce, _rbLoop,
                lblWait, _numWait, _cmbWaitUnit, lblWaitHint,
                _chkOneShot, _chkTriggerAtLogin, _chkEnabled,
                btnOk, btnCancel
            });

            // 播放模式切换 → 若等待时间没被手动改过，就带出该模式的默认值。挂在这里是为了确保
            // _numWait / _cmbWaitUnit 都已创建（BuildUi 内更早的位置触发 CheckedChanged 会踩空）。
            _rbOnce.CheckedChanged += (_, _) => SyncWaitOnPlayModeChange();
            _rbLoop.CheckedChanged += (_, _) => SyncWaitOnPlayModeChange();
        }

        private void LoadValues()
        {
            _loadingValues = true; // 载入期间的 RadioButton 赋值不算“用户切换播放模式”
            try
            {
                if (_source != null)
                {
                    _dtpTime.Value = DateTime.Today.Add(_source.TimeOfDay);
                    _numRandomOffset.Value = Math.Clamp(_source.RandomOffsetSeconds, 0, 3600);
                    _txtTitle.Text = _source.Title;
                    _txtSubtitle.Text = _source.Subtitle;
                    _txtAudio.Text = _source.AudioPath ?? "";
                    _txtOpenTarget.Text = _source.OpenTarget ?? "";
                    _txtSnoozeTitle.Text = _source.SnoozeTitle ?? "";
                    _txtSnoozeSubtitle.Text = _source.SnoozeSubtitle ?? "";
                    _rbLoop.Checked = _source.Loop;
                    _rbOnce.Checked = !_source.Loop;
                    _chkOneShot.Checked = _source.OneShot;
                    _cmbMode.SelectedIndex = Math.Clamp((int)_source.Mode, 0, 2);
                    _chkTriggerAtLogin.Checked = _source.TriggerAtLogin;
                    _chkEnabled.Checked = _source.Enabled;

                    // 响铃等待：闹钟自身值优先；旧数据（<= 0，此前没有该字段）回退到该播放模式的默认值。
                    double wait = _source.PlayWaitSeconds > 0
                        ? _source.PlayWaitSeconds
                        : WaitDefaultSeconds(_source.Loop);
                    WaitTimeInput.SetSeconds(_numWait, _cmbWaitUnit, wait);
                }
                else
                {
                    // 新建闹钟：使用可自定义的默认模板；时间自动设为当前时间（整分），用户可改。
                    var now = DateTime.Now;
                    _dtpTime.Value = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
                    _numRandomOffset.Value = 0;
                    _txtTitle.Text = _settings.DefaultTitle;
                    _txtSubtitle.Text = _settings.DefaultSubtitle;
                    _txtSnoozeTitle.Text = _settings.DefaultSnoozeTitle; // 预填模板值，可改；清空则回退模板
                    _txtSnoozeSubtitle.Text = _settings.DefaultSnoozeSubtitle;
                    _rbLoop.Checked = _settings.DefaultLoop;
                    _rbOnce.Checked = !_settings.DefaultLoop;
                    _chkOneShot.Checked = _settings.DefaultOneShot;
                    WaitTimeInput.SetSeconds(_numWait, _cmbWaitUnit, WaitDefaultSeconds(_settings.DefaultLoop));
                }
            }
            finally
            {
                _loadingValues = false;
            }
        }

        private void Save()
        {
            Alarm = _source ?? new Alarm();
            Alarm.TimeOfDay = _dtpTime.Value.TimeOfDay;
            Alarm.RandomOffsetSeconds = (int)_numRandomOffset.Value;
            Alarm.Title = string.IsNullOrWhiteSpace(_txtTitle.Text) ? "⏰ 闹钟时间到！" : _txtTitle.Text.Trim();
            Alarm.Subtitle = _txtSubtitle.Text.Trim();
            Alarm.SnoozeTitle = string.IsNullOrWhiteSpace(_txtSnoozeTitle.Text) ? null : _txtSnoozeTitle.Text.Trim();
            Alarm.SnoozeSubtitle = string.IsNullOrWhiteSpace(_txtSnoozeSubtitle.Text) ? null : _txtSnoozeSubtitle.Text.Trim();
            Alarm.AudioPath = string.IsNullOrWhiteSpace(_txtAudio.Text) ? null : _txtAudio.Text.Trim();
            Alarm.OpenTarget = string.IsNullOrWhiteSpace(_txtOpenTarget.Text) ? null : _txtOpenTarget.Text.Trim();
            Alarm.Loop = _rbLoop.Checked;
            Alarm.PlayWaitSeconds = WaitTimeInput.GetSeconds(_numWait, _cmbWaitUnit);
            Alarm.OneShot = _chkOneShot.Checked;
            Alarm.Mode = (AlarmMode)_cmbMode.SelectedIndex;
            Alarm.RemindOnly = Alarm.Mode == AlarmMode.RemindOnly; // 同步旧字段，兼容旧版读取
            Alarm.TriggerAtLogin = _chkTriggerAtLogin.Checked;
            Alarm.Enabled = _chkEnabled.Checked;
        }

        /// <summary>“登录时触发”勾选后，时间/随机设置无意义，禁用对应控件。</summary>
        private void UpdateTimeControlsEnabled()
        {
            bool login = _chkTriggerAtLogin.Checked;
            _dtpTime.Enabled = !login;
            _numRandomOffset.Enabled = !login;
        }

        /// <summary>“默认模板…”里按播放模式给出的默认响铃等待时间（秒）。</summary>
        private double WaitDefaultSeconds(bool loop)
        {
            double value = loop ? _settings.DefaultPlayWaitSecondsLoop : _settings.DefaultPlayWaitSecondsOnce;
            return value > 0 ? value : loop ? 300 : 8;
        }

        /// <summary>
        /// 播放模式切换时的联动：如果当前等待时间仍等于“原播放模式的默认值”（说明用户没手动改过），
        /// 就带出新模式的默认值（单次播放 8 秒 / 循环播放 5 分钟）；已手动改过则保留用户的值。
        /// </summary>
        private void SyncWaitOnPlayModeChange()
        {
            if (_loadingValues)
                return;

            // 取消选中的那个单选按钮也会回调一次，此时两个都没选中，直接跳过。
            if (!_rbLoop.Checked && !_rbOnce.Checked)
                return;

            bool loop = _rbLoop.Checked;
            double previousDefault = WaitDefaultSeconds(!loop); // 切换前那个模式的默认值
            double current = WaitTimeInput.GetSeconds(_numWait, _cmbWaitUnit);
            if (Math.Abs(current - previousDefault) < 0.001)
                WaitTimeInput.SetSeconds(_numWait, _cmbWaitUnit, WaitDefaultSeconds(loop));
        }

        private void BrowseAudioFile()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择闹钟铃声（.wav）",
                Filter = "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                _txtAudio.Text = dialog.FileName;
        }

        private void BrowseAudioFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择铃声文件夹（触发时随机选其中 1 个 .wav 播放，含嵌套子文件夹）",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                _txtAudio.Text = dialog.SelectedPath;
        }

        private void BrowseOpenTarget()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择“打开”目标文件",
                Filter = "所有文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                _txtOpenTarget.Text = dialog.FileName;
        }
    }
}
