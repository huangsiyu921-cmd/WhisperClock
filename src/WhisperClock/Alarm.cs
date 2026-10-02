using System;
using System.Text.Json.Serialization;

namespace WhisperClock
{
    /// <summary>闹钟提醒模式：普通（Toast 交互 + 确认期 + 贪睡）/ 仅通知（弹无按钮 Toast + 直接播/打开，不贪睡，直接结束）/ 纯提醒（beta，无 Toast，直接播/打开，不贪睡，直接结束）。</summary>
    public enum AlarmMode
    {
        Normal = 0,
        NotifyOnly = 1,
        RemindOnly = 2,
    }

    /// <summary>一个闹钟的完整配置。列表保存在 %LocalAppData%\AlarmClock\alarms.json。</summary>
    public class Alarm
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>每天触发的时间（时分秒）。</summary>
        public TimeSpan TimeOfDay { get; set; } = new TimeSpan(7, 0, 0);

        /// <summary>随机提前/延后秒数（int）：闹钟在设定时间 ± 该秒数范围内随机触发；0 = 不随机，到点即触发。</summary>
        public int RandomOffsetSeconds { get; set; }

        /// <summary>本次（当天）计划随机触发时间；触发后清空，程序重启后丢失并重新随机。</summary>
        [JsonIgnore]
        public DateTime? RandomTriggerAt { get; set; }

        /// <summary>Toast 通知主标题。</summary>
        public string Title { get; set; } = "⏰ 闹钟时间到！";

        /// <summary>Toast 通知副标题。</summary>
        public string Subtitle { get; set; } = "该起床啦！";

        /// <summary>贪睡提醒 Toast 的主标题；为空时使用“默认模板…”中的贪睡主标题。</summary>
        public string? SnoozeTitle { get; set; }

        /// <summary>贪睡提醒 Toast 的副标题；为空时使用“默认模板…”中的贪睡副标题。</summary>
        public string? SnoozeSubtitle { get; set; }

        /// <summary>铃声文件路径（.wav）。为空则仅发送通知。</summary>
        public string? AudioPath { get; set; }

        /// <summary>true = 循环播放（PlayLooping）；false = 单次播放（Play）。</summary>
        public bool Loop { get; set; }

        /// <summary>
        /// 等待时长（秒）：触发后等这么久就走分支——普通模式到点自动贪睡（进中间态1），
        /// 仅通知 / 纯提醒到点直接结束（单次闹钟顺带删除）。
        /// &lt;= 0 表示未设置，运行时按播放模式回退到“默认模板…”里的默认值
        /// （单次播放 20 秒、循环播放 60 秒）。音频怎么播由播放模式决定，不受该值截断。
        /// </summary>
        public double PlayWaitSeconds { get; set; }

        /// <summary>单次闹钟：触发一次（含贪睡重触发）后自动从列表删除。</summary>
        public bool OneShot { get; set; }

        /// <summary>登录时触发：程序启动（开机自启）时触发一次，不按设定时间；贪睡/确认期逻辑照常。</summary>
        public bool TriggerAtLogin { get; set; }

        /// <summary>旧版“纯提醒”开关（0.2.2alpha）；保留仅用于旧数据迁移，新代码请用 Mode。</summary>
        public bool RemindOnly { get; set; }

        /// <summary>闹钟提醒模式：普通（默认）/ 仅通知 / 纯提醒（beta）。</summary>
        public AlarmMode Mode { get; set; } = AlarmMode.Normal;

        /// <summary>“打开”目标：网址（http/https）或本地文件/文件夹路径。设置后 Toast 通知上出现“打开”按钮（在贪睡左侧）。</summary>
        public string? OpenTarget { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>最近一次实际触发所在的分钟（yyyyMMddHHmm），防止同一分钟重复触发。</summary>
        [JsonIgnore]
        public string? LastTriggeredMinute { get; set; }

        /// <summary>贪睡结束时间；为 null 表示当前没有贪睡等待。</summary>
        [JsonIgnore]
        public DateTime? SnoozeUntil { get; set; }

        /// <summary>
        /// 触发后的“待确认”中间态截止时间（触发后 30 秒，见 MainForm.PendingDeleteSeconds）。期间用户可在
        /// Toast 上贪睡/结束、在主界面“结束”；超时未操作则自动贪睡（单次闹钟同样循环提醒，
        /// 只有用户点“结束”/“打开”才删除或恢复）。
        /// </summary>
        [JsonIgnore]
        public DateTime? PendingDeleteUntil { get; set; }
    }
}
