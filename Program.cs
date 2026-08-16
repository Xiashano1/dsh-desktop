namespace DshDesktop;

static class Program
{
    /// <summary>
    /// 入口：DshDesktop.exe [--url http://127.0.0.1:3080]
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // 在任何 UI 初始化之前就开始记录，保证崩溃也有日志
        Form1.Log($"== DshDesktop v3 启动 args=[{string.Join(' ', args)}] ==");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Form1.Log($"未处理异常: {e.ExceptionObject}");
        Application.ThreadException += (_, e) =>
            Form1.Log($"UI 线程异常: {e.Exception}");

        ApplicationConfiguration.Initialize();

        var url = "http://127.0.0.1:3080";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--url" or "-u")
            {
                url = args[i + 1];
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme != "http")
        {
            MessageBox.Show(
                $"无效的 URL: {url}\n\n用法: DshDesktop.exe [--url http://127.0.0.1:3080]",
                "DshDesktop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new Form1(target));
    }
}
