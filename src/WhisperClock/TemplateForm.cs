using System;
using System.Drawing;
using System.Windows.Forms;

namespace WhisperClock
{
    /// <summary>默认模板设置：新建闹钟时预填的默认值，可一键恢复最初默认。</summary>
    public class TemplateForm : Form
    {
        private readonly AppSettings _settings;

        private TextBox _txtTitle = null!;
        private TextBox _txtSubtitle = null!;
        private TextBox _txtSnoozeTitle = null!;
        private TextBox _txtSnoozeSubtitle = null!;
        private TextBox _txtAutoSnoozeTitle = null!;
        private TextBox _txtAutoSnoozeSubtitle = null!;
        private TextBox _txtOpenButton = null!;
        private TextBox _txtSnoozeButton = null!;
        private TextBox _txtDismissButton = null!;
        private RadioButton _rbOnce = null!;
        private RadioButton _rbLoop = null!;
        private CheckBox _chkOneShot = null!;
        private NumericUpDown _numWaitOnce = null!;
        private ComboBox _cmbWaitOnceUnit = null!;
        private NumericUpDown _numWaitLoop = null!;
        private ComboBox _cmbWaitLoopUnit = null!;

        public TemplateForm(AppSettings settings)
        {
            _settings = settings;

            Text = "默认闹钟模板";
            ClientSize = new Size(430, 510);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            BuildUi();
            LoadFromSettings();

            Load += (_, _) =>
            {
                ThemeManager.Apply(this);
                Icon = AppIcon.Load(ThemeManager.IsDark, SystemInformation.IconSize); // 标题栏图标跟随主题
            };
            FormClosed += (_, _) => Icon?.Dispose();
        }

