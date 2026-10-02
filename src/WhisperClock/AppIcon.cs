using System;
using System.Drawing;
using System.Windows.Forms;

namespace WhisperClock
{
    /// <summary>
    /// 程序图标：按“当前系统主题”从嵌入资源加载——深色主题用白线条版（app-dark.ico），
    /// 浅色主题用深线条版（app.ico）。两版形状相同，保证深/浅任务栏（含浅色任务栏）下都看得清。
    /// 资源缺失时退回 exe 内置图标（csproj 的 ApplicationIcon）。
    /// </summary>
    internal static class AppIcon
    {
        /// <summary>深色主题（深色任务栏/标题栏）用的图标资源名：白色线条。</summary>
        private const string DarkResource = "WhisperClock.Assets.app-dark.ico";

        /// <summary>浅色主题用的图标资源名：深色线条（与 exe 的 ApplicationIcon 同一份设计）。</summary>
        private const string LightResource = "WhisperClock.Assets.app.ico";

        /// <summary>按主题加载指定尺寸的图标。调用方负责 Dispose（每次切换主题都会新建一个）。</summary>
        public static Icon Load(bool dark, Size size)
        {
            string resource = dark ? DarkResource : LightResource;

            try
            {
                using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(resource);
                if (stream != null)
                    return new Icon(stream, size);
            }
            catch
            {
                // 资源缺失/损坏时退回 exe 图标。
            }

            return LoadFromExecutable(size);
        }

        /// <summary>兜底：直接从 exe 的图标资源取（尺寸不精确时由系统缩放）。</summary>
        private static Icon LoadFromExecutable(Size size)
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var fromFile = new Icon(exe, size);
                    if (fromFile.Width > 0)
                        return fromFile;
                    fromFile.Dispose();
                }
            }
            catch
            {
                // 忽略，走下面的默认图标。
            }

            // SystemIcons 是共享实例，克隆一份交给调用方释放，避免误 Dispose 掉系统图标。
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
