using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace WhisperClock
{
    internal static class Program
    {
        /// <summary>Toast 通知使用的应用标识（AUMID），同时用于注册表“开机启动”和单实例互斥体。</summary>
        public const string AppId = "WhisperClock.App";

        private const string PipeName = "WhisperClock.SingleInstancePipe";

        /// <summary>当前进程接收到的 Toast 激活参数（例如贪睡按钮的 "snooze"）。</summary>
        public static string? ActivationArgs { get; set; }

        /// <summary>激活参数到达信号：第二实例等待转发时阻塞在它上面（主订阅者收到参数时 Set）。</summary>
        private static readonly ManualResetEventSlim ActivationReceived = new(false);

        /// <summary>调试日志文件路径（%LocalAppData%\AlarmClock\debug.log），用于定位 Toast 激活问题。</summary>
        private static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlarmClock", "debug.log");

        /// <summary>追加一行调试日志；写失败不影响主流程。UTF-8 带 BOM，方便记事本直接查看中文。</summary>
        private static readonly object LogLock = new();

        internal static void Log(string message)
        {
            lock (LogLock) // 串行化多线程（Toast 回调/管道/UI）写入，避免并发 AppendAllText 交错
            {
                try
                {
                    var dir = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}",
                        new UTF8Encoding(true));
                }
                catch
                {
                    // 忽略日志写入失败。
                }
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Log($"进程启动 PID={Environment.ProcessId} 参数=[{string.Join(" ", args)}]");

            // 兼容残留的旧启动项：--login-signal 已废弃（进桌面判定改由主程序锁屏检测负责）。
            // 若注册表仍残留 WhisperClock.Login / AlarmClock.Login 启动项，本进程必须静默退出——否则它
            // 会作为主实例完整启动并显示界面（且不带 --minimized），而真正的 --minimized 实例反而被
            // 单实例 mutex 拒之门外（“开机无法自动隐藏界面”的根因）。
            if (Array.Exists(args, a => a.Equals("--login-signal", StringComparison.OrdinalIgnoreCase)))
            {
                Log("检测到废弃参数 --login-signal（旧启动项残留），本进程静默退出");
                return;
            }

            // 暗色模式：跟随系统主题并监听切换。
            ThemeManager.Init();

            // 注册表开机启动项带 --minimized 参数：开机时直接隐藏到托盘。
            bool startMinimized = Array.Exists(args,
                a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

            // --open <目标>：命令行直接用系统默认程序打开网址/应用/文件（方案二，不依赖 Toast 激活）。
            string? openTarget = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--open", StringComparison.OrdinalIgnoreCase))
                {
                    openTarget = args[i + 1];
                    break;
                }
            }

            // 不注册 WhisperClock.App 的 AUMID/CLSID：通知与激活由 toolkit 自动注册的 exe 路径 AUMID
            // 负责（主实例运行中点击 Toast 走动态 COM 激活，不依赖该注册表项）。手动注册会造成
            // 系统“通知”设置里出现多余的 WhisperClock 条目（残留）。

            // 订阅 Toast 激活（打开/贪睡按钮）。
            // 关键：主实例在托盘运行中点击 Toast 时，Windows 通过动态 COM 激活把事件直接
            // 派发给已运行的进程（不会启动第二实例），此时 WasCurrentProcessToastActivated()
            // 为 false 但这是真实点击——必须按“参数内容”判断处理，不能按进程启动方式判断。
            // 空参数（点击通知主体/假回调）不处理，避免误弹主界面。
            try
            {
                ToastNotificationManagerCompat.OnActivated += args2 =>
                {
                    string? arg = args2.Argument;
                    ActivationArgs = arg;
                    // 先唤醒第二实例的转发等待（compat 对每次激活只派发一次，必须由本订阅者
                    // 统一收口；第二实例不再注册第二个 handler 去抢参数）。
                    ActivationReceived.Set();

                    Log($"OnActivated 参数=[{arg}] WasToastActivated={ToastNotificationManagerCompat.WasCurrentProcessToastActivated()} 有主窗体={_form != null}");

                    if (_form != null && IsKnownActivation(arg))
                        _form.ProcessActivation(arg);
                    // 第二实例（_form == null）只负责经 WaitForActivationArgs 转发，不在此处理。
                };
            }
            catch (Exception ex)
            {
                Log("OnActivated 订阅失败：" + ex.Message);
                // compat 初始化失败时 Toast 按钮激活不可用，但不影响闹钟主流程。
            }

            // 单实例：若已有实例在运行，把激活参数（如“打开/贪睡”）转发给它后退出。
            using (var mutex = new Mutex(true, @"Local\" + AppId, out bool createdNew))
            {
                if (!createdNew)
                {
                    // 第二实例：等待 Toast 激活参数（compat 的 OnActivated 由它内部的后台
                    // 线程派发，无需本进程运行消息循环），拿到后经命名管道转发给主实例；
                    // 超时则静默退出（此时激活参数已无法送达，但不阻塞、不残留进程）。
                    string? activation = WaitForActivationArgs(TimeSpan.FromSeconds(5));
                    if (!string.IsNullOrEmpty(activation))
                        SendToRunningInstance(activation);

                    // 命令行 --open 目标转发给主实例打开。
                    if (!string.IsNullOrEmpty(openTarget))
                        SendToRunningInstance("cmdopen:" + openTarget);
                    return;
                }

                MainForm? form = new MainForm();
                form.StartMinimized = startMinimized;
                if (!string.IsNullOrEmpty(openTarget))
                    form.OpenExternal(openTarget); // 无主实例时本进程直接打开
                _form = form;

                // 后台命名管道服务器：接收来自第二实例转发的激活参数。
                StartPipeServer(form.ProcessActivation);

                Application.Run(form);
            }
        }

        private static MainForm? _form;

        /// <summary>是否为已知的 Toast 激活参数（打开/贪睡/结束/命令行打开）。空参数视为未知（可能是假回调），不处理。</summary>
        private static bool IsKnownActivation(string? arg)
        {
            return !string.IsNullOrEmpty(arg)
                && (arg.StartsWith("open-target:", StringComparison.OrdinalIgnoreCase)
                    || arg.StartsWith("open:", StringComparison.OrdinalIgnoreCase)
                    || arg.StartsWith("snooze:", StringComparison.OrdinalIgnoreCase)
                    || arg.StartsWith("dismiss:", StringComparison.OrdinalIgnoreCase)
                    || arg.StartsWith("cmdopen:", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 等待 Toast 激活参数到达（第二实例用）。
        /// 参数由主订阅者（OnActivated 回调）写入 ActivationArgs 并 Set 信号，
        /// 本方法只等待信号 + 读取字段，不注册第二个 handler（compat 对每次激活
        /// 只派发一次，重复订阅者拿不到参数）。超时返回 null（不阻塞、不残留进程）。
        /// </summary>
        private static string? WaitForActivationArgs(TimeSpan timeout)
        {
            if (!string.IsNullOrEmpty(ActivationArgs))
                return ActivationArgs;

            ActivationReceived.Wait(timeout);
            Log($"WaitForActivationArgs 结束：{(string.IsNullOrEmpty(ActivationArgs) ? "超时未收到" : "收到=" + ActivationArgs)}");
            return ActivationArgs;
        }

        /// <summary>启动一个后台线程，持续监听其他实例转发过来的激活参数。</summary>
        private static void StartPipeServer(Action<string> onMessage)
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                        server.WaitForConnection();
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        string? message = reader.ReadLine();
                        if (!string.IsNullOrEmpty(message))
                            onMessage(message);
                    }
                    catch
                    {
                        Thread.Sleep(100);
                    }
                }
            })
            { IsBackground = true };

            thread.Start();
        }

        /// <summary>把激活参数发送给正在运行的第一个实例。</summary>
        private static void SendToRunningInstance(string args)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000);
                using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine(args);
                Log("已转发给主实例：" + args);
            }
            catch (Exception ex)
            {
                Log("转发给主实例失败：" + ex.Message);
                // 已有实例未响应时静默失败：不影响新实例正常启动。
            }
        }
    }
}