        private void BuildUi()
        {
            var lblTitle = new Label { Text = "默认主标题", Location = new Point(16, 20), AutoSize = true };
            _txtTitle = new TextBox { Location = new Point(110, 16), Size = new Size(290, 23) };

            var lblSubtitle = new Label { Text = "默认副标题", Location = new Point(16, 54), AutoSize = true };
            _txtSubtitle = new TextBox { Location = new Point(110, 50), Size = new Size(290, 23) };

            var lblSnoozeTitle = new Label { Text = "贪睡主标题", Location = new Point(16, 88), AutoSize = true };
            _txtSnoozeTitle = new TextBox { Location = new Point(110, 84), Size = new Size(290, 23) };

            var lblSnoozeSubtitle = new Label { Text = "贪睡副标题", Location = new Point(16, 122), AutoSize = true };
            _txtSnoozeSubtitle = new TextBox { Location = new Point(110, 118), Size = new Size(290, 23) };

            var lblAutoSnoozeTitle = new Label { Text = "自动贪睡主标题", Location = new Point(16, 156), AutoSize = true };
            _txtAutoSnoozeTitle = new TextBox { Location = new Point(110, 152), Size = new Size(290, 23) };

            var lblAutoSnoozeSubtitle = new Label { Text = "自动贪睡副标题", Location = new Point(16, 190), AutoSize = true };
            _txtAutoSnoozeSubtitle = new TextBox { Location = new Point(110, 186), Size = new Size(290, 23) };

            var lblOpenButton = new Label { Text = "打开按钮文字", Location = new Point(16, 224), AutoSize = true };
            _txtOpenButton = new TextBox { Location = new Point(110, 220), Size = new Size(290, 23) };

            var lblSnoozeButton = new Label { Text = "延迟按钮文字", Location = new Point(16, 258), AutoSize = true };
            _txtSnoozeButton = new TextBox { Location = new Point(110, 254), Size = new Size(290, 23) };

            var lblDismissButton = new Label { Text = "结束按钮文字", Location = new Point(16, 292), AutoSize = true };
            _txtDismissButton = new TextBox { Location = new Point(110, 288), Size = new Size(290, 23) };

            var lblMode = new Label { Text = "播放模式", Location = new Point(16, 326), AutoSize = true };
            _rbOnce = new RadioButton { Text = "单次播放", Location = new Point(110, 322), AutoSize = true, Checked = true };
            _rbLoop = new RadioButton { Text = "循环播放", Location = new Point(212, 322), AutoSize = true };

            _chkOneShot = new CheckBox { Text = "默认单次闹钟（触发后自动删除）", Location = new Point(110, 358), AutoSize = true };

            // 新建闹钟的默认响铃等待时间（秒）：单次播放默认 8 秒、循环播放默认 5 分钟，都可改。
            var lblWaitOnce = new Label { Text = "单次响铃等待", Location = new Point(16, 390), AutoSize = true };
            _numWaitOnce = new NumericUpDown { Location = new Point(110, 386), Size = new Size(70, 23) };
            _cmbWaitOnceUnit = new ComboBox { Location = new Point(186, 386), Size = new Size(62, 25) };
            WaitTimeInput.Bind(_numWaitOnce, _cmbWaitOnceUnit);

            var lblWaitLoop = new Label { Text = "循环响铃等待", Location = new Point(16, 422), AutoSize = true };
            _numWaitLoop = new NumericUpDown { Location = new Point(110, 418), Size = new Size(70, 23) };
            _cmbWaitLoopUnit = new ComboBox { Location = new Point(186, 418), Size = new Size(62, 25) };
            WaitTimeInput.Bind(_numWaitLoop, _cmbWaitLoopUnit);

            var btnReset = new Button { Text = "恢复最初默认", Location = new Point(16, 464), Size = new Size(130, 30) };
            btnReset.Click += (_, _) =>
            {
                // 一键恢复：直接重置为出厂默认并保存。
                _settings.ResetToFactory();
                _settings.Save();
                LoadFromSettings();
                MessageBox.Show(this, "已恢复为最初默认模板。", "默认闹钟模板",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            var btnOk = new Button { Text = "确定", Location = new Point(250, 464), Size = new Size(80, 30), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Location = new Point(338, 464), Size = new Size(80, 30), DialogResult = DialogResult.Cancel };
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            btnOk.Click += (_, _) => Save();

            Controls.AddRange(new Control[]
            {
                lblTitle, _txtTitle,
                lblSubtitle, _txtSubtitle,
                lblSnoozeTitle, _txtSnoozeTitle,
                lblSnoozeSubtitle, _txtSnoozeSubtitle,
                lblAutoSnoozeTitle, _txtAutoSnoozeTitle,
                lblAutoSnoozeSubtitle, _txtAutoSnoozeSubtitle,
                lblOpenButton, _txtOpenButton,
                lblSnoozeButton, _txtSnoozeButton,
                lblDismissButton, _txtDismissButton,
                lblMode, _rbOnce, _rbLoop,
                _chkOneShot,
                lblWaitOnce, _numWaitOnce, _cmbWaitOnceUnit,
                lblWaitLoop, _numWaitLoop, _cmbWaitLoopUnit,
                btnReset, btnOk, btnCancel
            });
        }

        private void LoadFromSettings()
        {
            _txtTitle.Text = _settings.DefaultTitle;
            _txtSubtitle.Text = _settings.DefaultSubtitle;
            _txtSnoozeTitle.Text = _settings.DefaultSnoozeTitle;
            _txtSnoozeSubtitle.Text = _settings.DefaultSnoozeSubtitle;
            _txtAutoSnoozeTitle.Text = _settings.DefaultAutoSnoozeTitle;
            _txtAutoSnoozeSubtitle.Text = _settings.DefaultAutoSnoozeSubtitle;
            _txtOpenButton.Text = _settings.DefaultOpenButton;
            _txtSnoozeButton.Text = _settings.DefaultSnoozeButton;
            _txtDismissButton.Text = _settings.DefaultDismissButton;
            _rbLoop.Checked = _settings.DefaultLoop;
            _rbOnce.Checked = !_settings.DefaultLoop;
            _chkOneShot.Checked = _settings.DefaultOneShot;

            // 响铃等待时间（秒）：单次播放默认 8 秒、循环播放默认 300 秒（5 分钟）。
            WaitTimeInput.SetSeconds(_numWaitOnce, _cmbWaitOnceUnit,
                _settings.DefaultPlayWaitSecondsOnce > 0 ? _settings.DefaultPlayWaitSecondsOnce : 8);
            WaitTimeInput.SetSeconds(_numWaitLoop, _cmbWaitLoopUnit,
                _settings.DefaultPlayWaitSecondsLoop > 0 ? _settings.DefaultPlayWaitSecondsLoop : 300);
        }

        private void Save()
        {
            _settings.DefaultTitle = string.IsNullOrWhiteSpace(_txtTitle.Text)
                ? "⏰ 闹钟时间到！"
                : _txtTitle.Text.Trim();
            _settings.DefaultSubtitle = _txtSubtitle.Text.Trim();
            _settings.DefaultSnoozeTitle = string.IsNullOrWhiteSpace(_txtSnoozeTitle.Text)
                ? "⏰ 贪睡结束！"
                : _txtSnoozeTitle.Text.Trim();
            _settings.DefaultSnoozeSubtitle = _txtSnoozeSubtitle.Text.Trim();
            _settings.DefaultAutoSnoozeTitle = string.IsNullOrWhiteSpace(_txtAutoSnoozeTitle.Text)
                ? "⏰ 已自动贪睡"
                : _txtAutoSnoozeTitle.Text.Trim();
            _settings.DefaultAutoSnoozeSubtitle = _txtAutoSnoozeSubtitle.Text.Trim();
            _settings.DefaultOpenButton = string.IsNullOrWhiteSpace(_txtOpenButton.Text)
                ? "打开"
                : _txtOpenButton.Text.Trim();
            _settings.DefaultSnoozeButton = string.IsNullOrWhiteSpace(_txtSnoozeButton.Text)
                ? "延迟 {0} 分钟"
                : _txtSnoozeButton.Text.Trim();
            _settings.DefaultDismissButton = string.IsNullOrWhiteSpace(_txtDismissButton.Text)
                ? "结束"
                : _txtDismissButton.Text.Trim();
            _settings.DefaultLoop = _rbLoop.Checked;
            _settings.DefaultOneShot = _chkOneShot.Checked;
            _settings.DefaultPlayWaitSecondsOnce = WaitTimeInput.GetSeconds(_numWaitOnce, _cmbWaitOnceUnit);
            _settings.DefaultPlayWaitSecondsLoop = WaitTimeInput.GetSeconds(_numWaitLoop, _cmbWaitLoopUnit);
            _settings.Save();
        }
    }
}
