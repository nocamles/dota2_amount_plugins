using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dota2NetWorth.Services;
using Dota2NetWorth.ViewModels;

namespace Dota2NetWorth
{
    /// <summary>
    /// 仅做 View 协调：组装 Services，转发事件到 ViewModel。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int GWL_EXSTYLE = -20;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        // GSI watchdog：超过该时间没收到 GSI 数据，强制视为离开比赛并隐藏 UI
        private static readonly TimeSpan GsiTimeout = TimeSpan.FromSeconds(30);

        private readonly NetWorthViewModel _vm = new NetWorthViewModel();
        private readonly AppConfig _config;
        private readonly ItemPriceDb _priceDb = new ItemPriceDb();
        private readonly GsiParser _parser = new GsiParser();
        private readonly GsiServer _gsi = new GsiServer();
        private readonly ProcessWatcher _watcher = new ProcessWatcher();
        private readonly HotKeyManager _hotKey = new HotKeyManager();
        private DispatcherTimer _watchdogTimer;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;

            _config = AppConfig.Load();
            Logger.VerboseEnabled = _config.VerboseLog;
            Logger.Info("MainWindow 初始化 (VerboseLog=" + _config.VerboseLog + ")");

            Left = _config.Left;
            Top = _config.Top;

            _priceDb.Load();
            EnsureGsiCfg();
            ApplyAutoStart();

            _watcher.OnRunningChanged += OnDotaRunningChanged;
            _watcher.Start();

            _gsi.OnPayload += OnGsiPayload;
            _gsi.Start();
        }

        // ----- 自启 -----
        private void ApplyAutoStart()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                AutoStartManager.Apply(_config.AutoStart, exe);
            }
            catch (Exception ex) { Logger.Warn("读取 exe 路径失败: " + ex.Message); }
        }

        // ----- GSI cfg -----
        private void EnsureGsiCfg()
        {
            string p = _config.DotaPath;
            if (string.IsNullOrEmpty(p) || !Directory.Exists(p))
                p = DotaPathLocator.AutoDetect();

            if (string.IsNullOrEmpty(p) || !Directory.Exists(p))
            {
                var r = MessageBox.Show(
                    "未自动找到 Dota 2 安装路径。\n\n是否手动选择 dota2.exe 所在目录？\n(若不配置，无法获取游戏内资产数据)",
                    "Dota2 资产助手 - 路径未找到", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) p = DotaPathLocator.ManuallyPick();
            }
            if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) return;

            string outPath, content;
            if (DotaPathLocator.EnsureGsiCfg(p, out outPath, out content))
            {
                _config.DotaPath = p;
                _config.Save();
            }
            else
            {
                try { Clipboard.SetText(content); } catch { }
                MessageBox.Show(string.Format(
                    "无法自动创建 GSI 配置文件。\n\n【目标路径】\n{0}\n\n" +
                    "可能是权限不足或杀毒软件拦截。\n配置内容已自动【复制到您的剪贴板】。\n\n" +
                    "【手动解决步骤】\n1. 打开上述路径（不存在请新建）。\n" +
                    "2. 新建文本文件，将剪贴板内容粘贴并保存。\n" +
                    "3. 重命名为：gamestate_integration_networth.cfg\n\n" +
                    "(或以管理员身份重启本程序)", outPath),
                    "配置创建失败 - 需手动处理", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ----- 事件处理 -----
        private void OnDotaRunningChanged(bool running)
        {
            Logger.Info("Dota 运行状态变化: " + running);
            if (!running)
            {
                _config.IsLocked = true;
                _parser.MarkOffline(); // Dota 关闭：清状态
            }
            Dispatcher.BeginInvoke(new Action(ApplyLockState));
        }

        private void OnGsiPayload(string json)
        {
            if (!_parser.Parse(json)) return;
            var snap = _parser.Snapshot;
            int total = NetWorthCalculator.Calculate(snap, _priceDb);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (snap.InMatch) _vm.UpdateNetWorth(total);
                else _vm.Reset();
                ApplyLockState();
            }));
        }

        // ----- watchdog：检测 GSI 是否长时间无数据（修复 BUG 1）-----
        private void StartWatchdog()
        {
            _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watchdogTimer.Tick += (s, e) =>
            {
                if (!_parser.Snapshot.InMatch) return;
                if (_gsi.LastReceivedAt == DateTime.MinValue) return;
                if (DateTime.Now - _gsi.LastReceivedAt > GsiTimeout)
                {
                    Logger.Warn("Watchdog 触发：" + GsiTimeout.TotalSeconds + " 秒未收到 GSI 数据。");
                    _parser.MarkOffline();
                    _vm.Reset();
                    ApplyLockState();
                }
            };
            _watchdogTimer.Start();
        }

        // ----- 快捷键 / 锁定状态 -----
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr h = new WindowInteropHelper(this).Handle;
            HwndSource src = HwndSource.FromHwnd(h);
            if (src != null) src.AddHook(HwndHook);
            _hotKey.OnPressed += ToggleLock;
            _hotKey.Register(h);
            ApplyLockState();
            StartWatchdog();
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_hotKey.HandleWmHotKey(msg, wParam)) handled = true;
            return IntPtr.Zero;
        }

        private void ToggleLock()
        {
            if (!_watcher.IsRunning) return;
            _config.IsLocked = !_config.IsLocked;
            _config.Save();
            Logger.Info("ToggleLock -> " + _config.IsLocked);
            Dispatcher.BeginInvoke(new Action(ApplyLockState));
        }

        private void ApplyLockState()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            bool locked = _config.IsLocked;
            bool dotaUp = _watcher.IsRunning;
            bool inMatch = _parser.Snapshot.InMatch;

            if (locked)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
                BgBorder.Background = Brushes.Transparent;
                BgBorder.BorderThickness = new Thickness(0);
                Cursor = Cursors.Arrow;
                Visibility = (dotaUp && inMatch) ? Visibility.Visible : Visibility.Hidden;
            }
            else
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
                BgBorder.Background = new SolidColorBrush(Color.FromArgb(200, 20, 40, 20));
                BgBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                BgBorder.BorderThickness = new Thickness(2);
                Cursor = Cursors.SizeAll;
                // 解锁拖拽态：dota 运行就显示，便于用户调整位置
                Visibility = dotaUp ? Visibility.Visible : Visibility.Hidden;
            }
        }

        // ----- 拖拽 -----
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_config.IsLocked) DragMove();
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_config.IsLocked)
            {
                _config.Left = Left;
                _config.Top = Top;
                _config.Save();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_watchdogTimer != null) _watchdogTimer.Stop();
            _hotKey.Unregister();
            _gsi.Dispose();
            _watcher.Dispose();
            base.OnClosed(e);
        }
    }
}
