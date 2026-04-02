using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Dota2NetWorth
{
    public partial class MainWindow : Window
    {
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int GWL_EXSTYLE = -20;
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);[DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int HOTKEY_ID = 9000;

        // --- 全局状态 ---
        private bool isLocked = true;
        private bool isDotaRunning = false;
        private bool inMatch = false;

        // --- 快捷键配置 (默认 Ctrl+Alt+F10) ---
        private uint hotkeyMods = 0x0001 | 0x0002; // Alt + Ctrl
        private uint hotkeyVk = 0x79; // F10
        private string hotkeyName = "Ctrl + Alt + F10";

        private Dictionary<string, int> itemPrices = new Dictionary<string, int>();
        private System.Windows.Forms.NotifyIcon trayIcon;
        private HttpListener gsiListener;

        private int cacheGold = 0;
        private bool cacheShard = false;
        private bool cacheScepterBuff = false;
        private Dictionary<string, string> cacheItems = new Dictionary<string, string>();

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
            LoadItemPrices();
            EnsureGsiConfigExists();
            InitTrayIcon();

            StartGsiServer();
            StartProcessMonitor();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(HwndHook);

            // 首次注册记忆的快捷键
            RegisterCurrentHotkey();
            ApplyLockState();
        }

        // --- 快捷键动态注册与解绑逻辑 ---
        private void RegisterCurrentHotkey()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID); // 先解绑旧的
            RegisterHotKey(handle, HOTKEY_ID, hotkeyMods, hotkeyVk); // 注册新的
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleLockState();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // 打开快捷键设置弹窗
        private void OpenHotkeyDialog()
        {
            var dialog = new HotkeyWindow(hotkeyName);
            dialog.ShowDialog();

            if (dialog.IsSuccess)
            {
                hotkeyMods = dialog.Modifiers;
                hotkeyVk = dialog.VirtualKey;
                hotkeyName = dialog.HotkeyName;

                RegisterCurrentHotkey(); // 重新向系统注册新快捷键
                SaveConfig();            // 永久保存

                // 更新右下角托盘的显示文字
                trayIcon.ContextMenuStrip.Items[0].Text = string.Format("修改快捷键 (当前: {0})", hotkeyName);
            }
        }

        // --- 以下为原有的 UI 与 GSI 逻辑保持不变 ---
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isLocked) DragMove();
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isLocked) SaveConfig();
        }

        private void ApplyLockState()
        {
            Dispatcher.Invoke(new Action(() =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

                if (isLocked)
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
                    BgBorder.Background = Brushes.Transparent;
                    BgBorder.BorderThickness = new Thickness(0);
                    this.Cursor = Cursors.Arrow;

                    if (isDotaRunning && inMatch) this.Visibility = Visibility.Visible;
                    else this.Visibility = Visibility.Hidden;
                }
                else
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);

                    // 深绿色半透明背景不变
                    BgBorder.Background = new SolidColorBrush(Color.FromArgb(200, 20, 40, 20));
                    // 改用纯净的高亮电竞绿 (#4CAF50) 实线边框，彻底杜绝渲染溢出
                    BgBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    BgBorder.BorderThickness = new Thickness(2);

                    this.Cursor = Cursors.SizeAll;

                    if (isDotaRunning) this.Visibility = Visibility.Visible;
                    else this.Visibility = Visibility.Hidden;
                }
            }));
        }

        private void ToggleLockState()
        {
            if (!isDotaRunning) return;
            isLocked = !isLocked;
            SaveConfig();
            ApplyLockState();
        }

        private async void StartProcessMonitor()
        {
            while (true)
            {
                bool currentRunning = Process.GetProcessesByName("dota2").Length > 0;
                if (currentRunning != isDotaRunning)
                {
                    isDotaRunning = currentRunning;
                    if (!isDotaRunning) isLocked = true;
                    ApplyLockState();
                }
                await Task.Delay(3000);
            }
        }

        private async void StartGsiServer()
        {
            gsiListener = new HttpListener();
            gsiListener.Prefixes.Add("http://127.0.0.1:3000/");
            try
            {
                gsiListener.Start();
                while (true)
                {
                    var context = await gsiListener.GetContextAsync();
                    ProcessGsiRequest(context);
                }
            }
            catch { }
        }

        private async void ProcessGsiRequest(HttpListenerContext context)
        {
            using (var reader = new StreamReader(context.Request.InputStream))
            {
                string json = await reader.ReadToEndAsync();
                context.Response.StatusCode = 200;
                context.Response.Close();

                if (string.IsNullOrWhiteSpace(json)) return;

                try
                {
                    var jss = new JavaScriptSerializer();
                    var root = jss.Deserialize<Dictionary<string, object>>(json);
                    if (root == null) return;

                    bool currentInMatch = root.ContainsKey("player") || root.ContainsKey("hero") || root.ContainsKey("items");
                    if (!currentInMatch)
                    {
                        cacheGold = 0; cacheItems.Clear(); cacheShard = false; cacheScepterBuff = false;
                        if (inMatch) { inMatch = false; ApplyLockState(); }
                        return;
                    }

                    if (!inMatch) { inMatch = true; ApplyLockState(); }

                    if (root.ContainsKey("player"))
                    {
                        var player = root["player"] as Dictionary<string, object>;
                        if (player != null && player.ContainsKey("gold")) cacheGold = Convert.ToInt32(player["gold"]);
                    }

                    if (root.ContainsKey("items"))
                    {
                        var items = root["items"] as Dictionary<string, object>;
                        if (items != null)
                        {
                            for (int i = 0; i < 9; i++)
                            {
                                string slot = "slot" + i;
                                if (items.ContainsKey(slot))
                                {
                                    var itemData = items[slot] as Dictionary<string, object>;
                                    if (itemData != null && itemData.ContainsKey("name"))
                                    {
                                        string name = itemData["name"] as string;
                                        cacheItems[slot] = string.IsNullOrEmpty(name) ? "empty" : name;
                                    }
                                    else cacheItems[slot] = "empty";
                                }
                            }
                        }
                    }

                    if (root.ContainsKey("hero"))
                    {
                        var hero = root["hero"] as Dictionary<string, object>;
                        if (hero != null)
                        {
                            if (hero.ContainsKey("aghanims_shard")) cacheShard = Convert.ToBoolean(hero["aghanims_shard"]);
                            if (hero.ContainsKey("aghanims_scepter")) cacheScepterBuff = Convert.ToBoolean(hero["aghanims_scepter"]);
                        }
                    }

                    int totalNetWorth = cacheGold;
                    bool hasScepterItem = false;

                    foreach (var item in cacheItems.Values)
                    {
                        if (item != "empty")
                        {
                            string apiName = item.Replace("item_", "");
                            if (apiName == "ultimate_scepter") hasScepterItem = true;
                            if (itemPrices.ContainsKey(apiName)) totalNetWorth += itemPrices[apiName];
                        }
                    }

                    if (cacheShard) totalNetWorth += 1400;
                    if (cacheScepterBuff && !hasScepterItem) totalNetWorth += 4200;

                    Dispatcher.Invoke(new Action(() => TextLabel.Text = totalNetWorth.ToString("N0")));
                }
                catch { }
            }
        }

        private void LoadItemPrices()
        {
            try
            {
                string json = File.ReadAllText("item_price.json");
                var jss = new JavaScriptSerializer();
                var dict = jss.Deserialize<Dictionary<string, object>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict)
                    {
                        var valDict = kvp.Value as Dictionary<string, object>;
                        if (valDict != null && valDict.ContainsKey("cost")) itemPrices[kvp.Key] = Convert.ToInt32(valDict["cost"]);
                    }
                }
            }
            catch { }
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists("config.txt"))
                {
                    var parts = File.ReadAllText("config.txt").Split(',');
                    this.Left = double.Parse(parts[0]);
                    this.Top = double.Parse(parts[1]);
                    this.isLocked = bool.Parse(parts[2]);

                    // 兼容读取新加入的快捷键配置
                    if (parts.Length >= 6)
                    {
                        hotkeyMods = uint.Parse(parts[3]);
                        hotkeyVk = uint.Parse(parts[4]);
                        hotkeyName = parts[5];
                    }
                }
                else { this.Left = 200; this.Top = 60; }
            }
            catch { this.Left = 200; this.Top = 60; }
        }

        private void SaveConfig()
        {
            try { File.WriteAllText("config.txt", string.Format("{0},{1},{2},{3},{4},{5}", this.Left, this.Top, this.isLocked, hotkeyMods, hotkeyVk, hotkeyName)); } catch { }
        }

        private void EnsureGsiConfigExists()
        {
            try
            {
                string path = null;
                string regPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 570";
                using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (RegistryKey subkey = key.OpenSubKey(regPath))
                    {
                        if (subkey != null) path = subkey.GetValue("InstallLocation") as string;
                    }
                }
                if (string.IsNullOrEmpty(path))
                {
                    using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    {
                        using (RegistryKey subkey = key.OpenSubKey(regPath))
                        {
                            if (subkey != null) path = subkey.GetValue("InstallLocation") as string;
                        }
                    }
                }

                if (string.IsNullOrEmpty(path)) return;

                string gsiFolder = Path.Combine(path, "game", "dota", "cfg", "gamestate_integration");
                if (!Directory.Exists(gsiFolder)) Directory.CreateDirectory(gsiFolder);

                string cfgPath = Path.Combine(gsiFolder, "gamestate_integration_networth.cfg");
                string cfgContent = @"""Dota 2 Net Worth""
                                        {
                                            ""uri""           ""http://127.0.0.1:3000/""
                                            ""timeout""       ""5.0""
                                            ""buffer""        ""0.1""
                                            ""throttle""      ""0.5""
                                            ""heartbeat""     ""10.0""
                                            ""data""
                                            {
                                                ""items""     ""1""
                                                ""player""    ""1""
                                                ""hero""      ""1""
                                            }
                                        }";
                if (!File.Exists(cfgPath)) File.WriteAllText(cfgPath, cfgContent);
            }
            catch { }
        }

        private void InitTrayIcon()
        {
            trayIcon = new System.Windows.Forms.NotifyIcon();
            trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            trayIcon.Text = "Dota2 资产助手";
            trayIcon.Visible = true;

            var menu = new System.Windows.Forms.ContextMenuStrip();

            // --- 绑定修改快捷键功能 ---
            menu.Items.Add(string.Format("修改快捷键 (当前: {0})", hotkeyName), null, (s, e) => OpenHotkeyDialog());

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出插件", null, (s, e) => {
                trayIcon.Dispose();
                Environment.Exit(0);
            });
            trayIcon.ContextMenuStrip = menu;
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID);
            base.OnClosed(e);
        }
    }
}