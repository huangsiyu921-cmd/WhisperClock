using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace WhisperClock
{
    /// <summary>
    /// 闹钟列表页：展示所有闹钟；每个闹钟有独立的设置界面（AlarmEditForm）；
    /// 支持单次闹钟（触发后自动删除）、贪睡（Toast 按钮，5 分钟后再触发）、
    /// 托盘驻留、开机自启动与暗色模式。
    /// </summary>
    public class MainForm : Form
    {
        private const string StartupValueName = "WhisperClock";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // 确认期时长不再写死：由闹钟的“等待时长”（Alarm.PlayWaitSeconds，默认单次 20 秒 / 循环 1 分钟）
        // 决定。到点仍未操作 → 自动贪睡（进中间态1，见 CheckAlarms）。

        private readonly System.Windows.Forms.Timer _tickTimer;
        private readonly List<Alarm> _alarms = new();
        private bool _reallyExit;

        /// <summary>正在响铃（含延迟等待中）的闹钟 Id 集合；每个闹钟独立标记，互不顶替。
        /// 延迟播放前检查、播放器加载完成后检查；贪睡/结束/删除时移除。</summary>
        private readonly HashSet<Guid> _ringingIds = new();

        /// <summary>每个正在播放声音的闹钟对应的播放器（每闹钟一个，互不影响）。</summary>
        private readonly Dictionary<Guid, SoundPlayer> _players = new();

        /// <summary>保护 _ringingIds / _players 的锁：PlaySound 在后台线程，贪睡/结束/删除在 UI 线程。</summary>
        private readonly object _soundLock = new();

        /// <summary>最近一次已处理的 Toast 激活参数及其时间；用于短时间去重（compat 回调与 Shown 可能各送达一次）。</summary>
        private string? _lastActivation;
        private DateTime _lastActivationAt;

        /// <summary>每个闹钟的“本次响铃代际”：每次 Trigger 自增；延迟停声任务到点时会核对，
        /// 避免把“下一轮贪睡触发”的响铃停掉。（随机选曲改用线程安全的 Random.Shared，不再需要实例字段。）</summary>
        private readonly Dictionary<Guid, int> _ringGenerations = new();

        // ---- 控件 ----
        private readonly AppSettings _settings = AppSettings.Load();
        private Button _btnAdd = null!;
        private Button _btnEdit = null!;
        private Button _btnDelete = null!;
        private Button _btnOpen = null!;
        private Button _btnEnd = null!;
        private CheckBox _chkAutoStart = null!;
        private NumericUpDown _numSnooze = null!;
        private NumericUpDown _numDelay = null!;
        private CheckBox _chkTrayTip = null!;
        private Button _btnTemplate = null!;
        private ListView _listView = null!;
        private Label _lblStatus = null!;

        // ---- 系统托盘 ----
        private NotifyIcon _notifyIcon = null!;
        private ContextMenuStrip _trayMenu = null!;

        /// <summary>开机自启动（带 --minimized 参数）时是否直接隐藏到系统托盘。</summary>
        public bool StartMinimized { get; set; }

        private static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlarmClock");

        private static string DataFile => Path.Combine(DataDir, "alarms.json");

        public MainForm()
        {
            Text = "Whisper";
            ClientSize = new Size(700, 410);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;

            BuildUi();
            LoadSettingsToControls();
            LoadAlarms();
            LoadAutoStartState();
            RefreshList();

            _tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _tickTimer.Tick += (_, _) => CheckAlarms();
            _tickTimer.Start();

            // 只在窗体真正关闭时清理。必须用 FormClosed（不是 FormClosing）：点“×”只是隐藏到托盘，
            // 那种情况下若停掉 _tickTimer，闹钟就再也不响了；释放图标还会让托盘图标抛 ObjectDisposedException。
            FormClosed += (_, _) =>
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                ThemeManager.ThemeChanged -= OnThemeChanged;
                _tickTimer.Stop();
                _tickTimer.Dispose();
                StopAllSounds();
                DisposeThemeIcons();
            };

            // 兜底：若激活参数到达时主窗体已显示（例如进程因 Toast 点击而启动），在此处统一消费。
            Shown += (_, _) =>
            {
                if (!string.IsNullOrEmpty(Program.ActivationArgs))
                {
                    string? args = Program.ActivationArgs;
                    Program.ActivationArgs = null;
                    ProcessActivation(args);
                }

                // 开机自启动（--minimized）时直接隐藏到托盘，不打扰用户。
                if (StartMinimized)
                    HideToTray();
            };

            BuildTrayIcon();
            ApplyThemeIcons(); // 窗口 + 托盘图标：深色主题用白线条版，浅色主题用深线条版

            // 暗色模式：窗体显示前应用当前系统主题（含暗色标题栏）。
            // 登录时触发的闹钟也在 Load 触发——比 Shown 更早，越快越好（用户要求）。
            // 隐形登录检测：开启“使用我的登录信息在更新后自动完成设置”时，系统会在开机时
            // 做一次“隐形登录”并立即锁屏，Run 项在锁屏前就启动。若此时直接触发，用户还没
            // 解锁就响（判定丢失）。改为：会话已锁定 → 等 SessionUnlock（真正进桌面）再触发；
            // 未锁定（正常登录/手动启动）→ 立即触发（快）。
            Load += (_, _) =>
            {
                ThemeManager.Apply(this);
                ThemeManager.ThemeChanged += OnThemeChanged; // 系统主题切换 → 换窗口/托盘图标

                // 登录闹钟“进桌面判定”：隐形登录的 SessionLock 事件可能发生在订阅前而错过，
                // 10 秒兜底又会抢跑（锁屏时触发 → Toast 弹出但锁屏会话无声）。
                // 最终方案：锁屏应用 LockApp.exe 进程检测（锁屏/登录界面必运行，进桌面后退出）：
                // - 启动 10 秒后 LockApp 仍在运行 → 用户还在锁屏，等 SessionUnlock（已验证可靠）再触发
                // - LockApp 未运行（正常登录/手动启动，已进桌面）→ 触发
                SystemEvents.SessionSwitch += OnSessionSwitch;
                _ = LoginAlarmFallback();
            };
        }

        // ==================== 界面构建 ====================

        private void BuildUi()
        {
            _btnAdd = new Button { Text = "添加闹钟", Location = new Point(12, 12), Size = new Size(90, 30) };
            _btnAdd.Click += (_, _) => AddAlarm();

            _btnEdit = new Button { Text = "编辑", Location = new Point(110, 12), Size = new Size(70, 30) };
            _btnEdit.Click += (_, _) => EditSelected();

            _btnDelete = new Button { Text = "删除", Location = new Point(188, 12), Size = new Size(70, 30) };
            _btnDelete.Click += (_, _) => DeleteSelected();

            _btnOpen = new Button { Text = "打开", Location = new Point(266, 12), Size = new Size(70, 30) };
            _btnOpen.Click += (_, _) => OpenSelected();

            // “结束”按钮：停止响铃并结束本次触发（贪睡后/待确认中的闹钟也可结束）。
            _btnEnd = new Button { Text = "结束", Location = new Point(344, 12), Size = new Size(70, 30) };
            _btnEnd.Click += (_, _) => EndSelected();

            // “自启动”开关：注册主程序（--minimized），值名固定覆盖注册（登录信号机制已移除，由锁屏检测取代）。
            _chkAutoStart = new CheckBox { Text = "自启动", Location = new Point(430, 18), AutoSize = true };
            _chkAutoStart.CheckedChanged += (_, _) => ApplyAutoStart(_chkAutoStart.Checked);

            _listView = new ListView
            {
                Location = new Point(12, 52),
                Size = new Size(676, 282),
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _listView.Columns.Add("状态", 70);
            _listView.Columns.Add("时间", 80);
            _listView.Columns.Add("标题", 200);
            _listView.Columns.Add("单次", 45);
            _listView.Columns.Add("循环", 45);
            _listView.Columns.Add("音频", 230);
            _listView.ItemActivate += (_, _) => EditSelected();

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("打开目标", null, (_, _) => OpenSelected());
            contextMenu.Items.Add("结束本次", null, (_, _) => EndSelected());
            contextMenu.Items.Add("编辑", null, (_, _) => EditSelected());
            contextMenu.Items.Add("启用 / 禁用", null, (_, _) => ToggleSelected());
            contextMenu.Items.Add("删除", null, (_, _) => DeleteSelected());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("导出选中…", null, (_, _) => ExportSelected());
            contextMenu.Items.Add("导出全部…", null, (_, _) => ExportAll());
            contextMenu.Items.Add("导入…", null, (_, _) => ImportAlarms());
            _listView.ContextMenuStrip = contextMenu;

            var lblSnooze = new Label { Text = "贪睡时长(分)", Location = new Point(12, 342), AutoSize = true };
            _numSnooze = new NumericUpDown
            {
                Location = new Point(100, 338),
                Size = new Size(48, 23),
                Minimum = 1,
                Maximum = 60,
                Value = 5
            };
            _numSnooze.ValueChanged += (_, _) =>
            {
                if (_loadingSettings)
                    return; // 载入时的赋值不算用户修改
                _settings.SnoozeMinutes = (int)_numSnooze.Value;
                _settings.Save();
            };

            _chkTrayTip = new CheckBox { Text = "最小化时显示提示", Location = new Point(310, 340), AutoSize = true };
            _chkTrayTip.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings)
                    return;
                _settings.ShowTrayTipOnMinimize = _chkTrayTip.Checked;
                _settings.Save();
            };

            var lblDelay = new Label { Text = "音频延迟(秒)", Location = new Point(160, 342), AutoSize = true };
            _numDelay = new NumericUpDown
            {
                Location = new Point(250, 338),
                Size = new Size(52, 23),
                Minimum = 0,
                Maximum = 30,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = 3
            };
            _numDelay.ValueChanged += (_, _) =>
            {
                if (_loadingSettings)
                    return;
                _settings.AudioDelaySeconds = (double)_numDelay.Value;
                _settings.Save();
            };

            _btnTemplate = new Button { Text = "默认模板…", Location = new Point(592, 336), Size = new Size(96, 26) };
            _btnTemplate.Click += (_, _) =>
            {
                using var dialog = new TemplateForm(_settings);
                dialog.ShowDialog(this);
            };

            _lblStatus = new Label
            {
                Location = new Point(12, 370),
                Size = new Size(676, 24),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.MiddleLeft
            };

            Controls.AddRange(new Control[]
            {
                _btnAdd, _btnEdit, _btnDelete, _btnOpen, _btnEnd, _chkAutoStart, _listView,
                lblSnooze, _numSnooze, lblDelay, _numDelay, _chkTrayTip, _btnTemplate, _lblStatus
            });
        }

        /// <summary>载入设置行控件期间为 true：避免赋值触发的 ValueChanged 把 clamp 后的值又写回设置。</summary>
        private bool _loadingSettings;

        /// <summary>把已保存的设置载入设置行控件。</summary>
        private void LoadSettingsToControls()
        {
            _loadingSettings = true;
            try
            {
                _numSnooze.Value = Math.Clamp(_settings.SnoozeMinutes, 1, 60);
                _numDelay.Value = Math.Clamp((decimal)_settings.AudioDelaySeconds, 0, 30);
                _chkTrayTip.Checked = _settings.ShowTrayTipOnMinimize;
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        // ==================== 闹钟增删改 ====================

        private void AddAlarm()
        {
            using var dialog = new AlarmEditForm(_settings, null);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _alarms.Add(dialog.Alarm);
                SaveAlarms();
                RefreshList("已添加闹钟：" + dialog.Alarm.Title);
            }
        }

        private Alarm? SelectedAlarm =>
            _listView.SelectedItems.Count > 0 ? _listView.SelectedItems[0].Tag as Alarm : null;

        private void EditSelected()
        {
            var alarm = SelectedAlarm;
            if (alarm == null)
            {
                _lblStatus.Text = "请先在列表中选择一个闹钟";
                return;
            }

            using var dialog = new AlarmEditForm(_settings, alarm);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                SaveAlarms();
                RefreshList($"已保存闹钟：“{alarm.Title}”");
            }
        }

        private void ToggleSelected()
        {
            var alarm = SelectedAlarm;
            if (alarm == null)
                return;

            alarm.Enabled = !alarm.Enabled;
            if (!alarm.Enabled)
            {
                // 禁用即彻底静默：清掉贪睡、待确认与随机计划点。否则禁用后
                // 待确认超时仍会触发自动贪睡（PendingDeleteUntil 未清）→ 到点复活响铃。
                alarm.SnoozeUntil = null;
                alarm.PendingDeleteUntil = null;
                alarm.RandomTriggerAt = null;
            }

            SaveAlarms();
            RefreshList($"“{alarm.Title}”已{(alarm.Enabled ? "启用" : "禁用")}");
        }

        private void DeleteSelected()
        {
            var alarm = SelectedAlarm;
            if (alarm == null)
            {
                _lblStatus.Text = "请先在列表中选择一个闹钟";
                return;
            }

            if (MessageBox.Show(this, $"确定删除闹钟“{alarm.Title}”吗？", "删除闹钟",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _alarms.Remove(alarm);
            StopSound(alarm.Id);
            SaveAlarms();
            RefreshList($"已删除闹钟：“{alarm.Title}”");
        }

        /// <summary>打开选中闹钟的“打开目标”（程序用系统默认程序打开网址/应用/文件）。</summary>
        private void OpenSelected()
        {
            var alarm = SelectedAlarm;
            if (alarm == null)
            {
                _lblStatus.Text = "请先在列表中选择一个闹钟";
                return;
            }

            if (string.IsNullOrWhiteSpace(alarm.OpenTarget))
            {
                _lblStatus.Text = "该闹钟未设置打开目标";
                return;
            }

            OpenTarget(alarm);
        }

        /// <summary>结束选中的闹钟本次提醒（停止响铃、清除贪睡/待确认；单次闹钟删除）。</summary>
        private void EndSelected()
        {
            var alarm = SelectedAlarm;
            if (alarm == null)
            {
                _lblStatus.Text = "请先在列表中选择一个闹钟";
                return;
            }

            if (alarm.SnoozeUntil.HasValue || alarm.PendingDeleteUntil.HasValue || IsRinging(alarm.Id))
                EndAlarm(alarm);
            else
                _lblStatus.Text = $"“{alarm.Title}”当前未在响铃/待确认中";
        }

        /// <summary>结束闹钟的本次提醒：停止响铃、清除贪睡与待确认状态；单次闹钟从列表删除。</summary>
        public void EndAlarm(Alarm alarm)
        {
            Program.Log("结束：" + alarm.Title);
            StopSound(alarm.Id);

            alarm.SnoozeUntil = null;
            alarm.PendingDeleteUntil = null;

            bool removed = alarm.OneShot;
            if (removed)
                _alarms.Remove(alarm);

            SaveAlarms();
            // 提示必须经 RefreshList 传入：它会重写状态栏，先设文本再刷新会被盖掉。
            RefreshList(removed
                ? $"“{alarm.Title}”已结束并删除"
                : $"“{alarm.Title}”已结束，明天照常提醒");
        }

        // ==================== 导出 / 导入 ====================

        /// <summary>导出选中的单个闹钟为 .json 文件。</summary>
        private void ExportSelected()
        {
            var alarm = SelectedAlarm;
            if (alarm == null)
            {
                _lblStatus.Text = "请先在列表中选择一个闹钟";
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "导出选中闹钟",
                Filter = "闹钟文件 (*.json)|*.json",
                FileName = "闹钟-" + SanitizeFileName(alarm.Title) + ".json",
                DefaultExt = "json"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                File.WriteAllText(dialog.FileName,
                    JsonSerializer.Serialize(alarm, JsonOptions));
                _lblStatus.Text = "已导出：" + dialog.FileName;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "导出失败：" + ex.Message;
            }
        }

        /// <summary>导出全部闹钟为 .json 文件（数组）。</summary>
        private void ExportAll()
        {
            if (_alarms.Count == 0)
            {
                _lblStatus.Text = "没有闹钟可导出";
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "导出全部闹钟",
                Filter = "闹钟文件 (*.json)|*.json",
                FileName = "闹钟列表-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json",
                DefaultExt = "json"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                File.WriteAllText(dialog.FileName,
                    JsonSerializer.Serialize(_alarms, JsonOptions));
                _lblStatus.Text = $"已导出 {_alarms.Count} 个闹钟：" + dialog.FileName;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "导出失败：" + ex.Message;
            }
        }

        /// <summary>从 .json 文件导入闹钟（兼容单个对象或数组），导入后重新生成 Id。</summary>
        private void ImportAlarms()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "导入闹钟",
                Filter = "闹钟文件 (*.json)|*.json|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                var json = File.ReadAllText(dialog.FileName);
                List<Alarm>? list = null;
                try
                {
                    list = JsonSerializer.Deserialize<List<Alarm>>(json);
                }
                catch
                {
                    // 不是数组，尝试单个对象。
                }

                if (list == null)
                {
                    var single = JsonSerializer.Deserialize<Alarm>(json);
                    if (single != null)
                        list = new List<Alarm> { single };
                }

                if (list == null || list.Count == 0)
                {
                    _lblStatus.Text = "未从文件解析到闹钟";
                    return;
                }

                foreach (var a in list)
                {
                    a.Id = Guid.NewGuid(); // 避免与现有闹钟 Id 冲突
                    Normalize(a);          // 与 LoadAlarms 一致：旧的 RemindOnly 开关迁移为 Mode
                    _alarms.Add(a);
                }

                SaveAlarms();
                RefreshList($"已导入 {list.Count} 个闹钟");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "导入失败：" + ex.Message;
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string SanitizeFileName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "闹钟" : name;
        }

        /// <summary>主页列表的状态文本：未启用 / 贪睡中 / 等待（含响铃、待确认、等待触发）。</summary>
        private static string AlarmStatusText(Alarm alarm)
        {
            if (!alarm.Enabled)
                return "未启用";
            if (alarm.SnoozeUntil.HasValue)
                return "贪睡中";
            return "等待";
        }

        /// <summary>重建列表，状态栏显示默认统计文本。</summary>
        private void RefreshList() => RebuildList(DefaultStatusText());

        /// <summary>重建列表并把状态栏设为指定提示（提示不会再被列表重建覆盖）。</summary>
        private void RefreshList(string status) => RebuildList(status);

        /// <summary>只同步列表内容、不动状态栏（供每秒检查里的状态变化使用）。</summary>
        private void RefreshListSilently() => RebuildList(null);

        private string DefaultStatusText() => _alarms.Count == 0
            ? "没有闹钟，点击“添加闹钟”创建"
            : $"共 {_alarms.Count} 个闹钟（双击列表项可编辑）";

        private void RebuildList(string? status)
        {
            var previouslySelected = SelectedAlarm; // 重建会丢选中项，重建后按引用恢复

            _listView.BeginUpdate();
            _listView.Items.Clear();

            ListViewItem? toSelect = null;
            foreach (var alarm in _alarms)
            {
                var item = new ListViewItem(AlarmStatusText(alarm));
                item.SubItems.Add(alarm.TimeOfDay.ToString(@"hh\:mm"));
                item.SubItems.Add(string.IsNullOrWhiteSpace(alarm.Title) ? "闹钟" : alarm.Title);
                item.SubItems.Add(alarm.OneShot ? "是" : "否");
                item.SubItems.Add(alarm.Loop ? "是" : "否");
                item.SubItems.Add(string.IsNullOrEmpty(alarm.AudioPath) ? "（无）" : Path.GetFileName(alarm.AudioPath));
                item.Tag = alarm;
                _listView.Items.Add(item);

                if (previouslySelected != null && ReferenceEquals(alarm, previouslySelected))
                    toSelect = item;
            }

            _listView.EndUpdate();

            if (toSelect != null)
            {
                toSelect.Selected = true;
                toSelect.EnsureVisible();
            }

            if (status != null)
                _lblStatus.Text = status;
        }

        // ==================== 持久化 ====================

        private void LoadAlarms()
        {
            try
            {
                if (File.Exists(DataFile))
                {
                    var list = JsonSerializer.Deserialize<List<Alarm>>(File.ReadAllText(DataFile));
                    if (list != null)
                    {
                        _alarms.Clear();
                        foreach (var alarm in list)
                        {
                            Normalize(alarm);
                            _alarms.Add(alarm);
                        }
                    }
                }
            }
            catch
            {
                // 数据损坏时从空列表开始。
            }
        }

        /// <summary>
        /// 旧数据规范化：0.2.2alpha 的“纯提醒”开关（RemindOnly）迁移为 Mode（新数据以 Mode 为准）。
        /// 加载和导入都要走这一步，否则“从文件导入”的旧数据行为与“直接加载”不一致。
        /// </summary>
        private static void Normalize(Alarm alarm)
        {
            if (alarm.RemindOnly && alarm.Mode == AlarmMode.Normal)
                alarm.Mode = AlarmMode.RemindOnly;
        }

        private void SaveAlarms()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(DataFile,
                    JsonSerializer.Serialize(_alarms, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 保存失败不影响本次运行。
            }
        }

        // ==================== 开机启动（注册表） ====================

        private void LoadAutoStartState()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                _chkAutoStart.Checked = key?.GetValue(StartupValueName) != null;
            }
            catch
            {
                _chkAutoStart.Checked = false;
            }
        }

        private void ApplyAutoStart(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key == null)
                    return;

                if (enabled)
                    key.SetValue(StartupValueName, "\"" + Application.ExecutablePath + "\" --minimized");
                else
                    key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "设置自启动失败：" + ex.Message;
            }
        }

        // ==================== 闹钟检查（每秒遍历） ====================

        private void CheckAlarms()
        {
            var now = DateTime.Now;
            bool listStateChanged = false; // 列表“状态”列是否需要同步（触发 / 自动贪睡会改变它）

            foreach (var alarm in _alarms)
            {
                // 贪睡优先：贪睡到点后再次触发。
                if (alarm.SnoozeUntil.HasValue)
                {
                    // 禁用状态不处理贪睡：禁用时 ToggleSelected 已清 SnoozeUntil，此处是纵深防御（防旧数据/异常路径残留）。
                    if (alarm.Enabled && now >= alarm.SnoozeUntil.Value && alarm.LastTriggeredMinute != MinuteKey(now))
                    {
                        alarm.LastTriggeredMinute = MinuteKey(now);
                        alarm.SnoozeUntil = null;
                        Trigger(alarm, fromSnooze: true, now);
                        listStateChanged = true; // “贪睡中” → “等待”
                    }
                    continue;
                }

                if (alarm.Enabled)
                {
                    // 登录时触发的闹钟不按时间触发（启动时已触发一次），跳过随机/到点逻辑；
                    // 贪睡/确认期/自动贪睡逻辑照常（登录闹钟被贪睡后同样会再次提醒）。
                    if (!alarm.TriggerAtLogin)
                    {
                        var target = DateTime.Today.Add(alarm.TimeOfDay);

                        // 只有“今天的目标分钟已完全过去”（超过 60 秒）才顺延到明天；
                        // 目标分钟到来即触发，避免 target <= now 把刚到点的闹钟直接推到明天（永远不响）。
                        if (target < now.AddSeconds(-60))
                            target = target.AddDays(1);

                        // 当天已完成：今天已触发过（含随机提前、贪睡到点再触发）→ 不再重新随机计划、不再按目标触发。
                        // 修复：随机提前触发后目标到点不再二次触发；贪睡到点后不再重新随机（贪睡后不会乱响）。
                        bool triggeredToday = alarm.LastTriggeredMinute != null
                            && alarm.LastTriggeredMinute.StartsWith(now.ToString("yyyyMMdd"), StringComparison.Ordinal);

                        // 随机偏移：进入 [目标-N, 目标+N] 窗口后计划一个随机触发点（提前/延后均可能）；
                        // 计划点已过（如程序重启后）则改为立即触发。
                        // 随机点限制在目标日当天：提前不跨到前一天（0:00 闹钟不会昨晚响+今早再响），延后不跨到第二天。
                        if (!triggeredToday && alarm.RandomOffsetSeconds > 0 && alarm.RandomTriggerAt == null
                            && now >= target.AddSeconds(-alarm.RandomOffsetSeconds))
                        {
                            int offset = Random.Shared.Next(-alarm.RandomOffsetSeconds, alarm.RandomOffsetSeconds + 1);
                            DateTime plan = target.AddSeconds(offset);
                            if (plan < target.Date)
                                plan = target.Date; // 提前跨日 → 当天 00:00
                            if (plan.Date > target.Date)
                                plan = target.Date.AddDays(1).AddSeconds(-1); // 延后跨日 → 当天 23:59:59
                            alarm.RandomTriggerAt = plan;
                            if (alarm.RandomTriggerAt.Value <= now)
                                alarm.RandomTriggerAt = now;
                        }

                        DateTime fireAt = alarm.RandomTriggerAt ?? target;
                        if (!triggeredToday && now >= fireAt && alarm.LastTriggeredMinute != MinuteKey(now))
                        {
                            alarm.LastTriggeredMinute = MinuteKey(now);
                            alarm.RandomTriggerAt = null;
                            Trigger(alarm, fromSnooze: false, now);
                        }
                    }
                }

                // 待确认超时且用户未点击任何按钮 → 默认自动贪睡（再次提醒，直到用户操作）。
                // 单次闹钟同样自动贪睡循环；用户点“结束”/“打开”才会删除。
                // Enabled 条件：禁用后（PendingDeleteUntil 已被清）不应再走自动贪睡，纵深防御。
                if (alarm.Enabled && alarm.PendingDeleteUntil.HasValue && now >= alarm.PendingDeleteUntil.Value)
                {
                    alarm.PendingDeleteUntil = null;
                    StopSound(alarm.Id);
                    alarm.SnoozeUntil = DateTime.Now.AddMinutes(_settings.SnoozeMinutes);
                    Program.Log($"自动贪睡：{alarm.Title}（未操作，{_settings.SnoozeMinutes} 分钟后再次提醒）");
                    _lblStatus.Text = $"“{alarm.Title}”未操作，已自动贪睡 {_settings.SnoozeMinutes} 分钟";
                    listStateChanged = true; // “等待” → “贪睡中”
                    ShowAutoSnoozeToast(alarm); // 单独的“已自动贪睡”提示 Toast（无按钮）
                }
            } // foreach (_alarms)

            if (listStateChanged)
                RefreshListSilently(); // 只同步列表内容，不覆盖状态栏提示
        }

        /// <summary>
        /// 仅通知 / 纯提醒模式的收尾：等音频播起来并到达“响铃等待时间”后停止响铃
        /// （等待时长由闹钟自身或“默认模板…”配置，不再写死 2 秒——原来长于 2 秒的 wav 会被掐断）。
        /// 单次闹钟顺带删除；常规闹钟只停声音并清掉响铃标记（下次照常提醒）。
        /// </summary>
        private async Task FinishPlaybackLater(Alarm alarm, int generation, TimeSpan delay)
        {
            await Task.Delay(delay);
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => { _ = FinishPlaybackLater(alarm, generation, TimeSpan.Zero); }));
                return;
            }

            if (!_alarms.Contains(alarm))
                return; // 已被用户手动删除/禁用

            if (!IsCurrentRingGeneration(alarm, generation))
                return; // 已被新一轮触发取代，别停错/删错

            bool wasRinging = IsRinging(alarm.Id);
            StopSound(alarm.Id); // 停止播放器并清 _ringingIds（非单次闹钟原来会一直留着标记）

            if (alarm.OneShot)
            {
                _alarms.Remove(alarm);
                SaveAlarms();
                RefreshList($"“{alarm.Title}”已结束并删除");
            }
            else if (wasRinging)
            {
                _lblStatus.Text = $"“{alarm.Title}”已结束";
            }
        }

        /// <summary>核对闹钟当前的响铃代际是否仍是 generation（延迟任务到点后调用）。</summary>
        private bool IsCurrentRingGeneration(Alarm alarm, int generation)
        {
            lock (_soundLock)
            {
                return _ringGenerations.TryGetValue(alarm.Id, out var current) && current == generation;
            }
        }

        /// <summary>会话切换事件（SystemEvents 在系统线程回调，需封送 UI 线程）：
        /// SessionUnlock（真正进桌面）→ 触发登录闹钟。SessionLock 不处理（可能错过，改用 LockApp 进程检测）。</summary>
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason != SessionSwitchReason.SessionUnlock)
                return;

            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (InvokeRequired)
                BeginInvoke(new Action(TriggerLoginAlarms));
            else
                TriggerLoginAlarms();
        }

        /// <summary>兜底：启动 10 秒后检查锁屏应用 LockApp.exe——仍运行（锁屏/登录界面）→ 不触发，等
        /// SessionUnlock；未运行（正常登录/手动启动，已进桌面）→ 触发。锁屏时触发会“Toast 弹出但无声”。</summary>
        private async Task LoginAlarmFallback()
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (IsLockScreenActive())
                return; // 锁屏中（LockApp 运行），等 SessionUnlock 触发
            TriggerLoginAlarms();
        }

        /// <summary>锁屏应用 LockApp.exe 是否在运行：锁屏/登录界面时为 true，进桌面后退出。</summary>
        private static bool IsLockScreenActive()
        {
            try
            {
                return System.Diagnostics.Process.GetProcessesByName("LockApp").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>登录闹钟是否已触发过（解锁触发或兜底触发只发生一次）。</summary>
        private bool _loginAlarmsTriggered;

        /// <summary>触发所有“登录时触发”的闹钟（每次启动一次；被贪睡后照常再次提醒）。</summary>
        private void TriggerLoginAlarms()
        {
            if (_loginAlarmsTriggered)
                return; // 已触发过（立即触发/解锁触发只发生一次）
            _loginAlarmsTriggered = true;

            var now = DateTime.Now;
            var loginAlarms = _alarms.Where(a => a.Enabled && a.TriggerAtLogin).ToList();
            foreach (var alarm in loginAlarms)
            {
                // 与按时间触发一致：记录本次触发分钟，防止贪睡到点同分钟重复触发。
                alarm.LastTriggeredMinute = MinuteKey(now);
                Trigger(alarm, fromSnooze: false, now);
            }
        }

        /// <summary>分钟标识，用于“同一分钟最多触发一次”。</summary>
        private static string MinuteKey(DateTime time) => time.ToString("yyyyMMddHHmm");

        /// <summary>音频延迟秒数：登录时触发的闹钟跳过延迟（越快越好），与 DelayPlaySound 保持一致。</summary>
        private double AudioDelayFor(Alarm alarm)
            => alarm.TriggerAtLogin ? 0 : Math.Max(0, _settings.AudioDelaySeconds);

        /// <summary>
        /// 解析闹钟的“等待时长”（秒）：触发后等这么久就走分支——普通模式到点自动贪睡（进中间态1），
        /// 仅通知/纯提醒到点直接结束。闹钟自身值 &gt; 0 用它；否则按播放模式回退到“默认模板…”的默认值
        /// （单次播放 20 秒 / 循环播放 60 秒），最后再硬编码兜底防设置损坏。
        /// </summary>
        private double ResolvePlayWaitSeconds(Alarm alarm)
        {
            if (alarm.PlayWaitSeconds > 0)
                return alarm.PlayWaitSeconds;

            double configured = alarm.Loop ? _settings.DefaultPlayWaitSecondsLoop : _settings.DefaultPlayWaitSecondsOnce;
            if (configured > 0)
                return configured;

            return alarm.Loop ? 60 : 20;
        }

        /// <summary>把秒数显示成简短中文：整分钟用“N 分钟”，否则用“N 秒”。</summary>
        private static string FormatWaitSeconds(double seconds)
            => seconds >= 60 && Math.Abs(seconds % 60) < 0.001
                ? $"{seconds / 60:0.##} 分钟"
                : $"{seconds:0.##} 秒";

        private void Trigger(Alarm alarm, bool fromSnooze, DateTime now)
        {
            _lblStatus.Text = fromSnooze
                ? $"“{alarm.Title}”贪睡提醒已触发"
                : $"“{alarm.Title}”闹钟已触发";

            // “响铃等待”对普通模式与仅通知/纯提醒模式都生效，这里解析一次供两条路径共用。
            double waitSeconds = ResolvePlayWaitSeconds(alarm);

            int generation;
            lock (_soundLock)
            {
                _ringingIds.Add(alarm.Id); // 标记为待响铃；贪睡/结束/删除时移除
                // 本次响铃的代际：延迟停声任务到点时会核对，避免把“下一轮贪睡触发”的响铃停掉。
                generation = _ringGenerations.TryGetValue(alarm.Id, out var previous) ? previous + 1 : 1;
                _ringGenerations[alarm.Id] = generation;
            }

            if (alarm.Mode != AlarmMode.Normal)
            {
                // 仅通知 / 纯提醒：无确认期、不贪睡；直接播放音频（无音频则无现象），
                // 有“打开目标”直接打开，随后直接结束（单次闹钟播完自动删除，常规闹钟明天照常）。
                // 差异：仅通知先弹一个无按钮 Toast 提示；纯提醒（beta）完全不弹。
                if (alarm.Mode == AlarmMode.NotifyOnly)
                    ShowNotifyOnlyToast(alarm);
                if (!string.IsNullOrEmpty(alarm.OpenTarget))
                    OpenExternal(alarm.OpenTarget); // 链接直接打开（后台线程执行，不阻塞）
                DelayPlaySound(alarm);

                // 响铃等待：音频开始播放后等待“响铃等待时间”再停止响铃（单次闹钟顺带删除）。
                // 时长取闹钟自身设置，未设置则按播放模式回退到“默认模板…”的默认值
                // （单次播放 8 秒 / 循环播放 5 分钟）；不再写死 AudioDelaySeconds + 2。
                // 等待时长到点 → 直接结束（这些模式不进状态机、不贪睡）。
                _ = FinishPlaybackLater(alarm, generation, TimeSpan.FromSeconds(waitSeconds));

                _lblStatus.Text += $"（{FormatWaitSeconds(waitSeconds)}后结束）";
                return;
            }

            ShowToast(alarm, fromSnooze); // 先弹 Toast 通知
            DelayPlaySound(alarm);         // 延迟设置秒数后再播放音频

            // 触发后（首次或贪睡后再触发）都进入“待确认”中间态：
            // 期间可在 Toast 上贪睡/结束，或在主界面“结束”；超时后单次闹钟删除、常规闹钟恢复。
            alarm.PendingDeleteUntil = now.AddSeconds(waitSeconds);

            _lblStatus.Text += $"（{FormatWaitSeconds(waitSeconds)}后自动贪睡）";
        }

        /// <summary>等待设置的音频延迟秒数后播放铃声；期间若已贪睡/停止/删除则不再播放。</summary>
        private async void DelayPlaySound(Alarm alarm)
        {
            try
            {
                // 登录时触发的闹钟“越快越好”：跳过音频延迟设置（AudioDelaySeconds 默认 3 秒会拖慢登录提示）。
                await Task.Delay(TimeSpan.FromSeconds(AudioDelayFor(alarm)));

                // Load + 播放放到后台线程：大 .wav 的同步 Load 不再卡 UI 线程，
                // 避免“通知打开/贪睡”等激活操作因 UI 被占用而迟迟得不到处理。
                // 按闹钟自身状态判断（仍待响铃且未被贪睡/结束/删除），不依赖全局单值：
                // 其它闹钟触发不会顶掉本闹钟延迟窗口内的播放。
                // 纯提醒模式没有确认期（PendingDeleteUntil 为空），统一以响铃标记为准。
                if (_alarms.Contains(alarm) && IsRinging(alarm.Id))
                    await Task.Run(() => PlaySound(alarm));
            }
            catch
            {
                // 延迟期间被关闭等异常不影响主流程。
            }
        }

        /// <summary>在后台线程执行：同步加载 .wav 并播放（单次/循环）。加载期间被停止则不播放。</summary>
        private void PlaySound(Alarm alarm)
        {
            if (string.IsNullOrEmpty(alarm.AudioPath))
            {
                UpdateStatusAppend("（未设置铃声，仅发送通知）");
                return;
            }

            string? audio = ResolveAudioFile(alarm);
            if (audio == null)
                return; // ResolveAudioFile 已给出具体提示

            try
            {
                var player = new SoundPlayer(audio);
                player.Load(); // 同步预加载（在后台线程，不卡 UI）；播放失败立即捕获

                // 注册 + 播放原子进行：加载期间若已被贪睡/结束/删除（StopSound 已移除响铃标记），则不播放。
                lock (_soundLock)
                {
                    if (!_ringingIds.Contains(alarm.Id))
                    {
                        player.Dispose(); // 加载期间已被贪睡/结束/删除
                        return;
                    }

                    // 防御：同一闹钟理论上不会重复触发（分钟去重），若有残留播放器先停掉；不影响其它闹钟的声音。
                    if (_players.TryGetValue(alarm.Id, out var old))
                    {
                        try { old.Stop(); } catch { }
                        old.Dispose();
                    }

                    _players[alarm.Id] = player;
                    if (alarm.Loop)
                        player.PlayLooping(); // 循环播放
                    else
                        player.Play();        // 单次播放
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("音频播放失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 解析实际播放的 .wav：路径为文件时直接返回；路径为文件夹时随机选取其中
        /// 一个可播放音频（递归含嵌套子文件夹；支持 .lnk 快捷方式——解析后跟随到
        /// 目标文件夹/目标 .wav，如 D:\voice\Jigsaw\welcome 里放快捷方式指向音频目录）；
        /// 找不到可用音频返回 null（并更新状态提示）。
        /// </summary>
        private string? ResolveAudioFile(Alarm alarm)
        {
            var path = alarm.AudioPath;
            if (string.IsNullOrEmpty(path))
                return null;

            if (File.Exists(path))
                return path;

            if (Directory.Exists(path))
            {
                var candidates = new List<string>();
                // 环检测集合：Windows 路径大小写不敏感。
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectAudioCandidates(path, candidates, visited, 0);
                if (candidates.Count > 0)
                    return candidates[Random.Shared.Next(candidates.Count)];

                UpdateStatusAppend("（文件夹内没有可播放的音频）");
                return null;
            }

            UpdateStatusAppend("（未找到音频文件）");
            return null;
        }

        /// <summary>音频文件夹递归扫描的深度上限（配合 visited，兜住符号链接/目录联接造成的环）。</summary>
        private const int MaxAudioScanDepth = 24;

        /// <summary>
        /// 递归收集文件夹内可播放的 .wav；文件夹里的 .lnk 快捷方式会被解析，
        /// 目标是文件夹则继续递归收集，目标是 .wav 文件则直接加入。异常（权限/坏快捷方式）静默跳过。
        /// visited 记录已扫过的目录：快捷方式指回自身/祖先目录时不会无限递归
        /// （原实现没有环检测，会 StackOverflowException 直接崩掉进程，且该异常无法 catch）。
        /// </summary>
        private void CollectAudioCandidates(string folder, List<string> candidates, HashSet<string> visited, int depth)
        {
            if (depth > MaxAudioScanDepth)
                return;

            if (!visited.Add(NormalizeDirectoryPath(folder)))
                return; // 这个目录已经扫过（快捷方式指回了自己/祖先 → 成环）

            try
            {
                foreach (var wav in Directory.GetFiles(folder, "*.wav"))
                    candidates.Add(wav);

                foreach (var lnk in Directory.GetFiles(folder, "*.lnk"))
                {
                    string? target = ResolveShortcutTarget(lnk);
                    if (string.IsNullOrEmpty(target))
                        continue;
                    if (File.Exists(target))
                    {
                        if (target.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                            candidates.Add(target);
                    }
                    else if (Directory.Exists(target))
                    {
                        CollectAudioCandidates(target, candidates, visited, depth + 1);
                    }
                }

                foreach (var sub in Directory.GetDirectories(folder))
                    CollectAudioCandidates(sub, candidates, visited, depth + 1);
            }
            catch
            {
                // 权限等原因读取失败时忽略该目录。
            }
        }

        /// <summary>目录路径规范化（全路径 + 去尾部分隔符）后作为 visited 键；失败时退回原字符串。</summary>
        private static string NormalizeDirectoryPath(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        /// <summary>解析 .lnk 快捷方式的 TargetPath（WScript.Shell）；失败返回 null。</summary>
        private static string? ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    return null;
                var shell = Activator.CreateInstance(shellType);
                if (shell == null)
                    return null;
                dynamic com = shell;
                dynamic lnk = com.CreateShortcut(lnkPath);
                string target = lnk.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>发送 Windows 原生 Toast 通知：自定义主标题、副标题，带“贪睡”按钮（携带闹钟 Id）。</summary>
        private void ShowToast(Alarm alarm, bool fromSnooze)
        {
            string title;
            string subtitle;
            if (fromSnooze)
            {
                // 贪睡提醒主/副标题优先级：闹钟自己的贪睡标题 > “默认模板…”中的贪睡内容 > 兜底。
                title = !string.IsNullOrWhiteSpace(alarm.SnoozeTitle)
                    ? alarm.SnoozeTitle!
                    : !string.IsNullOrWhiteSpace(_settings.DefaultSnoozeTitle)
                        ? _settings.DefaultSnoozeTitle
                        : (string.IsNullOrWhiteSpace(alarm.Title) ? "⏰ 贪睡结束！" : alarm.Title);
                subtitle = !string.IsNullOrWhiteSpace(alarm.SnoozeSubtitle)
                    ? alarm.SnoozeSubtitle!
                    : !string.IsNullOrWhiteSpace(_settings.DefaultSnoozeSubtitle)
                        ? _settings.DefaultSnoozeSubtitle
                        : "该起床啦！";
            }
            else
            {
                title = string.IsNullOrWhiteSpace(alarm.Title) ? "⏰ 闹钟时间到！" : alarm.Title;
                subtitle = string.IsNullOrWhiteSpace(alarm.Subtitle) ? "闹钟提醒" : alarm.Subtitle;
            }

            var builder = new ToastContentBuilder()
                .AddText(title)   // 主标题
                .AddText(subtitle); // 副标题

            // 按钮从左到右：“打开”（可选）、“延迟”、“结束”。按钮文字可在“默认模板…”中自定义。
            // “打开”激活参数携带 {闹钟Id}|{目标}：既能直接打开（闹钟已删除时也可用），
            // 也能让主实例识别闹钟——打开目标即视为完成本次提醒。
            if (!string.IsNullOrWhiteSpace(alarm.OpenTarget))
            {
                builder.AddButton(new ToastButton(
                    string.IsNullOrWhiteSpace(_settings.DefaultOpenButton) ? "打开" : _settings.DefaultOpenButton,
                    $"open-target:{alarm.Id}|{alarm.OpenTarget}")
                {
                    ActivationType = ToastActivationType.Foreground
                });
            }
            builder.AddButton(new ToastButton(
                string.Format(string.IsNullOrWhiteSpace(_settings.DefaultSnoozeButton) ? "延迟 {0} 分钟" : _settings.DefaultSnoozeButton, _settings.SnoozeMinutes),
                $"snooze:{alarm.Id}")
            {
                ActivationType = ToastActivationType.Foreground
            });
            builder.AddButton(new ToastButton(
                string.IsNullOrWhiteSpace(_settings.DefaultDismissButton) ? "结束" : _settings.DefaultDismissButton,
                $"dismiss:{alarm.Id}")
            {
                ActivationType = ToastActivationType.Foreground
            });

            var content = builder.GetToastContent();

            try
            {
                // Toast 发送放到后台线程：ToastNotificationManagerCompat 偶发会阻塞调用线程
                // （非打包应用的 dispatcher 初始化），不能卡住 UI 线程上的秒级 Timer。
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var toast = new ToastNotification(content.GetXml());
                        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
                    }
                    catch
                    {
                        // 发送失败不影响闹钟主流程。
                    }
                });
            }
            catch
            {
                _lblStatus.Text = "发送通知失败";
            }
        }

        /// <summary>仅通知模式的 Toast：无按钮，仅提示主/副标题（触发时直接播/打开，无需交互，不贪睡）。</summary>
        private void ShowNotifyOnlyToast(Alarm alarm)
        {
            var content = new ToastContentBuilder()
                .AddText(string.IsNullOrWhiteSpace(alarm.Title) ? "⏰ 闹钟时间到！" : alarm.Title)
                .AddText(string.IsNullOrWhiteSpace(alarm.Subtitle) ? "闹钟提醒" : alarm.Subtitle)
                .GetToastContent();

            try
            {
                // 与 ShowToast 一致：放到后台线程发送，避免卡 UI。
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var toast = new ToastNotification(content.GetXml());
                        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
                    }
                    catch
                    {
                        // 发送失败不影响主流程。
                    }
                });
            }
            catch
            {
                _lblStatus.Text = "发送通知失败";
            }
        }

        /// <summary>发送“未操作自动贪睡”提示 Toast：独立的通知类型，主/副标题在“默认模板…”中自定义，暂不带按钮。</summary>
        private void ShowAutoSnoozeToast(Alarm alarm)
        {
            string title = string.IsNullOrWhiteSpace(_settings.DefaultAutoSnoozeTitle)
                ? $"“{alarm.Title}”已自动贪睡"
                : _settings.DefaultAutoSnoozeTitle;
            string subtitle = string.IsNullOrWhiteSpace(_settings.DefaultAutoSnoozeSubtitle)
                ? $"{_settings.SnoozeMinutes} 分钟后再次提醒"
                : _settings.DefaultAutoSnoozeSubtitle;

            var content = new ToastContentBuilder()
                .AddText(title)
                .AddText(subtitle)
                .GetToastContent();

            try
            {
                // 与 ShowToast 一致：放到后台线程发送，避免卡 UI。
                // 确认期 30 秒后触发 Toast 早已结束，不再需要动画冲突延迟，立即发送。
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var toast = new ToastNotification(content.GetXml());
                        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
                    }
                    catch
                    {
                        // 发送失败不影响主流程。
                    }
                });
            }
            catch
            {
                // 发送失败不影响主流程。
            }
        }

        // ==================== 贪睡 / 激活处理 ====================

        /// <summary>处理 Toast 激活参数（可能是 UI 线程之外的回调，需封送到 UI 线程）。</summary>
        public void ProcessActivation(string? args)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ProcessActivation(args)));
                return;
            }

            // 空参数（点击通知主体/假回调）仅显示主窗体；先于一切 StartsWith 判断，
            // 避免对 null 调用扩展方法（历史顺序隐患：null 检查曾排在 open-target 判断之后）。
            if (args == null || args.Length == 0)
            {
                ShowFromTray();
                return;
            }

            // 短时间去重：同一激活参数在 2 秒内只处理一次（compat 回调与 Shown 可能各派发一次）。
            // 注意不能用“参数内容相同”永久去重——贪睡/打开/结束按钮的参数（snooze:{id} 等）
            // 每次触发都相同，用户再次点击（贪睡循环、再次打开）必须重新处理。
            if (args == _lastActivation && (DateTime.Now - _lastActivationAt).TotalSeconds < 2)
                return;
            _lastActivation = args;
            _lastActivationAt = DateTime.Now;

            Program.Log("ProcessActivation 收到：" + (args ?? "(空)"));

            // 命令行 --open 转发的目标：形如 "cmdopen:<目标>"。
            if (args!.StartsWith("cmdopen:", StringComparison.OrdinalIgnoreCase))
            {
                var target = args.Substring("cmdopen:".Length);
                if (!string.IsNullOrWhiteSpace(target))
                    OpenExternal(target);
                return;
            }

            // Toast“打开”按钮激活参数形如 "open-target:{闹钟Id}|{目标}"：
            // 有闹钟上下文时，单次闹钟打开目标即视为完成（结束并删除）；闹钟已删除则仅打开目标。
            if (args.StartsWith("open-target:", StringComparison.OrdinalIgnoreCase))
            {
                string rest = args.Substring("open-target:".Length);
                int sep = rest.IndexOf('|');
                if (sep >= 0)
                {
                    string idPart = rest.Substring(0, sep);
                    string target = rest.Substring(sep + 1);

                    if (Guid.TryParse(idPart, out var id))
                    {
                        var alarm = _alarms.Find(a => a.Id == id);
                        if (alarm != null)
                        {
                            OpenTarget(alarm); // 打开 + 单次闹钟自动完成
                            return;
                        }
                    }

                    // 闹钟已删除、或 Id 解析失败：只要目标非空就直接打开（原来 Id 解析失败会什么都不做）。
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        OpenExternal(target);
                        return;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(rest))
                {
                    OpenExternal(rest); // 兼容旧格式（纯目标，无 id）
                    return;
                }
                UpdateStatus("打开目标为空");
                return;
            }

            // “打开”按钮激活参数形如 "open:{alarmId}"。
            if (args.StartsWith("open:", StringComparison.OrdinalIgnoreCase))
            {
                var idPart = args.Substring("open:".Length);
                if (Guid.TryParse(idPart, out var id))
                {
                    var alarm = _alarms.Find(a => a.Id == id);
                    if (alarm != null)
                        OpenTarget(alarm);
                    else
                        _lblStatus.Text = "该闹钟已结束，无法打开";
                }
                return;
            }

            // 贪睡按钮激活参数形如 "snooze:{alarmId}"。
            if (args.StartsWith("snooze:", StringComparison.OrdinalIgnoreCase))
            {
                var idPart = args.Substring("snooze:".Length);
                if (Guid.TryParse(idPart, out var id))
                {
                    var alarm = _alarms.Find(a => a.Id == id);
                    if (alarm != null)
                        Snooze(alarm);
                    else
                        _lblStatus.Text = "该闹钟已结束，无法贪睡";
                }
                return;
            }

            // “结束”按钮激活参数形如 "dismiss:{alarmId}"：停止响铃并结束本次提醒。
            if (args.StartsWith("dismiss:", StringComparison.OrdinalIgnoreCase))
            {
                var idPart = args.Substring("dismiss:".Length);
                if (Guid.TryParse(idPart, out var id))
                {
                    var alarm = _alarms.Find(a => a.Id == id);
                    if (alarm != null)
                        EndAlarm(alarm);
                    else
                        _lblStatus.Text = "该闹钟已结束，无需结束";
                }
                return;
            }

            ShowFromTray();
        }

        /// <summary>打开闹钟的“打开目标”。打开目标 = 用户已响应本次提醒 → 判定为完成：
        /// 停止响铃、清除待确认/贪睡；单次闹钟从列表删除，常规闹钟恢复（明天照常提醒）。
        /// 避免 8 秒确认期超时后被误判自动贪睡。</summary>
        private void OpenTarget(Alarm alarm)
        {
            if (string.IsNullOrWhiteSpace(alarm.OpenTarget))
                return;

            OpenExternal(alarm.OpenTarget);

            // 任意闹钟（不限于单次）打开目标后都视为完成本次提醒。
            if (alarm.PendingDeleteUntil.HasValue || IsRinging(alarm.Id))
                EndAlarm(alarm);
        }

        /// <summary>用系统默认程序打开任意目标（网址/文件/文件夹）。供主窗口“打开”按钮与命令行 --open 使用。</summary>
        public void OpenExternal(string target)
        {
            target = target.Trim();

            // 网址无协议时自动补 https://（ShellExecute 对“www.xxx.com”这类输入不可靠）。
            if (LooksLikeUrl(target)
                && !target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                target = "https://" + target;
            }

            UpdateStatus("正在打开：" + target);

            // ShellExecute 放后台线程：目标无关联程序时系统可能弹“打开方式”对话框，
            // 后台执行不会阻塞 UI（UI 被占用是“通知打开/贪睡卡住”的常见原因之一）。
            var finalTarget = target;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var info = new ProcessStartInfo(finalTarget) { UseShellExecute = true };
                    Process.Start(info);
                    Program.Log("OpenExternal 已打开：" + finalTarget);
                    UpdateStatus("已打开：" + finalTarget);
                }
                catch (Exception ex)
                {
                    Program.Log("OpenExternal 失败：" + finalTarget + " —— " + ex.Message);
                    UpdateStatus("打开目标失败：" + ex.Message);
                }
            });
        }

        /// <summary>粗略判断是否为网址：带协议前缀，或形如 example.com（含点、非本地路径）。</summary>
        private static bool LooksLikeUrl(string target)
        {
            if (target.Contains("://"))
                return true;

            return target.Contains('.')
                && !target.Contains('\\')
                && !target.StartsWith("/")
                && !System.IO.Path.IsPathRooted(target);
        }

        /// <summary>贪睡：关闭当前闹钟（停止声音），按设置时长后再次触发。</summary>
        public void Snooze(Alarm alarm)
        {
            Program.Log("Snooze：" + alarm.Title);
            StopSound(alarm.Id); // 只停止被贪睡闹钟的声音，不影响其它正在响的闹钟
            alarm.PendingDeleteUntil = null; // 取消单次闹钟的删除，进入贪睡
            alarm.SnoozeUntil = DateTime.Now.AddMinutes(_settings.SnoozeMinutes);
            RefreshList($"“{alarm.Title}”已贪睡，{_settings.SnoozeMinutes} 分钟后再次提醒");
            // 注意：这里不再 ShowFromTray/置顶——从 Toast 点“贪睡/延迟”不应弹出主界面。
        }

        private void StopSound(Guid id)
        {
            lock (_soundLock)
            {
                if (_players.TryGetValue(id, out var player))
                {
                    try { player.Stop(); } catch { }
                    player.Dispose();
                    _players.Remove(id);
                }
                _ringingIds.Remove(id);
            }
        }

        /// <summary>停止所有闹钟的声音并清空响铃状态（退出/关闭窗口时调用）。</summary>
        private void StopAllSounds()
        {
            lock (_soundLock)
            {
                foreach (var player in _players.Values)
                {
                    try { player.Stop(); } catch { }
                    player.Dispose();
                }
                _players.Clear();
                _ringingIds.Clear();
            }
        }

        /// <summary>该闹钟是否仍在待响铃状态（已标记触发且未被贪睡/结束/删除）。</summary>
        private bool IsRinging(Guid id)
        {
            lock (_soundLock)
            {
                return _ringingIds.Contains(id);
            }
        }

        /// <summary>更新状态栏文本；可从后台线程安全调用（PlaySound / OpenExternal 在后台线程执行）。</summary>
        private void UpdateStatus(string text)
        {
            if (InvokeRequired)
                BeginInvoke(new Action(() => _lblStatus.Text = text));
            else
                _lblStatus.Text = text;
        }

        /// <summary>在状态栏文本后追加；可从后台线程安全调用。</summary>
        private void UpdateStatusAppend(string text)
        {
            if (InvokeRequired)
                BeginInvoke(new Action(() => _lblStatus.Text += text));
            else
                _lblStatus.Text += text;
        }

        // ==================== 窗口 / 托盘图标 ====================

        /// <summary>当前窗口 / 托盘图标；主题切换时替换并释放旧的，避免 GDI 句柄泄漏。</summary>
        private Icon? _windowIcon;
        private Icon? _trayIcon;

        /// <summary>按当前系统主题设置窗口与托盘图标（深色主题用白线条版，浅色主题用深线条版）。</summary>
        private void ApplyThemeIcons()
        {
            bool dark = ThemeManager.IsDark;

            // 先装新图标、再放旧的：反过来的话，_notifyIcon 会短暂指向已释放的 Icon，
            // 期间任何一次 NotifyIcon.UpdateIcon 都会抛 ObjectDisposedException。
            var previousWindowIcon = _windowIcon;
            _windowIcon = AppIcon.Load(dark, SystemInformation.IconSize);
            Icon = _windowIcon;
            previousWindowIcon?.Dispose();

            var previousTrayIcon = _trayIcon;
            _trayIcon = AppIcon.Load(dark, SystemInformation.SmallIconSize);
            _notifyIcon.Icon = _trayIcon;
            previousTrayIcon?.Dispose();
        }

        /// <summary>系统主题切换（ThemeManager.ThemeChanged）→ 重取图标。</summary>
        private void OnThemeChanged()
        {
            if (IsDisposed)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(OnThemeChanged));
                return;
            }

            ApplyThemeIcons();
        }

        /// <summary>释放自己创建的图标。</summary>
        private void DisposeThemeIcons()
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            _windowIcon?.Dispose();
            _windowIcon = null;
        }

        // ==================== 系统托盘 ====================

        private void BuildTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("显示主界面", null, (_, _) => ShowFromTray());
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("退出", null, (_, _) => ExitApp());

            _notifyIcon = new NotifyIcon
            {
                // 托盘图标由 ApplyThemeIcons() 按系统主题设置（深色任务栏用白线条版）。
                Text = "Whisper（运行中）",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 点击“×”不退出程序，而是最小化到托盘继续运行闹钟。
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }

            base.OnFormClosing(e);
        }

        private void HideToTray()
        {
            Hide();
            _notifyIcon.Visible = true;

            // 按设置决定是否显示托盘提示。
            if (_settings.ShowTrayTipOnMinimize)
                _notifyIcon.ShowBalloonTip(2000, "Whisper", "闹钟仍在后台运行，点击托盘图标可恢复。", ToolTipIcon.Info);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _reallyExit = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            StopAllSounds();
            Application.Exit();
        }
    }
}
