using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshDesktop;

/// <summary>
/// DeepSeek Harness WebUI 桌面壳（v9 · 原生窗口 + 托盘 + 提问面板）。
/// - 标准 Windows 窗口：原生标题栏（官方图标 + 深灰标题栏）+ 原生边框与缩放
/// - 页面区 = 整个客户区（WebView2 铺满）
/// - 提问面板：注入页面右侧"◀"按钮，点击向左滑出全部用户提问列表，点击某行跳转
/// - 系统托盘：X 收进托盘（图标存在 = DSH 服务运行中）；托盘菜单可完全退出并关闭所有 dsh 服务
/// - 自动拉起 dsh web；错误/连接中状态以底部悬浮气泡显示
/// - 全程日志 %TEMP%\DshDesktop.log
/// </summary>
public sealed class Form1 : Form
{
    private readonly Uri _target;
    private readonly WebView2 _web = new();
    private readonly Panel _toast = new();
    private readonly Label _toastText = new();
    private readonly Button _retry = new() { Text = "重试连接", FlatStyle = FlatStyle.Flat };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _dotTimer = new() { Interval = 2500 };

    private Process? _spawnedDsh;
    private bool _pollingStarted;
    private bool _webReady;
    private bool _loadingHistory;
    private string _loadedFirstKey = ""; // 已加载完整历史的对话首个用户节点 key

    // 只探测本地地址：必须绕过系统代理（系统代理会把 127.0.0.1 请求转发给代理导致失败）
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    private static readonly string LogPath = ResolveLogPath();

