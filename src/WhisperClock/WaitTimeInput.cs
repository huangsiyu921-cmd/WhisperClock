using System;
using System.Windows.Forms;

namespace WhisperClock
{
    /// <summary>
    /// “响铃等待时间”输入组合（数值框 + 秒/分钟单位下拉）的读写帮助器。
    /// 闹钟编辑框（AlarmEditForm）与默认模板（TemplateForm）共用，保证单位换算规则一致。
    /// 对外一律以“秒”为单位存取，内部按所选单位决定 NumericUpDown 的范围与显示值。
    /// </summary>
    internal static class WaitTimeInput
    {
        private const int UnitSeconds = 0;
        private const int UnitMinutes = 1;

        private const decimal MaxSecondsValue = 3600m;
        private const decimal MaxMinutesValue = 60m; // 与 MaxSecondsValue 对齐：上限统一为 1 小时

        /// <summary>初始化下拉项与取值范围，并挂上“切换单位时按比例换算”的处理。</summary>
        public static void Bind(NumericUpDown num, ComboBox unit)
        {
            num.DecimalPlaces = 0;

            unit.DropDownStyle = ComboBoxStyle.DropDownList;
            unit.Items.Clear();
            unit.Items.AddRange(new object[] { "秒", "分钟" });
            unit.Tag = UnitSeconds; // 记录上一次的单位，供切换时换算
            unit.SelectedIndex = UnitSeconds;
            unit.SelectedIndexChanged += (_, _) => OnUnitChanged(num, unit);

            ApplyRange(num, unit);
        }

        /// <summary>读取当前设置，返回秒数。</summary>
        public static double GetSeconds(NumericUpDown num, ComboBox unit)
            => (double)num.Value * (unit.SelectedIndex == UnitMinutes ? 60 : 1);

        /// <summary>写入秒数（自动挑单位：整分钟且 ≥ 60 秒用“分钟”，否则用“秒”）。</summary>
        public static void SetSeconds(NumericUpDown num, ComboBox unit, double seconds)
        {
            if (seconds < 1)
                seconds = 1;

            bool useMinutes = seconds >= 60 && Math.Abs(seconds % 60) < 0.001;
            int index = useMinutes ? UnitMinutes : UnitSeconds;

            // 先同步 Tag，避免 SelectedIndex 变更触发 OnUnitChanged 再做一次换算。
            unit.Tag = index;
            unit.SelectedIndex = index;

            WriteValue(num, unit, seconds);
        }

        private static void OnUnitChanged(NumericUpDown num, ComboBox unit)
        {
            int current = unit.SelectedIndex;
            int last = unit.Tag is int tag ? tag : UnitSeconds;
            if (current == last)
                return;

            unit.Tag = current;

            // 按旧单位读出秒数，再按新单位取整写入（避免出现 0 或小数）。
            double seconds = (double)num.Value * (last == UnitMinutes ? 60 : 1);
            double rounded = current == UnitMinutes
                ? Math.Max(1, Math.Round(seconds / 60)) * 60
                : Math.Max(1, Math.Round(seconds));
            WriteValue(num, unit, rounded);
        }

        private static void WriteValue(NumericUpDown num, ComboBox unit, double seconds)
        {
            ApplyRange(num, unit);

            bool minutes = unit.SelectedIndex == UnitMinutes;
            decimal value = minutes ? (decimal)(seconds / 60) : (decimal)seconds;

            if (value < num.Minimum)
                value = num.Minimum;
            if (value > num.Maximum)
                value = num.Maximum;

            num.Value = value;
        }

        private static void ApplyRange(NumericUpDown num, ComboBox unit)
        {
            bool minutes = unit.SelectedIndex == UnitMinutes;
            num.Minimum = 1;
            num.Maximum = minutes ? MaxMinutesValue : MaxSecondsValue;
        }
    }
}
