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
        private int myTeamSlot = -1;

        // --- 快捷键配置 (默认 Ctrl+Alt+F10) ---
        private uint hotkeyMods = 0x0001 | 0x0002; // Alt + Ctrl
        private uint hotkeyVk = 0x79; // F10
        private string hotkeyName = "Ctrl + Alt + F10";

        private string dotaPath = null;
        private Dictionary<string, int> itemPrices = new Dictionary<string, int>();
        private System.Windows.Forms.NotifyIcon trayIcon;
        private HttpListener gsiListener;

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
                        myTeamSlot = -1;
                        isFirstTick = true;
                        lastRawNetWorth = 0;
                        lastItems.Clear();
                        ghostItems.Clear();
                        if (inMatch) { inMatch = false; ApplyLockState(); }
                        return;
                    }

                    if (!inMatch) { inMatch = true; ApplyLockState(); }

                    List<string> currItemsList;
                    int currRawNetWorth = CalculateNetWorth(root, out currItemsList);

                    if (isFirstTick)
                    {
                        lastRawNetWorth = currRawNetWorth;
                        lastItems = currItemsList;
                        isFirstTick = false;
                    }
                    else
                    {
                        var missingItems = new List<string>();
                        var tempCurr = new List<string>(currItemsList);
                        foreach (var oldItem in lastItems)
                        {
                            if (tempCurr.Contains(oldItem)) tempCurr.Remove(oldItem);
                            else missingItems.Add(oldItem);
                        }
                        var appearedItems = tempCurr;

                        int deltaRawNW = currRawNetWorth - lastRawNetWorth;

                        // 1. 移除已经重新出现的影子装备
                        foreach (var appeared in appearedItems)
                        {
                            if (ghostItems.Contains(appeared))
                            {
                                ghostItems.Remove(appeared);
                            }
                        }

                        // 2. 判断消失的装备是否进入了信使/丢在地上
                        foreach (var missing in missingItems)
                        {
                            if (IsConsumable(missing)) continue;

                            int cost = GetItemCost("item_" + missing);
                            // 价格大于0，且总资产出现了与该物品价格相符的下跌 (跌幅大于75%，过滤掉半价出售的情况)
                            if (cost > 0 && deltaRawNW <= -(cost * 0.75))
                            {
                                ghostItems.Add(missing);
                                deltaRawNW += cost; // 补偿跌幅，以便同时判断多个物品
                            }
                        }

                        lastRawNetWorth = currRawNetWorth;
                        lastItems = currItemsList;
                    }

                    int totalNetWorth = currRawNetWorth;
                    foreach (var ghost in ghostItems)
                    {
                        totalNetWorth += GetItemCost("item_" + ghost);
                    }

                    Dispatcher.Invoke(new Action(() => TextLabel.Text = totalNetWorth.ToString("N0")));
                }
                catch { }
            }
        }

        private int CalculateNetWorth(Dictionary<string, object> root, out List<string> currItemsList)
        {
            int netWorth = 0;
            currItemsList = new List<string>();

            // 1. 现金
            if (root.ContainsKey("player"))
            {
                var player = root["player"] as Dictionary<string, object>;
                if (player != null)
                {
                    if (player.ContainsKey("gold")) netWorth += Convert.ToInt32(player["gold"]);
                    if (player.ContainsKey("team_slot")) myTeamSlot = Convert.ToInt32(player["team_slot"]);
                }
            }

            // 2. 物品 (身上 + 储藏栏)
            if (root.ContainsKey("items"))
            {
                var items = root["items"] as Dictionary<string, object>;
                if (items != null)
                {
                    foreach (var kvp in items)
                    {
                        // slot0-8, stash0-5, 排除 neutral0
                        if (!kvp.Key.StartsWith("slot") && !kvp.Key.StartsWith("stash")) continue;
                        
                        var itemData = kvp.Value as Dictionary<string, object>;
                        if (itemData == null || !itemData.ContainsKey("name")) continue;

                        string name = itemData["name"] as string;
                        if (string.IsNullOrEmpty(name) || name == "empty") continue;

                        // 归属权校验
                        if (itemData.ContainsKey("purchaser"))
                        {
                            int purchaser = Convert.ToInt32(itemData["purchaser"]);
                            // 如果不是自己的装备，且不是圣剑/宝石，则不计费
                            if (purchaser != myTeamSlot)
                            {
                                if (!name.Contains("rapier") && !name.Contains("gem")) continue;
                            }
                        }

                        string apiName = name.Replace("item_", "");
                        currItemsList.Add(apiName);

                        int cost = GetItemCost(name);
                        int charges = 1;
                        if (itemData.ContainsKey("charges")) charges = Convert.ToInt32(itemData["charges"]);

                        netWorth += cost * charges;
                    }
                }
            }

            // 3. 永久 Buff (A杖、魔晶、银月)
            if (root.ContainsKey("hero"))
            {
                var hero = root["hero"] as Dictionary<string, object>;
                if (hero != null)
                {
                    // 魔晶
                    if (hero.ContainsKey("aghanims_shard") && Convert.ToBoolean(hero["aghanims_shard"]))
                    {
                        netWorth += GetItemCost("item_aghanims_shard");
                    }

                    // A 杖 (被吃掉的状态)
                    if (hero.ContainsKey("aghanims_scepter") && Convert.ToBoolean(hero["aghanims_scepter"]))
                    {
                        // 检查身上是否有 A 杖，如果没有，说明是吃掉的 Buff
                        if (!HasItem(root, "item_ultimate_scepter"))
                        {
                            int scepterCost = GetItemCost("item_ultimate_scepter_2");
                            if (scepterCost == 0) scepterCost = GetItemCost("item_ultimate_scepter"); // 兜底查普通 A 杖
                            netWorth += scepterCost;
                        }
                    }

                    // 银月 (消耗)
                    if (hero.ContainsKey("permanent_buffs"))
                    {
                        var buffs = hero["permanent_buffs"] as System.Collections.ArrayList;
                        if (buffs != null)
                        {
                            foreach (Dictionary<string, object> buff in buffs)
                            {
                                if (buff.ContainsKey("name") && buff["name"].ToString() == "modifier_item_moon_shard_consumed")
                                {
                                    netWorth += GetItemCost("item_moon_shard");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            return netWorth;
        }

        private bool HasItem(Dictionary<string, object> root, string itemName)
        {
            if (!root.ContainsKey("items")) return false;
            var items = root["items"] as Dictionary<string, object>;
            if (items == null) return false;

            foreach (var kvp in items)
            {
                if (!kvp.Key.StartsWith("slot") && !kvp.Key.StartsWith("stash")) continue;
                var itemData = kvp.Value as Dictionary<string, object>;
                if (itemData != null && itemData.ContainsKey("name") && itemData["name"].ToString() == itemName) return true;
            }
            return false;
        }

        private int GetItemCost(string itemName)
        {
            string apiName = itemName.Replace("item_", "");
            if (itemPrices.ContainsKey(apiName)) return itemPrices[apiName];
            return 0;
        }

        private bool IsConsumable(string name)
        {
            string[] consumables = {
                "tango", "tango_single", "clarity", "flask",
                "ward_observer", "ward_sentry", "ward_dispenser",
                "smoke_of_deceit", "dust", "tpscroll",
                "infused_raindrop", "blood_grenade",
                "cheese", "aegis", "refresher_shard", "royal_jelly",
                "moon_shard", "aghanims_shard", "ultimate_scepter_2",
                "tome_of_knowledge", "courier", "flying_courier", "bottle"
            };
            return Array.IndexOf(consumables, name) >= 0;
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
                    double l = double.Parse(parts[0]);
                    double t = double.Parse(parts[1]);

                    // 检查是否在屏幕内，防止带鱼屏/多屏切换后窗口丢失
                    if (l < 0 || l > SystemParameters.VirtualScreenWidth - 100) l = 200;
                    if (t < 0 || t > SystemParameters.VirtualScreenHeight - 40) t = 60;

                    this.Left = l;
                    this.Top = t;
                    this.isLocked = bool.Parse(parts[2]);

                    if (parts.Length >= 6)
                    {
                        hotkeyMods = uint.Parse(parts[3]);
                        hotkeyVk = uint.Parse(parts[4]);
                        hotkeyName = parts[5];
                    }
                    if (parts.Length >= 7)
                    {
                        dotaPath = parts[6];
                    }
                }
                else { this.Left = 200; this.Top = 60; }
            }
            catch { this.Left = 200; this.Top = 60; }
        }

        private void SaveConfig()
        {
            try 
            {
                // 保存前确保坐标有效
                double l = this.Left;
                double t = this.Top;
                File.WriteAllText("config.txt", string.Format("{0},{1},{2},{3},{4},{5},{6}", l, t, this.isLocked, hotkeyMods, hotkeyVk, hotkeyName, dotaPath)); 
            } 
            catch { }
        }

        private void EnsureGsiConfigExists()
        {
            // 先尝试从缓存配置读取并验证
            bool needsDetection = string.IsNullOrEmpty(dotaPath) || !Directory.Exists(dotaPath);
            
            if (needsDetection)
            {
                dotaPath = AutoDetectDotaPath();
            }

            if (string.IsNullOrEmpty(dotaPath) || !Directory.Exists(dotaPath))
            {
                var result = MessageBox.Show(
                    "未自动找到 Dota 2 安装路径。\n\n是否手动选择 dota2.exe 所在目录？\n(如果不配置此项，助手将无法获取游戏内资产数据)", 
                    "Dota2 资产助手 - 路径未找到", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    dotaPath = ManuallySelectDotaPath();
                }
            }

            if (!string.IsNullOrEmpty(dotaPath) && Directory.Exists(dotaPath))
            {
                string targetPath;
                string cfgContent;
                if (TryCreateGsiFile(dotaPath, out targetPath, out cfgContent))
                {
                    SaveConfig(); // 成功则保存路径
                }
                else
                {
                    try { Clipboard.SetText(cfgContent); } catch { } // 将内容写入剪贴板
                    
                    string errorMsg = string.Format(
                        "无法自动创建 GSI 配置文件。\n\n" +
                        "【目标路径】\n{0}\n\n" +
                        "可能是权限不足或杀毒软件拦截。\n" +
                        "为了不影响使用，配置内容已自动【复制到您的剪贴板】。\n\n" +
                        "【手动解决步骤】\n" +
                        "1. 手动打开上述路径（如果文件夹不存在请新建）。\n" +
                        "2. 新建一个文本文件，将剪贴板内容粘贴进去并保存。\n" +
                        "3. 将文件名修改为：gamestate_integration_networth.cfg\n\n" +
                        "(或者完全退出软件，右键选择“以管理员身份运行”重试)", 
                        targetPath);

                    MessageBox.Show(errorMsg, "配置创建失败 - 需手动处理", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private bool TryCreateGsiFile(string path, out string outCfgPath, out string outCfgContent)
        {
            string gsiFolder = Path.Combine(path, "game", "dota", "cfg", "gamestate_integration");
            
            // 兼容处理：防呆设计，处理用户选偏了目录的情况
            if (path.EndsWith("dota", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(path, "cfg")))
            {
                 gsiFolder = Path.Combine(path, "cfg", "gamestate_integration");
            }
            else if (path.EndsWith("game", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(path, "dota", "cfg")))
            {
                 gsiFolder = Path.Combine(path, "dota", "cfg", "gamestate_integration");
            }

            outCfgPath = Path.Combine(gsiFolder, "gamestate_integration_networth.cfg");
            outCfgContent = @"""Dota 2 Net Worth""
{
    ""uri""           ""http://127.0.0.1:3000/""
    ""timeout""       ""5.0""
    ""buffer""        ""0.1""
    ""throttle""      ""0.1""
    ""heartbeat""     ""10.0""
    ""data""
    {
        ""items""     ""1""
        ""player""    ""1""
        ""hero""      ""1""
    }
}";

            try
            {
                if (!Directory.Exists(gsiFolder)) Directory.CreateDirectory(gsiFolder);
                File.WriteAllText(outCfgPath, outCfgContent);
                return true;
            }
            catch
            {
                return false; // 写入失败，通常为权限问题
            }
        }

        private string AutoDetectDotaPath()
        {
            try
            {
                string path = null;
                string steamPath = null;

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null) steamPath = key.GetValue("SteamPath") as string;
                }

                if (!string.IsNullOrEmpty(steamPath))
                {
                    steamPath = steamPath.Replace("/", "\\");
                    string defaultLibrary = Path.Combine(steamPath, "steamapps", "common", "dota 2 beta");
                    if (Directory.Exists(defaultLibrary)) return defaultLibrary;

                    // 增强：尝试解析 steamapps\libraryfolders.vdf，应对游戏安装在非默认盘符的情况
                    string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdfPath))
                    {
                        string[] lines = File.ReadAllLines(vdfPath);
                        string currentLibraryPath = null;
                        foreach (string line in lines)
                        {
                            if (line.Contains("\"path\""))
                            {
                                int start = line.IndexOf("\"path\"") + 6;
                                int firstQuote = line.IndexOf('"', start);
                                int lastQuote = line.IndexOf('"', firstQuote + 1);
                                if (firstQuote != -1 && lastQuote != -1)
                                {
                                    currentLibraryPath = line.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Replace("\\\\", "\\");
                                }
                            }
                            // 570 是 Dota 2 的 AppID
                            if (line.Contains("\"570\"") && !string.IsNullOrEmpty(currentLibraryPath))
                            {
                                string possiblePath = Path.Combine(currentLibraryPath, "steamapps", "common", "dota 2 beta");
                                if (Directory.Exists(possiblePath)) return possiblePath;
                            }
                        }
                    }
                }

                // 备选方案：通过卸载注册表寻找 (32位和64位)
                string regPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 570";
                using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (RegistryKey subkey = key.OpenSubKey(regPath))
                        if (subkey != null) path = subkey.GetValue("InstallLocation") as string;
                }

                if (string.IsNullOrEmpty(path))
                {
                    using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    {
                        using (RegistryKey subkey = key.OpenSubKey(regPath))
                            if (subkey != null) path = subkey.GetValue("InstallLocation") as string;
                    }
                }

                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
            }
            catch { }
            return null;
        }

        private string ManuallySelectDotaPath()
        {
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog();
            ofd.Filter = "Dota 2 可执行文件 (dota2.exe)|dota2.exe|所有文件 (*.*)|*.*";
            ofd.Title = "请找到并选择 dota2.exe (通常位于 steamapps\\common\\dota 2 beta\\game\\bin\\win64\\)";
            
            if (ofd.ShowDialog() == true)
            {
                string path = Path.GetDirectoryName(ofd.FileName);
                DirectoryInfo di = new DirectoryInfo(path);
                // 向上追溯，提取出正确的 dota 2 beta 目录，防呆
                while (di != null)
                {
                    if (di.Name.Equals("dota 2 beta", StringComparison.OrdinalIgnoreCase) || 
                       (di.Name.Equals("game", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(di.FullName, "dota", "cfg"))))
                    {
                        if (di.Name.Equals("game", StringComparison.OrdinalIgnoreCase))
                            return di.Parent.FullName;
                        return di.FullName;
                    }
                    di = di.Parent;
                }
                return path; // 若都没匹配，返回所在目录让 TryCreate 容错处理
            }
            return null;
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