    private static string ResolveLogPath()
    {
        try
        {
            var p = Path.Combine(Path.GetTempPath(), "DshDesktop.log");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            return p;
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "DshDesktop.log");
        }
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响运行 */ }
    }

    public Form1(Uri target)
    {
        _target = target;
        Text = "DeepSeek Harness";
        // 官方 DeepSeek Harness 图标（嵌入 exe 的 app.ico；单文件解压场景下显式提取）
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Icon;
        }
        catch { /* 图标提取失败使用默认 */ }
        // 尺寸自适应工作区（SystemAware 下为会话像素；超出时收缩）
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1360, 900);
        Width = Math.Min(1360, wa.Width - 24);
        Height = Math.Min(900, wa.Height - 24);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        // 原生窗口：系统标题栏 + 边框 + 缩放，全部交给 Windows（标题栏在客户区外，不占页面）
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(24, 24, 27);

        // 页面区 = 整个客户区
        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.FromArgb(24, 24, 27);
        Controls.Add(_web);

        // —— 悬浮气泡（仅未连接时可见）——
        _toast.BackColor = Color.FromArgb(215, 18, 18, 20);
        _toast.Visible = false;

        _toastText.ForeColor = Color.White;
        _toastText.BackColor = Color.Transparent;
        _toastText.Font = new Font("Microsoft YaHei UI", 9F);
        _toastText.TextAlign = ContentAlignment.MiddleLeft;

        _retry.FlatAppearance.BorderColor = Color.FromArgb(90, 120, 255);
        _retry.ForeColor = Color.White;
        _retry.BackColor = Color.FromArgb(60, 70, 140);
        _retry.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 95, 180);
        _retry.Click += async (_, _) => await EnsureServerAndConnectAsync();

        _toast.Controls.Add(_toastText);
        _toast.Controls.Add(_retry);
        Controls.Add(_toast);
        _toast.BringToFront();

        Resize += (_, _) => LayoutToast();
        FormClosing += OnFormClosing;
        Shown += OnShown;

        // 窗口移动/缩放后通知 WebView2 控制器，让浏览器内容实时跟随（原生拖动同样需要）
        LocationChanged += (_, _) => NotifyWebViewParentMoved();
        Resize += (_, _) => NotifyWebViewParentMoved();

        SetupTray();
    }

    // ============ 系统托盘 ============

    private readonly NotifyIcon _tray = new();

    private void SetupTray()
    {
        _tray.Icon = Icon;
        _tray.Text = $"DeepSeek Harness — {_target.Host}:{_target.Port}";
        _tray.Visible = true;
        // 左键单击 → 直接打开桌面版主窗口
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowFromTray();
        };

        // 右键菜单：打开桌面版 / 打开 WEB 版 / 关闭所有 DSH（深灰配色 + 圆角，与标题栏一致）
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ForeColor = Color.FromArgb(215, 215, 222),
            Renderer = new TrayMenuRenderer(),
            Font = new Font("Microsoft YaHei UI", 9F),
            Padding = new Padding(4),
        };
        menu.Items.Add("打开桌面版", null, (_, _) => ShowFromTray());
        menu.Items.Add("打开 WEB 版", null, (_, _) => OpenWeb());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关闭所有 DSH", null, (_, _) => ExitAll());
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = Color.FromArgb(225, 225, 230);
            if (item is ToolStripSeparator) continue;
            item.Padding = new Padding(14, 10, 30, 10); // 项高与左右间距（文字偏移由渲染器控制）
            item.TextAlign = ContentAlignment.MiddleLeft; // 左对齐
        }
        // 布局完成后再套圆角（延迟一小段，避免尺寸未定导致切角错误）
        menu.Opened += (_, _) =>
        {
            var t = new System.Windows.Forms.Timer { Interval = 80 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); RoundMenuWindow(menu.Handle); };
            t.Start();
        };
        menu.SizeChanged += (_, _) =>
        {
            if (menu.Visible) RoundMenuWindow(menu.Handle);
        };
        _tray.ContextMenuStrip = menu;

        Log("托盘图标已创建（左键开窗口；右键：桌面版 / WEB 版 / 关闭所有 DSH）");
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    /// <summary>打开系统默认浏览器访问 dsh web 地址（WEB 版）。</summary>
    private void OpenWeb()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_target.ToString()) { UseShellExecute = true });
            Log($"已打开浏览器：{_target}");
        }
        catch (Exception ex)
        {
            Log($"打开浏览器失败：{ex.Message}");
        }
    }

    /// <summary>托盘退出：关闭所有 dsh web 服务（含外部启动的）并完全退出应用。</summary>
    private void ExitAll()
    {
        Log("托盘退出：开始关闭所有 dsh 服务");
        KillAllDshServices();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    private void KillAllDshServices()
    {
        // 1) 本应用拉起的
        if (_spawnedDsh is { } sp)
        {
            try { sp.Kill(entireProcessTree: true); Log("已关闭本应用拉起的 dsh"); } catch { /* 已退出 */ }
        }
        // 2) 外部启动的（监听目标端口的所有进程）
        foreach (var pid in FindDshWebPids())
        {
            try
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
                Log($"已关闭外部 dsh web 服务（PID={pid}）");
            }
            catch (Exception ex)
            {
                Log($"关闭 dsh web 服务失败（PID={pid}）：{ex.Message}");
            }
        }
    }

    // ============ 对话提问面板（注入页面） ============

    /// <summary>
    /// 在页面右侧注入"◀ 展开"按钮：点击面板向左平行滑出，显示全部用户提问（单行省略号），
    /// 点击某行跳转并自动收回；再点按钮（▶）动画收起。幂等：节点集合未变时不重建。
    /// </summary>
    private const string SyncDotsJs = """
        (() => {
          const CSS = `
            #dsh-dots{position:fixed;right:10px;top:50%;transform:translateY(-50%);z-index:2147483000;
              display:flex;flex-direction:column;align-items:center;gap:5px;pointer-events:none;}
            #dsh-dots-btn{pointer-events:auto;width:20px;height:34px;border:0;border-radius:6px;cursor:pointer;
              background:rgba(255,255,255,.14);color:#fff;font-size:11px;line-height:1;padding:0;
              transition:background .12s,transform .2s ease;}
            #dsh-dots-btn:hover{background:rgba(255,255,255,.22);}
            #dsh-dots.expanded #dsh-dots-btn{transform:rotate(180deg);}
            #dsh-dots-panel{position:absolute;top:50%;right:calc(100% + 12px);
              transform:translateY(-50%) translateX(110%);width:330px;max-height:min(60vh,420px);overflow-y:auto;
              scrollbar-width:none;pointer-events:none;opacity:0;
              background:rgba(20,20,26,.97);border:1px solid rgba(255,255,255,.18);border-radius:8px;padding:6px;
              box-shadow:0 8px 24px rgba(0,0,0,.5);transition:transform .22s ease,opacity .18s ease;}
            #dsh-dots-panel::-webkit-scrollbar{display:none;width:0;}
            #dsh-dots.expanded #dsh-dots-panel{transform:translateY(-50%) translateX(0);opacity:1;pointer-events:auto;}
            .dsh-row{font:12px/1.5 "Microsoft YaHei UI",sans-serif;color:#d8d8e0;padding:5px 8px;border-radius:5px;
              cursor:pointer;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
            .dsh-row:hover{background:rgba(255,255,255,.045);color:#e8e8ee;}
            .dsh-row .idx{color:#8ab4ff;font-weight:600;margin-right:6px;}
          `;
          if (!document.getElementById('dsh-dots-style')) {
            const s = document.createElement('style');
            s.id = 'dsh-dots-style';
            s.textContent = CSS;
            document.head.appendChild(s);
          }
          let host = document.getElementById('dsh-dots');
          if (!host) { host = document.createElement('div'); host.id = 'dsh-dots'; document.body.appendChild(host); }
          let btn = document.getElementById('dsh-dots-btn');
          if (!btn) {
            btn = document.createElement('button');
            btn.id = 'dsh-dots-btn';
            btn.textContent = '◀';
            btn.title = '展开/收起全部提问';
            btn.addEventListener('click', () => {
              const expanded = host.classList.toggle('expanded');
              if (expanded) panel.scrollTop = panel.scrollHeight;
            });
            host.appendChild(btn);
          }
          let panel = document.getElementById('dsh-dots-panel');
          if (!panel) { panel = document.createElement('div'); panel.id = 'dsh-dots-panel'; host.appendChild(panel); }

          const seen = new Set();
          const items = [];
          for (const el of document.querySelectorAll('[data-chat-flow-key]')) {
            if ((el.getAttribute('data-chat-flow-kind') || '') !== 'user') continue;
            const key = el.getAttribute('data-chat-flow-key');
            if (seen.has(key)) continue;
            seen.add(key);
            items.push({ key: key, text: (el.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 200) });
          }

          const sig = items.map(i => i.key).join('|');
          if (panel.dataset.sig === sig) return 'unchanged';
          panel.dataset.sig = sig;
          const panelTop = panel.scrollTop;
          panel.textContent = '';
          const all = [...document.querySelectorAll('[data-chat-flow-key]')];
          const jump = (key) => {
            const t = all.find(e => e.getAttribute('data-chat-flow-key') === key);
            if (t) t.scrollIntoView({ behavior: 'smooth', block: 'start' });
          };
          for (let i = 0; i < items.length; i++) {
            const it = items[i];
            const row = document.createElement('div');
            row.className = 'dsh-row';
            const idx = document.createElement('span');
            idx.className = 'idx';
            idx.textContent = String(i + 1) + '.';
            row.appendChild(idx);
            row.appendChild(document.createTextNode(it.text));
            row.addEventListener('click', () => {
              jump(it.key);
              host.classList.remove('expanded');
            });
            panel.appendChild(row);
          }
          if (host.classList.contains('expanded')) panel.scrollTop = panelTop;
          return 'ok:' + items.length;
        })()
        """;

    /// <summary>探测当前对话首个用户节点 key（判断是否切换了对话）。</summary>
    private const string FirstKeyProbeJs = """
        (() => {
          const el = document.querySelector('[data-chat-flow-kind="user"]');
          return el ? el.getAttribute('data-chat-flow-key') : '';
        })()
        """;

    private async Task SyncDotsAsync()
    {
        if (!_webReady || _web.CoreWebView2 is not { } wv) return;
        try
        {
            var result = await wv.ExecuteScriptAsync(SyncDotsJs);
            var firstKey = (await wv.ExecuteScriptAsync(FirstKeyProbeJs)).Trim('"');
            // 对话切换（首个用户节点变了）→ 重新加载完整历史
            if (firstKey.Length > 0 && firstKey != _loadedFirstKey)
            {
                _loadedFirstKey = firstKey;
                _ = LoadFullHistoryAsync();
            }
            if (result == "\"ok:0\"") _loadedFirstKey = ""; // 当前无对话
        }
        catch (Exception ex)
        {
            Log($"点图同步失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 对话历史是分页渲染的：前端提供"加载更早"按钮（调 loadOlder 加载上一页）。
    /// 反复点击直到按钮消失，完整历史载入 DOM，点图即可覆盖全部提问。
    /// </summary>
    private async Task LoadFullHistoryAsync()
    {
        if (!_webReady || _web.CoreWebView2 is not { } wv) return;
        if (_loadingHistory) return;
        _loadingHistory = true;
        try
        {
            for (var i = 0; i < 30; i++)
            {
                var clicked = await wv.ExecuteScriptAsync("""
                    (() => {
                      const btn = [...document.querySelectorAll('button')].find(b => {
                        const t = b.textContent || '';
                        return t.includes('加载更早') || t.includes('Load older');
                      });
                      if (btn) { btn.click(); return 'clicked'; }
                      return 'none';
                    })()
                    """);
                if (clicked == "\"none\"") break; // 没有更早历史了
                await Task.Delay(1200);
            }
            // 回到底部（最新消息）
            await wv.ExecuteScriptAsync(
                "(() => { const el = document.querySelector('[data-conversation-scroll]'); if (el) el.scrollTop = el.scrollHeight; return 'ok'; })()");
            var result = await wv.ExecuteScriptAsync(SyncDotsJs);
            Log($"历史加载完成：{result} 个用户节点");
        }
        catch (Exception ex)
        {
            Log($"历史加载失败：{ex.Message}");
        }
        finally
        {
            _loadingHistory = false;
        }
    }

    // ============ 原有逻辑 ============

    /// <summary>
    /// WebView2 的浏览器内容在宿主移动时需要显式通知才能实时跟随。
    /// 控制器的公开 API 未暴露在 WinForms 控件上，这里通过反射调用（失败无害）。
    /// </summary>
    private void NotifyWebViewParentMoved()
    {
        try
        {
            var field = typeof(WebView2).GetField("_coreWebView2Controller",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var controller = field?.GetValue(_web);
            controller?.GetType().GetMethod("NotifyParentWindowPositionChanged")?.Invoke(controller, null);
        }
        catch { /* 通知失败不影响拖动 */ }
    }

    // —— 标题栏外观（Win11 DWM）——
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            if (Environment.OSVersion.Version.Build < 22000) return; // 仅 Win11 支持标题栏着色

            // 深灰标题栏 #3C3C3C + 深色模式（白色文字）+ 同色边框
            var darkMode = 1;
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            var caption = ColorTranslator.ToWin32(Color.FromArgb(60, 60, 60));
            DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref caption, sizeof(int));
            Log($"标题栏已设为深灰 #{caption:X8}");
        }
        catch (Exception ex)
        {
            Log($"标题栏着色失败（不影响使用）：{ex.Message}");
        }
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        Log($"窗口已显示，开始初始化 WebView2；Size={Size} ClientSize={ClientSize} DeviceDpi={DeviceDpi}");
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshDesktop", "WebView2");
        try
        {
            await InitWebViewAsync(userData);
        }
        catch (Exception ex)
        {
            Log($"LOCALAPPDATA 目录初始化失败，退回临时目录：{ex.Message}");
            userData = Path.Combine(Path.GetTempPath(), "DshDesktop", "WebView2");
            try
            {
                await InitWebViewAsync(userData);
            }
            catch (Exception ex2)
            {
                Fail($"WebView2 初始化失败：{ex2.Message}");
                return;
            }
        }

        await EnsureServerAndConnectAsync();
    }

    private async Task InitWebViewAsync(string userDataFolder)
    {
        Log($"初始化 WebView2，userDataFolder={userDataFolder}");
        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: userDataFolder);
        _web.CoreWebView2InitializationCompleted += (_, e) =>
            Log($"CoreWebView2InitializationCompleted IsSuccess={e.IsSuccess} err={e.InitializationException?.Message}");
        _web.NavigationStarting += (_, e) =>
            Log($"NavigationStarting uri={e.Uri}");
        _web.NavigationCompleted += (_, e) =>
        {
            Log($"NavigationCompleted uri={_web.Source} IsSuccess={e.IsSuccess} WebErrorStatus={e.WebErrorStatus}");
            if (e.IsSuccess) _ = SyncDotsAsync();
        };
        await _web.EnsureCoreWebView2Async(env);
        _webReady = true;
        Log("WebView2 就绪");

        // 点图定时同步
        _dotTimer.Tick += async (_, _) => await SyncDotsAsync();
        _dotTimer.Start();
        await SyncDotsAsync();
    }

    private async Task EnsureServerAndConnectAsync()
    {
        _retry.Enabled = false;
        _pollTimer.Stop();

        var (reachable, error) = await ProbeAsync();
        Log($"检测 {_target} 可达性 = {reachable}" + (error.Length > 0 ? $"（{error}）" : ""));

        if (reachable)
        {
            HideToast();
            Connect();
            return;
        }

        ShowToast($"无法连接 {_target}：{Summarize(error)}", withRetry: true);

        if (_spawnedDsh is { HasExited: false })
        {
            Log("dsh 已在启动中，继续等待");
            StartPolling();
            return;
        }

        var dshCmd = FindDshCmd();
        if (dshCmd is null)
        {
            var msg = "找不到 dsh 命令。\n请先安装 DeepSeek Harness（npm i -g @deepseek-ai/dsh），或手动启动 dsh web 后再打开本应用。";
            Log(msg);
            Fail(msg);
            return;
        }

        Log($"开始拉起 dsh：{dshCmd} web --port {_target.Port}");
        ShowToast($"dsh web 未运行，正在启动…", withRetry: false);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{dshCmd}\" web --port {_target.Port}\"",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            _spawnedDsh = Process.Start(psi)!;
            _ = Task.Run(async () =>
            {
                try
                {
                    var stdout = await _spawnedDsh.StandardOutput.ReadToEndAsync();
                    Log($"dsh stdout: {stdout}");
                }
                catch { }
                try
                {
                    var stderr = await _spawnedDsh.StandardError.ReadToEndAsync();
                    Log($"dsh stderr: {stderr}");
                }
                catch { }
                try
                {
                    await _spawnedDsh.WaitForExitAsync();
                    Log($"dsh 进程已退出，exit={_spawnedDsh.ExitCode}");
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Log($"拉起 dsh 失败：{ex.Message}");
            Fail($"启动 dsh 失败：{ex.Message}");
            return;
        }

        StartPolling();
    }

    private void StartPolling()
    {
        if (_pollingStarted) return;
        _pollingStarted = true;
        _pollTimer.Tick += async (_, _) =>
        {
            var (ok, err) = await ProbeAsync();
            if (ok)
            {
                _pollTimer.Stop();
                HideToast();
                Log("轮询成功，开始导航");
                Connect();
            }
            else
            {
                ShowToast($"等待 dsh web 启动…（{DateTime.Now:HH:mm:ss}）原因：{Summarize(err)}", withRetry: true);
            }
        };
        _pollTimer.Start();
        Log("开始每秒轮询");
    }

    private async Task<(bool ok, string error)> ProbeAsync()
    {
        try
        {
            using var resp = await _http.GetAsync(_target);
            var ok = resp.StatusCode is System.Net.HttpStatusCode.OK
                or System.Net.HttpStatusCode.Found
                or System.Net.HttpStatusCode.Redirect;
            return (ok, ok ? "" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Summarize(string error)
    {
        if (error.Contains("proxy", StringComparison.OrdinalIgnoreCase)) return "代理拦截（本应用已绕过系统代理，如仍失败请检查代理工具）";
        if (error.Contains("refused", StringComparison.OrdinalIgnoreCase)) return "连接被拒绝（dsh web 未在运行）";
        if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "连接超时";
        return error.Length > 120 ? error[..120] : error;
    }

    private void Connect()
    {
        Log($"导航到 {_target}");
        _web.Source = _target;
    }

    private void ShowToast(string text, bool withRetry)
    {
        _toastText.Text = text;
        _retry.Visible = withRetry;
        _retry.Enabled = withRetry;
        _toast.Visible = true;
        LayoutToast();
        _toast.BringToFront();
    }

    private void HideToast()
    {
        _toast.Visible = false;
    }

    private void LayoutToast()
    {
        const int sidePad = 24;
        const int toastHeight = 44;
        var w = Math.Min(560, ClientSize.Width - sidePad * 2);
        if (w < 240) w = ClientSize.Width - sidePad * 2;
        _toast.Width = w;
        _toast.Height = toastHeight;
        _toast.Location = new Point((ClientSize.Width - w) / 2, ClientSize.Height - toastHeight - 22);
        var buttonW = _retry.Visible ? 76 : 0;
        _toastText.SetBounds(12, 6, w - 24 - buttonW - 8, toastHeight - 12);
        _retry.SetBounds(w - buttonW - 8, 8, buttonW, toastHeight - 16);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        Log($"FormClosing: reason={e.CloseReason}");
        // 窗口 X / Alt+F4 → 收进托盘（服务保持运行，托盘图标即运行指示）
        // 完全退出请用托盘菜单「退出（关闭所有 dsh 服务）」
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            Log("窗口关闭 → 已收进托盘（dsh 服务保持运行）");
            return;
        }

        // 真正的退出（托盘菜单 / 系统关机）：清理
        _pollTimer.Stop();
        _dotTimer.Stop();
        _tray.Visible = false;
        Log("应用退出");
    }

    /// <summary>通过 netstat 找到监听目标端口的所有进程 PID（仅本机回环监听）。</summary>
    private List<int> FindDshWebPids()
    {
        var pids = new List<int>();
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            var needle = $"{_target.Host}:{_target.Port}";
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid) && !pids.Contains(pid))
                    pids.Add(pid);
            }
        }
        catch (Exception ex)
        {
            Log($"查找 dsh web 进程失败：{ex.Message}");
        }
        return pids;
    }

    private void Fail(string message)
    {
        ShowToast(message, withRetry: false);
        MessageBox.Show(message, "DshDesktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string? FindDshCmd()
    {
        var appDataNpm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "dsh.cmd");
        if (File.Exists(appDataNpm)) return appDataNpm;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            var candidate = Path.Combine(dir.Trim(), "dsh.cmd");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ============ 托盘菜单样式（深灰 + 圆角，与标题栏一致） ============

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    /// <summary>给菜单窗口套一个圆角区域（四个角轻微圆润）。</summary>
    private static void RoundMenuWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return;
            var rgn = CreateRoundRectRgn(0, 0, w, h, 12, 12);
            if (rgn != IntPtr.Zero) SetWindowRgn(hwnd, rgn, true);
        }
        catch { /* 圆角失败不影响菜单 */ }
    }

    /// <summary>托盘右键菜单渲染器：滴答深色风 —— 纯平深底、白字、圆角悬停、无渐变。</summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color TextColor = Color.FromArgb(225, 225, 230);
        private static readonly Color Bar = Color.FromArgb(38, 38, 42);
        private static readonly Color Hover = Color.FromArgb(58, 58, 66);
        private const int TextDownShift = 6; // 文字相对布局位置下移的像素数

        public TrayMenuRenderer() : base(new TrayMenuColors()) { }

        // 菜单整体：纯色填充，不要系统渐变/边框
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Bar);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        // 悬停项：圆角高亮块（微内缩），贴近滴答的悬浮效果
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;
            using var path = RoundedRectPath(e.Item.ContentRectangle, 7);
            using var b = new SolidBrush(Hover);
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // 手动绘制文字：在布局矩形基础上加固定下偏移，保证可见效果（不依赖 padding）
            var text = e.Text;
            if (string.IsNullOrEmpty(text)) return;
            var rect = e.TextRectangle;
            rect.Offset(0, TextDownShift);
            TextRenderer.DrawText(e.Graphics, text, e.TextFont, rect, TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(58, 58, 66));
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(Rectangle r, int radius)
        {
            var d = radius * 2;
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>颜色表：全部收敛为同一深色，杜绝任何渐变残留。</summary>
    private sealed class TrayMenuColors : ProfessionalColorTable
    {
        private static readonly Color Bar = Color.FromArgb(38, 38, 42);

        public override Color ToolStripDropDownBackground => Bar;
        public override Color ToolStripGradientBegin => Bar;
        public override Color ToolStripGradientMiddle => Bar;
        public override Color ToolStripGradientEnd => Bar;
        public override Color MenuStripGradientBegin => Bar;
        public override Color MenuStripGradientEnd => Bar;
        public override Color StatusStripGradientBegin => Bar;
        public override Color StatusStripGradientEnd => Bar;
        public override Color ImageMarginGradientBegin => Bar;
        public override Color ImageMarginGradientMiddle => Bar;
        public override Color ImageMarginGradientEnd => Bar;
        public override Color MenuItemSelected => Bar;      // 悬停由渲染器自绘
        public override Color MenuItemBorder => Bar;
        public override Color MenuBorder => Bar;            // 无边框观感
        public override Color SeparatorDark => Color.FromArgb(58, 58, 66);
        public override Color SeparatorLight => Bar;
        public override Color CheckBackground => Bar;
        public override Color CheckSelectedBackground => Bar;
        public override Color ButtonSelectedGradientBegin => Bar;
        public override Color ButtonSelectedGradientMiddle => Bar;
        public override Color ButtonSelectedGradientEnd => Bar;
    }
}
