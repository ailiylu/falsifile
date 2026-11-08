using System.Runtime.InteropServices;
using System.Text;
using DeskTidy.Core;
using DeskTidy.Diagnostics;
using DeskTidy.Interop;
using DeskTidy.UI;
using static DeskTidy.Interop.NativeMethods;

namespace DeskTidy;

internal static class Program
{
    private static OverlayWindow? _overlay;
    private static Guardian? _guardian;
    private static volatile bool _exitRequested;
    private static uint _uiThreadId;

    [STAThread]
    private static int Main(string[] args)
    {
        bool selftest = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        bool wantConsole = args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase));
        int staySeconds = ParseStay(args);

        if (wantConsole)
            AttachConsole();

        AppLog.Init();
        AppLog.Info($"启动 args=[{string.Join(' ', args)}]");

        // 预览模式：只生成玻璃贴图 PNG，不建窗口、不碰桌面。用于定视觉。
        if (args.Any(a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase)))
        {
            var outDir = ParseValue(args, "--out")
                         ?? Path.Combine(AppPaths.DataDir, "preview");
            var iconPath = ParseValue(args, "--icon") ?? PickSampleFolder();
            return DeskTidy.Visual.Preview.RenderAll(outDir, iconPath);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Exception("UnhandledException", (Exception)e.ExceptionObject);
            TryEmergencyCleanup();
        };

        try
        {
            if (selftest)
                return SelfTest.Run();

            // 命令行：干跑 / 立即清理 / 立即还原（不建窗口，便于脚本化验证）
        if (args.Any(a => a.Equals("--clean-dryrun", StringComparison.OrdinalIgnoreCase)))
            return RunDryRun(args);
        if (args.Any(a => a.Equals("--clean-now", StringComparison.OrdinalIgnoreCase)))
            return RunCleanNow(args);
        if (args.Any(a => a.Equals("--restore-now", StringComparison.OrdinalIgnoreCase)))
            return RunRestoreNow(args);

        // 默认行为：一次性开关（清理 / 还原），全程无窗口、无动画。
        if (args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase)))
            return RunInstallShortcut(args);

        return RunToggle(args);
        }
        catch (Exception ex)
        {
            AppLog.Exception("Main", ex);
            return 1;
        }
        finally
        {
            try { _guardian?.Dispose(); } catch { }
            try { _overlay?.Dispose(); } catch { }
            SingleInstance.Release();
            AppLog.Info("退出");
        }
    }

    /// <summary>统一退出入口：先摘掉窗口，再让消息循环收手。</summary>
    private static void RequestExit()
    {
        _exitRequested = true;
        try { _overlay?.Hide(); } catch { }
        try
        {
            if (_overlay is not null && _overlay.Handle != IntPtr.Zero)
                PostMessageW(_overlay.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
        try
        {
            if (_uiThreadId != 0)
                PostThreadMessageW(_uiThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static void OnHotKey(int id)
    {
        switch (id)
        {
            case 9001:
                AppLog.Info("热键 Ctrl+Alt+Q：强制退出");
                RequestExit();
                break;
            case 9002:
                AppLog.Info("热键：紧急还原");
                if (!_animating) ForceRestore();
                break;
        }
    }

    private static void OnDisplayChanged()
    {
        AppLog.Info("收到显示设置变化（分辨率/DPI/主题）");
        try { _overlay?.MoveToWorkArea(); } catch (Exception ex) { AppLog.Exception("MoveToWorkArea", ex); }
    }

    private static void RunMessageLoop()
    {
        while (!_exitRequested)
        {
            int r = GetMessageW(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private static void RunForSeconds(int seconds)
    {
        var tid = _uiThreadId;
        _ = Task.Run(async () =>
        {
            await Task.Delay(seconds * 1000);
            PostThreadMessageW(tid, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        });
        RunMessageLoop();
    }
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static DeskTidy.Shell.DesktopViewReader? _reader;
    private static BallWindow? _ball;
    private static string _anchorPath = "";
    private static DeskTidy.Visual.Bgra? _wallpaper;
    private static DeskTidy.Visual.Bgra? _iconBmp;
    private static DeskTidy.Visual.GlassTile? _tile;
    private static int _spriteCx = int.MinValue, _spriteCy = int.MinValue;

    private static DeskTidy.UI.FlyAnimator? _anim;
    private static bool _animating;
    private static long _lastTickMs;
    private static int _animPhase; // 0=idle 1=suck 2=restore
    private static string _rootOverride = "";
    private static Config _cfg = new();

    private static void StartLiveMode(string[] args)
    {
        _cfg = Config.Load();
        var anchorArg = ParseValue(args, "--anchor");
        if (!string.IsNullOrWhiteSpace(anchorArg)) _cfg.AnchorPath = anchorArg;
        _anchorPath = Path.GetFullPath(_cfg.ResolveAnchor());
        _cfg.Save();

        _rootOverride = ParseValue(args, "--root") ?? "";

        _reader = DeskTidy.Shell.DesktopViewReader.TryOpen(out var err);
        if (_reader is null) AppLog.Error($"桌面视图打不开：{err}");

        // 球的尺寸：图标 48 DIP（与桌面图标同规格），×DPI → 96px
        float s = _overlay!.Dpi / 96f;
        int tilePx = (int)Math.Round(
            DeskTidy.Shell.DesktopViewReader.ReadIconSizePx() * s);
        int pad = Math.Max(6, (int)Math.Round(tilePx * 0.42f));
        int sprite = tilePx + pad * 2;

        _ball = BallWindow.Create(sprite, tilePx, pad);
        _ball.Clicked += OnAnchorClicked;
        // 双击不再绑定"打开文件夹"：它和"单击切换"抢同一个手势，容易误触。
        // 打开收纳夹走右键。
        _ball.RightClicked += OpenAnchorFolder;
        _ball.Dragged += OnBallDragged;

        _anim = new DeskTidy.UI.FlyAnimator(_overlay.Host);

        // 画布平时隐藏，只在动画期间出现 —— 常驻的只有那个小球
        _overlay.Hide();

        LoadAssets();

        var wa = _overlay.WorkArea;
        int left = _cfg.BallX ?? (wa.Right - sprite - (int)(24 * s));
        int top = _cfg.BallY ?? (wa.Top + (int)(wa.Height * 0.34));
        (left, top) = ClampBall(left, top);
        _ball.PlaceAtDesktopBand();
        _ball.ShowAt(left, top);
        _ball.PlaceAbove(_overlay.Handle);
        RenderBall();

        _overlay.Tick += EnsureDesktopBand;
        _overlay.AnimTick += OnAnimTick;
        _overlay.StartTick(1000);
        _overlay.DisplayChanged += () =>
        {
            LoadAssets();
            var (l, t) = ClampBall(_ball.Position.Left, _ball.Position.Top);
            _ball.ShowAt(l, t);
            _ball.PlaceAbove(_overlay.Handle);
            RenderBall();
        };

        AppLog.Info($"正式模式 anchor={_anchorPath} ball={tilePx}px sprite={sprite}px dpi={_overlay.Dpi}");
    }

    private static (int Left, int Top) ClampBall(int left, int top)
    {
        if (_overlay is null || _ball is null) return (left, top);
        var wa = _overlay.WorkArea;
        int margin = 4;
        left = Math.Clamp(left, wa.Left - _ball.Pad, wa.Right - _ball.Size + _ball.Pad);
        top = Math.Clamp(top, wa.Top - _ball.Pad, wa.Bottom - _ball.Size + _ball.Pad);
        _ = margin;
        return (left, top);
    }

    private static void OnBallDragged(int left, int top)
    {
        if (_ball is null) return;
        var (l, t) = ClampBall(left, top);
        _ball.ShowAt(l, t);
        _ball.PlaceAbove(_overlay!.Handle);

        // 松手后重算一次玻璃：脚下的壁纸换了一块
        RenderBall();

        _cfg.BallX = l;
        _cfg.BallY = t;
        _cfg.Save();
        AppLog.Info($"球移动到 ({l},{t})");
    }

    private static void LoadAssets()
    {
        if (_overlay is null) return;
        _overlay.RefreshWorkArea();
        var mon = _overlay.MonitorRect;
        var wall = DeskTidy.Visual.Wallpaper.Load(mon.Width, mon.Height);
        _wallpaper = wall.Image;
        _iconBmp = DeskTidy.Visual.ShellIcon.Load(_anchorPath, 256) ?? _iconBmp;
        _tile = null;
        _spriteCx = int.MinValue;
        AppLog.Info($"素材 wallpaper={wall.SourceDescription} {_wallpaper.W}x{_wallpaper.H} " +
                    $"icon={(_iconBmp is null ? "null" : $"{_iconBmp.W}x{_iconBmp.H}")}");
    }

    /// <summary>按球当前位置重算一次玻璃贴图。只在启动/拖动结束/换壁纸时调用。</summary>
    private static void RenderBall()
    {
        if (_overlay is null || _ball is null || _wallpaper is null) return;
        try
        {
            var (left, top) = _ball.Position;
            int cx = left + _ball.Size / 2;
            int cy = top + _ball.Size / 2;

            float s = _overlay.Dpi / 96f;
            int iconPx = _ball.TilePx;
            var mon = _overlay.MonitorRect;

            _tile = DeskTidy.Visual.GlassTile.Render(
                _wallpaper, cx - mon.Left, cy - mon.Top, s, _iconBmp,
                iconPx / s, glyphRatio: 0.90f, shadowScale: 0.35f);

            _ball.SetSprite(_tile.Sprite);
            _spriteCx = cx;
            _spriteCy = cy;
        }
        catch (Exception ex)
        {
            AppLog.Exception("RenderBall", ex);
        }
    }

    /// <summary>
    /// Z 序维护：Explorer 在"显示桌面"或点桌面时会把 Progman 抬到我们上面，
    /// 那样玻璃就被壁纸盖住了。检测到就重新压回桌面层（只在被破坏时才动，避免闪烁）。
    /// </summary>
    private static void EnsureDesktopBand()
    {
        if (_overlay is null || _ball is null) return;

        IntPtr progman = DeskTidy.Diagnostics.ZOrderProbe.FindDesktopWindow();
        if (progman == IntPtr.Zero) return;

        int ballIndex = -1, progIndex = -1;
        var h = GetTopWindow(IntPtr.Zero);
        int i = 0;
        while (h != IntPtr.Zero && i < 4096)
        {
            if (h == _ball.Handle) ballIndex = i;
            else if (h == progman) progIndex = i;
            if (ballIndex >= 0 && progIndex >= 0) break;
            h = GetWindow(h, GW_HWNDNEXT);
            i++;
        }

        if (progIndex >= 0 && (ballIndex < 0 || progIndex < ballIndex))
        {
            AppLog.Info($"Z 序被破坏（progman=#{progIndex} ball=#{ballIndex}），重新压回桌面层");
            _ball.PlaceAtDesktopBand();
            _ball.PlaceAbove(_overlay.Handle);
        }
    }

    private static long _lastToggleMs;

    private static void OnAnchorClicked()
    {
        if (_animating) { AppLog.Info("动画进行中，忽略点击"); return; }

        // 防抖：连点两下不该"清理+立刻回归"
        long now = Environment.TickCount64;
        if (now - _lastToggleMs < 450)
        {
            AppLog.Info("连击防抖，忽略");
            return;
        }
        _lastToggleMs = now;

        var journal = Journal.Load();
        if (journal.Count > 0) StartRestore(journal);
        else StartSuck();
    }

    private static float DpiScale => _overlay is null ? 1f : _overlay.Dpi / 96f;

    /// <summary>桌面图标矩形的边长（物理像素），用于飞行精灵的尺寸。</summary>
    private static int IconPx => (int)Math.Round(
        DeskTidy.Shell.DesktopViewReader.ReadIconSizePx() * DpiScale);

    private static int BadgePx => IconPx;

    /// <summary>吸入：图标加速冲向玻璃徽章。</summary>
    private static void StartSuck()
    {
        if (_anim is null || _overlay is null || _reader is null) return;

        var plan = DesktopCleaner.Scan(_anchorPath);
        AppLog.Info($"扫描桌面：保留 {plan.KeepCount}，待清理 {plan.MoveCount}");
        if (plan.MoveCount == 0)
        {
            AppLog.Info("没有可清理的普通文件（桌面只剩文件夹/快捷方式）");
            _ball?.Pulse();   // 给个"我收到了，但没东西可收"的反馈
            return;
        }

        var items = _reader.ReadItems(_overlay.WorkArea);
        var spacing = _reader.GetSpacing();
        var cells = new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, DeskTidy.Shell.DesktopItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.ParsePath)) continue;
            cells[it.ParsePath] = (it.ScreenPoint.X, it.ScreenPoint.Y);
            byPath[it.ParsePath] = it;
        }

        var wa = _overlay.WorkArea;
        float s = DpiScale;
        int iconPx = IconPx;
        float badgeCx = _spriteCx - wa.Left;
        float badgeCy = _spriteCy - wa.Top;
        float endSize = BadgePx * 0.26f;

        _anim.Begin();
        _overlay.Show();
        _ball?.PlaceAbove(_overlay.Handle);
        int i = 0;
        foreach (var item in plan.Moves)
        {
            if (!byPath.TryGetValue(item.Path, out var cell)) continue;
            var icon = DeskTidy.Visual.ShellIcon.Load(item.Path, Math.Max(48, iconPx));
            if (icon is null) continue;

            float fromCx = cell.ScreenPoint.X + iconPx / 2f - wa.Left;
            float fromCy = cell.ScreenPoint.Y + iconPx / 2f - wa.Top;

            _anim.AddSprite(icon, fromCx, fromCy, iconPx, iconPx,
                badgeCx, badgeCy, endSize, endSize,
                delayMs: Math.Min(i * 8, 220), durMs: 440,
                accelerate: true, fadeIn: false);

            if (i < 8)
                AppLog.Info($"sprite[{i}] {item.Name}: ({fromCx:0},{fromCy:0}) -> ({badgeCx:0},{badgeCy:0}) size {iconPx}->{endSize:0}");
            i++;
        }

        // 关键顺序：先把第 0 帧画出来（所有 ghost 已盖住原图标），再开始移文件。
        // 否则资源管理器会先把图标删掉，几十毫秒后 ghost 才出现 —— 看起来就是闪一下。
        _anim.Step(0f);

        // 文件和动画并行：ghost 一直盖着原位，Explorer 删图标的过程看不见
        var planRef = plan;
        var cellsRef = cells;
        _ = Task.Run(() =>
        {
            try
            {
                var res = DesktopCleaner.Execute(planRef, _anchorPath, cellsRef);
                AppLog.Info($"清理完成 moved={res.Moved} failed={res.Failed} dir={res.Dir}");
            }
            catch (Exception ex) { AppLog.Exception("Execute", ex); }
        });

        _animPhase = 1;
        _animating = true;
        _lastTickMs = 0;
        _overlay.StartAnimTick();
        AppLog.Info($"吸入动画开始：{i} 个精灵");
    }

    /// <summary>回归：文件先移回，精灵从徽章减速飞到原位，frost 遮罩最后揭开。</summary>
    private static void StartRestore(List<JournalEntry> journal)
    {
        if (_anim is null || _overlay is null || _reader is null) return;

        var entries = journal;
        _restoreEntries = entries.ToList();

        var wa = _overlay.WorkArea;
        int iconPx = IconPx;
        float badgeCx = _spriteCx - wa.Left;
        float badgeCy = _spriteCy - wa.Top;
        float startSize = BadgePx * 0.26f;

        _anim.Begin();
        _overlay.Show();
        _ball?.PlaceAbove(_overlay.Handle);

        int i = 0;
        foreach (var e in entries)
        {
            var icon = DeskTidy.Visual.ShellIcon.Load(e.To, Math.Max(48, iconPx))
                       ?? DeskTidy.Visual.ShellIcon.Load(e.From, Math.Max(48, iconPx));
            if (icon is null) continue;

            float toCx = e.CellX + iconPx / 2f - wa.Left;
            float toCy = e.CellY + iconPx / 2f - wa.Top;

            _anim.AddSprite(icon, badgeCx, badgeCy, startSize, startSize,
                toCx, toCy, iconPx, iconPx,
                delayMs: Math.Min(i * 10, 260), durMs: 460,
                accelerate: false, fadeIn: true);
            i++;
        }

        // 第 0 帧先上屏（frost 是 0、ghost 都在球里），再开始移文件
        _anim.Step(0f);

        // 延后 140ms：让 frost 铺满之后图标才回来，避免"遮罩还透明、图标先冒出来"
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(140);
                var (ok, fail, _remaining) = DesktopCleaner.RestoreFiles(entries);
                AppLog.Info($"还原文件 restored={ok} failed={fail}");
            }
            catch (Exception ex) { AppLog.Exception("RestoreFiles", ex); }
        });

        _animPhase = 2;
        _animating = true;
        _lastTickMs = 0;
        _overlay.StartAnimTick();
        AppLog.Info($"回归动画开始：{i} 个精灵，frost 开启");
    }

    private static void OnAnimTick()
    {
        if (!_animating || _anim is null || _overlay is null) return;

        long now = Environment.TickCount64;
        if (_lastTickMs == 0) _lastTickMs = now;
        float dt = Math.Clamp(now - _lastTickMs, 1, 120);
        _lastTickMs = now;

        if (_animPhase == 2)
        {
            float total = _anim.TotalMs;
            float up = Math.Clamp(_anim.Clock / 140f, 0f, 1f);
            float down = total > 0 ? Math.Clamp((_anim.Clock - (total - 30f)) / 280f, 0f, 1f) : 1f;
            _anim.FrostOpacity = 0.96f * up * (1f - down);
        }

        bool done = _anim.Step(dt);
        if (!done && _anim.Clock < 5000) return;

        // 超过 5 秒的动画一律强停：宁可少一帧，也不能让 frost 停在屏幕上
        if (_anim.Clock >= 5000)
            AppLog.Warn($"动画超时（{_anim.Clock:0}ms），强制结束");

        _overlay.StopAnimTick();
        _animating = false;
        _lastTickMs = 0;
        _anim.FrostOpacity = 0f;

        if (_animPhase == 2) FinishRestore();
        _animPhase = 0;

        AppLog.Info("动画结束");
        // 动画用的画布收起来，桌面只剩那个小球
        _overlay.Hide();
        _ball?.PlaceAbove(_overlay.Handle);
        RenderBall();
    }

    private static void FinishRestore()
    {
        // 注意：RestoreFiles 成功后会清空 journal，所以这里必须用开始回归时记住的那份
        var entries = _restoreEntries;
        _restoreEntries = new List<JournalEntry>();
        if (entries.Count == 0) return;

        Reposition(entries);
        Journal.Clear();
        AppLog.Info($"回归完成：{entries.Count} 个文件已回原位");
    }

    private static List<JournalEntry> _restoreEntries = new();

    /// <summary>把记录的格子坐标写回 shell（SVSI_POSITIONITEM）。</summary>
    private static void Reposition(IEnumerable<JournalEntry> entries)
    {
        if (_reader is null) return;
        var targets = new Dictionary<string, DeskTidy.Interop.NativeMethods.POINT>(StringComparer.OrdinalIgnoreCase);
        var vr = _reader.ViewRect;
        foreach (var e in entries)
        {
            targets[e.From] = new DeskTidy.Interop.NativeMethods.POINT
            {
                X = e.CellX - vr.Left,
                Y = e.CellY - vr.Top,
            };
        }

        // 文件刚移回来时 Explorer 还没把图标加进视图，这里重试几次等它出现
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (_reader.SetItemPositions(targets) > 0) return;
            Thread.Sleep(150);
        }
        AppLog.Warn("摆位失败：视图里始终没有找到这些条目");
    }

    /// <summary>紧急还原：不走动画，直接移回 + 摆位。热键/托盘用。</summary>
    private static void ForceRestore()
    {
        var entries = Journal.Load();
        if (entries.Count == 0) { AppLog.Info("紧急还原：没有可还原的内容"); return; }

        AppLog.Info($"紧急还原：{entries.Count} 项");
        var (ok, fail, _) = DesktopCleaner.RestoreFiles(entries);
        Reposition(entries);
        AppLog.Info($"紧急还原完成 ok={ok} fail={fail}");

        Journal.Clear();
        _tile = null;
        RenderBall();
    }

    private static void OpenAnchorFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_anchorPath}\"",
                UseShellExecute = true,
            });
            AppLog.Info($"打开锚点文件夹 {_anchorPath}");
        }
        catch (Exception ex) { AppLog.Warn($"打开文件夹失败：{ex.Message}"); }
    }

    private static int ParseStay(string[] args) => ParseSecondsFlag(args, "--stay");

    // ---------------- 无窗口模式 ----------------

    private static int RunToggle(string[] args)
    {
        var cfg = Config.Load();
        var anchorArg = ParseValue(args, "--anchor");
        if (!string.IsNullOrWhiteSpace(anchorArg)) cfg.AnchorPath = anchorArg;
        ApplyRootOverride(args);
        var anchor = Path.GetFullPath(cfg.ResolveAnchor());
        cfg.Save();

        using var mutex = new Mutex(false, @"Local\DeskTidy.Toggle");
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) { AppLog.Warn("已有实例在执行，退出"); return 2; }

        try
        {
            var journal = Journal.Load();
            if (journal.Count > 0)
            {
                AppLog.Info($"模式=还原（{journal.Count} 项）");
                return DoRestore(journal);
            }
            AppLog.Info("模式=清理");
            return DoClean(anchor);
        }
        catch (Exception ex)
        {
            AppLog.Exception("RunToggle", ex);
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    private static int DoClean(string anchor)
    {
        var plan = DesktopCleaner.Scan(anchor);
        AppLog.Info($"扫描：保留 {plan.KeepCount}，待清理 {plan.MoveCount}");
        if (plan.MoveCount == 0)
        {
            AppLog.Info("没有可清理的普通文件（桌面只剩文件夹/快捷方式）");
            return 0;
        }

        // 记录每个文件图标的格子坐标，供还原时摆回原处
        var cells = new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase);
        var reader = TryOpenReader();
        if (reader is not null)
        {
            try
            {
                var all = new DeskTidy.Interop.NativeMethods.RECT
                { Left = -100000, Top = -100000, Right = 100000, Bottom = 100000 };
                foreach (var it in reader.ReadItems(all))
                    if (!string.IsNullOrEmpty(it.ParsePath))
                        cells[it.ParsePath] = (it.ScreenPoint.X, it.ScreenPoint.Y);
            }
            catch (Exception ex) { AppLog.Warn($"读图标坐标失败：{ex.Message}"); }
            finally { reader.Dispose(); }
        }

        var (moved, failed, dir, _) = DesktopCleaner.Execute(plan, anchor, cells);
        AppLog.Info($"清理完成 moved={moved} failed={failed} dir={dir}");
        return 0;
    }

    private static int DoRestore(List<JournalEntry> entries)
    {
        var (ok, fail, _remaining) = DesktopCleaner.RestoreFiles(entries);
        AppLog.Info($"还原完成 ok={ok} fail={fail}");

        var reader = TryOpenReader();
        if (reader is not null)
        {
            try
            {
                var targets = new Dictionary<string, DeskTidy.Interop.NativeMethods.POINT>(
                    StringComparer.OrdinalIgnoreCase);
                var vr = reader.ViewRect;
                foreach (var e in entries)
                    targets[e.From] = new DeskTidy.Interop.NativeMethods.POINT
                    { X = e.CellX - vr.Left, Y = e.CellY - vr.Top };

                for (int attempt = 0; attempt < 8; attempt++)
                    if (reader.SetItemPositions(targets) > 0) break;
                    else Thread.Sleep(150);
            }
            catch (Exception ex) { AppLog.Warn($"摆位失败：{ex.Message}"); }
            finally { reader.Dispose(); }
        }
        return 0;
    }

    private static DeskTidy.Shell.DesktopViewReader? TryOpenReader()
    {
        try
        {
            var r = DeskTidy.Shell.DesktopViewReader.TryOpen(out var err);
            if (r is null) AppLog.Warn($"桌面视图打不开：{err}");
            return r;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"桌面视图异常：{ex.Message}");
            return null;
        }
    }

    private static int RunInstallShortcut(string[] args)
    {
        var cfg = Config.Load();
        var anchor = Path.GetFullPath(cfg.ResolveAnchor());

        var folderIcon = DeskTidy.Visual.ShellIcon.Load(anchor, 256)
            ?? DeskTidy.Visual.ShellIcon.Load(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 256);
        if (folderIcon is null)
        {
            AppLog.Error("取文件夹图标失败，无法生成快捷方式图标");
            return 1;
        }

        // 换文件名，逼资源管理器丢掉旧图标缓存
        var icoPath = Path.Combine(AppPaths.DataDir, "falsfile_v2.ico");
        DeskTidy.Visual.IconWriter.Write(icoPath, folderIcon);

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskTidy.exe");
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lnk = Path.Combine(desk, "falsfile.lnk");

        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("找不到 WScript.Shell");
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = exe;
            sc.Arguments = "--toggle";
            sc.WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
            sc.IconLocation = icoPath + ",0";
            sc.Description = "桌面净化：双击收纳桌面杂物，再双击还原";
            sc.WindowStyle = 7;   // 最小化，避免闪一下
            sc.Save();

            AppLog.Info($"快捷方式已建立：{lnk} -> {exe} --toggle");
            try { Console.Out.WriteLine(lnk); } catch { }
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Exception("RunInstallShortcut", ex);
            return 1;
        }
    }

    private static string ResolveAnchorArg(string[] args)
    {
        var a = ParseValue(args, "--anchor");
        if (!string.IsNullOrWhiteSpace(a)) return Path.GetFullPath(a);
        var cfg = Config.Load();
        return Path.GetFullPath(cfg.ResolveAnchor());
    }

    private static void ApplyRootOverride(string[] args)
    {
        var root = ParseValue(args, "--root");
        if (!string.IsNullOrWhiteSpace(root)) DesktopCleaner.DesktopOverride = Path.GetFullPath(root);
    }

    private static int RunDryRun(string[] args)
    {
        ApplyRootOverride(args);
        var anchor = ResolveAnchorArg(args);
        var plan = DesktopCleaner.Scan(anchor);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"桌面: {DesktopCleaner.DesktopDir}");
        sb.AppendLine($"锚点: {anchor}");
        sb.AppendLine($"保留 {plan.KeepCount} / 清理 {plan.MoveCount}");
        foreach (var it in plan.Items)
            sb.AppendLine($"{(it.WillMove ? "MOVE" : "KEEP")}\t{it.Kind}\t{it.Name}\t{it.Reason}");

        var text = sb.ToString();
        AppLog.Info("\n" + text);
        try { Console.Out.Write(text); } catch { }
        try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "dryrun.txt"), text); } catch { }
        return 0;
    }

    private static int RunCleanNow(string[] args)
    {
        ApplyRootOverride(args);
        var anchor = ResolveAnchorArg(args);
        var plan = DesktopCleaner.Scan(anchor);
        var cells = new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase);
        var (moved, failed, dir, _) = DesktopCleaner.Execute(plan, anchor, cells);
        var text = $"清理完成 moved={moved} failed={failed} dir={dir}";
        AppLog.Info(text);
        try { Console.Out.WriteLine(text); } catch { }
        return 0;
    }

    private static int RunRestoreNow(string[] args)
    {
        var entries = Journal.Load();
        var (ok, fail, _) = DesktopCleaner.RestoreFiles(entries);
        var text = $"还原完成 ok={ok} fail={fail}";
        AppLog.Info(text);
        try { Console.Out.WriteLine(text); } catch { }
        return 0;
    }

    private static string? ParseValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>预览用：挑桌面上第一个普通文件夹当样例图标。</summary>
    private static string PickSampleFolder()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var dir = Directory.EnumerateDirectories(desktop)
                .FirstOrDefault(d => (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0);
            if (dir is not null) return dir;
            return desktop;
        }
        catch { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
    }

    private static int ParseSecondsFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var s))
                return s;
        return 0;
    }

    /// <summary>
    /// M1 校准：连续读取桌面条目位置并画框，同时把坐标表写到 calibration.txt。
    /// 只读，不修改任何条目。
    /// </summary>
    private static int RunCalibration(int seconds)
    {
        var reader = DeskTidy.Shell.DesktopViewReader.TryOpen(out var error);
        if (reader is null)
        {
            AppLog.Error($"桌面视图打不开：{error}");
            return 3;
        }

        try
        {
            var spacing = reader.GetSpacing();
            AppLog.Info($"单元格 GetSpacing={spacing.X}x{spacing.Y}  视图窗口=0x{reader.ViewWindow:X} ({reader.ViewClass})");

            _overlay!.Show();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tablePath = Path.Combine(AppPaths.DataDir, "calibration.txt");

            while (sw.Elapsed.TotalSeconds < seconds && !_exitRequested)
            {
                var items = reader.ReadItems(_overlay.WorkArea);
                var cells = items.Where(i => i.OnPrimaryWorkArea)
                                 .Select(i => (i.ScreenPoint, spacing.X, spacing.Y))
                                 .ToList();

                _overlay.Host.RenderAndPresent(ctx =>
                    DeskTidy.UI.CalibrationRenderer.Draw(ctx, cells, _overlay.WorkArea, (int)_overlay.Dpi, items.Count));

                WriteCalibrationTable(tablePath, reader, items, spacing);
                Thread.Sleep(600);
            }

            _overlay.Hide();
            AppLog.Info("校准模式结束");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Exception("RunCalibration", ex);
            try { _overlay?.Hide(); } catch { }
            return 4;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static void WriteCalibrationTable(string path, DeskTidy.Shell.DesktopViewReader reader,
        List<DeskTidy.Shell.DesktopItem> items, DeskTidy.Interop.NativeMethods.POINT spacing)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("index\tname\tview_x\tview_y\tscreen_x\tscreen_y\tcanvas_x\tcanvas_y\tonCanvas\tmonitor\tpath");
        var wa = _overlay!.WorkArea;
        foreach (var it in items)
        {
            sb.Append(it.Index).Append('\t')
              .Append(it.Name).Append('\t')
              .Append(it.ViewPoint.X).Append('\t').Append(it.ViewPoint.Y).Append('\t')
              .Append(it.ScreenPoint.X).Append('\t').Append(it.ScreenPoint.Y).Append('\t')
              .Append(it.ScreenPoint.X - wa.Left).Append('\t').Append(it.ScreenPoint.Y - wa.Top).Append('\t')
              .Append(it.OnPrimaryWorkArea).Append('\t')
              .Append($"{it.MonitorRect.Left},{it.MonitorRect.Top},{it.MonitorRect.Width}x{it.MonitorRect.Height}").Append('\t')
              .Append(it.ParsePath)
              .AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine($"viewWindow=0x{reader.ViewWindow:X} class={reader.ViewClass}");
        sb.AppendLine($"viewRect={reader.ViewRect.Left},{reader.ViewRect.Top},{reader.ViewRect.Width}x{reader.ViewRect.Height}");
        sb.AppendLine($"workArea={wa.Left},{wa.Top},{wa.Width}x{wa.Height} cell={spacing.X}x{spacing.Y} dpi={_overlay.Dpi}");
        try { File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true)); } catch { }
    }

    private readonly record struct PatternSpec(int X, int Y, int W, int H, int R, int G, int B);

    private static PatternSpec? ParsePattern(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--pattern", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = args[i + 1].Split(',');
            if (parts.Length != 7) return null;
            var v = new int[7];
            for (int k = 0; k < 7; k++)
                if (!int.TryParse(parts[k], out v[k])) return null;
            return new PatternSpec(v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
        }
        return null;
    }

    private static void AttachConsole()
    {
        if (!NativeMethods.AttachConsole(ATTACH_PARENT_PROCESS) && !NativeMethods.AllocConsole())
            return;
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(stdout);
            AppLog.EchoToConsole = true;
        }
        catch { }
    }

    private static void TryEmergencyCleanup()
    {
        // 出任何意外都保证窗口不留在屏幕上
        try { _overlay?.Hide(); } catch { }
    }
}
