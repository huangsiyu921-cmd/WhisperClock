using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WhisperClock
{
    /// <summary>暗色模式支持：跟随 Windows 系统主题（深色/浅色），并为窗口启用暗色标题栏。</summary>
    public static class ThemeManager
    {
        private static readonly Color DarkBack = Color.FromArgb(32, 32, 32);   // 窗体 / 分组背景
        private static readonly Color DarkInput = Color.FromArgb(45, 45, 48);  // 输入框 / 列表背景
        private static readonly Color DarkText = Color.FromArgb(230, 230, 230);

        public static bool IsDark { get; private set; }

        /// <summary>在程序入口调用一次：检测当前系统主题，并监听主题切换自动重刷界面。</summary>
        public static void Init()
        {
            IsDark = IsSystemDark();

            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General)
                {
                    IsDark = IsSystemDark();
                    foreach (Form form in Application.OpenForms)
                        Apply(form);
                }
            };
        }

        /// <summary>读取系统“应用使用深色模式”设置。</summary>
        private static bool IsSystemDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>对窗体及其全部子控件应用当前主题，并设置暗色标题栏。</summary>
        public static void Apply(Form form)
        {
            if (form == null)
                return;

            if (IsDark)
                ApplyDark(form);
            else
                ApplyLight(form);

            SetDarkTitleBar(form.Handle, IsDark);
        }

        private static void ApplyDark(Control control)
        {
            switch (control)
            {
                case TextBox textBox:
                    textBox.BackColor = DarkInput;
                    textBox.ForeColor = DarkText;
                    break;
                case ListView listView:
                    listView.BackColor = DarkInput;
                    listView.ForeColor = DarkText;
                    break;
                case Form or GroupBox or Panel:
                    control.BackColor = DarkBack;
                    control.ForeColor = DarkText;
                    break;
                case Label or CheckBox or RadioButton:
                    control.ForeColor = DarkText;
                    break;
                default:
                    break; // Button / DateTimePicker 等由系统主题绘制，不手动改色
            }

            foreach (Control child in control.Controls)
                ApplyDark(child);
        }

        private static void ApplyLight(Control control)
        {
            switch (control)
            {
                case TextBox textBox:
                    textBox.BackColor = SystemColors.Window;
                    textBox.ForeColor = SystemColors.WindowText;
                    break;
                case ListView listView:
                    listView.BackColor = SystemColors.Window;
                    listView.ForeColor = SystemColors.WindowText;
                    break;
                case Form or GroupBox or Panel:
                    control.BackColor = SystemColors.Control;
                    control.ForeColor = SystemColors.ControlText;
                    break;
                case Label or CheckBox or RadioButton:
                    control.ForeColor = SystemColors.ControlText;
                    break;
                default:
                    break;
            }

            foreach (Control child in control.Controls)
                ApplyLight(child);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        /// <summary>启用/关闭窗口暗色标题栏（Windows 10 1809 / build 17763 及以上支持）。</summary>
        private static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return;

            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
        }
    }
}
