using System;
using System.IO;
using System.Text.Json;

namespace WhisperClock
{
    /// <summary>全局设置：贪睡时长、最小化提示、新建闹钟默认模板。保存在 %LocalAppData%\AlarmClock\settings.json。</summary>
    public class AppSettings
    {
        /// <summary>贪睡时长（分钟），默认 5。</summary>
        public int SnoozeMinutes { get; set; } = 5;

        /// <summary>最小化到托盘时是否显示气球提示，0.1.2 起默认关闭（默认安静）。</summary>
        public bool ShowTrayTipOnMinimize { get; set; }

        /// <summary>闹钟触发后先弹 Toast，延迟该秒数（支持小数）再播放音频，默认 3 秒。</summary>
        public double AudioDelaySeconds { get; set; } = 3;

        // ---- 新建闹钟默认模板 ----
        public string DefaultTitle { get; set; } = "⏰ 闹钟时间到！";
        public string DefaultSubtitle { get; set; } = "该起床啦！";
        public bool DefaultLoop { get; set; }
        public bool DefaultOneShot { get; set; }

        /// <summary>新建闹钟的默认等待时长（秒）——“单次播放”用，默认 20 秒。</summary>
        public double DefaultPlayWaitSecondsOnce { get; set; } = 20;

        /// <summary>新建闹钟的默认等待时长（秒）——“循环播放”用，默认 60 秒（1 分钟）。</summary>
        public double DefaultPlayWaitSecondsLoop { get; set; } = 60;

        /// <summary>贪睡提醒 Toast 的默认主标题（在“默认模板…”中自定义）。</summary>
        public string DefaultSnoozeTitle { get; set; } = "⏰ 贪睡结束！";

        /// <summary>贪睡提醒 Toast 的默认副标题（在“默认模板…”中自定义）。</summary>
        public string DefaultSnoozeSubtitle { get; set; } = "该起床啦！";

        /// <summary>“未操作自动贪睡”提示 Toast 的默认主标题（在“默认模板…”中自定义）。</summary>
        public string DefaultAutoSnoozeTitle { get; set; } = "⏰ 已自动贪睡";

        /// <summary>“未操作自动贪睡”提示 Toast 的默认副标题（在“默认模板…”中自定义）。</summary>
        public string DefaultAutoSnoozeSubtitle { get; set; } = "稍后将再次提醒";

        /// <summary>Toast“打开”按钮文字。</summary>
        public string DefaultOpenButton { get; set; } = "打开";

        /// <summary>Toast“贪睡/延迟”按钮文字；{0} 会被替换为贪睡分钟数。</summary>
        public string DefaultSnoozeButton { get; set; } = "延迟 {0} 分钟";

        /// <summary>Toast“结束”按钮文字。</summary>
        public string DefaultDismissButton { get; set; } = "结束";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlarmClock", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                    if (settings != null)
                        return settings;
                }
            }
            catch
            {
                // 设置损坏时回退到默认值。
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 保存失败不影响运行。
            }
        }

        /// <summary>恢复为最初默认值。</summary>
        public void ResetToFactory()
        {
            SnoozeMinutes = 5;
            AudioDelaySeconds = 3;
            DefaultTitle = "⏰ 闹钟时间到！";
            DefaultSubtitle = "该起床啦！";
            DefaultLoop = false;
            DefaultOneShot = false;
            DefaultPlayWaitSecondsOnce = 20;
            DefaultPlayWaitSecondsLoop = 60;
            ShowTrayTipOnMinimize = false;
            DefaultSnoozeTitle = "⏰ 贪睡结束！";
            DefaultSnoozeSubtitle = "该起床啦！";
            DefaultAutoSnoozeTitle = "⏰ 已自动贪睡";
            DefaultAutoSnoozeSubtitle = "稍后将再次提醒";
            DefaultOpenButton = "打开";
            DefaultSnoozeButton = "延迟 {0} 分钟";
            DefaultDismissButton = "结束";
        }
    }
}
