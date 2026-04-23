using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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

        // --- 固定快捷键 Ctrl+Alt+F10 ---
        private const uint HOTKEY_MODS = 0x0001 | 0x0002; // Alt + Ctrl
        private const uint HOTKEY_VK = 0x79; // F10

        private string dotaPath = null;
        private Dictionary<string, int> itemPrices = new Dictionary<string, int>();
        private Dictionary<string, bool> itemIsConsumable = new Dictionary<string, bool>();
        private Dictionary<string, int> itemMaxCharges = new Dictionary<string, int>();
        private HttpListener gsiListener;

        private int cacheGold = 0;
        private bool cacheShard = false;
        private bool cacheScepterBuff = false;
        private bool cacheMoonShard = false;
        private Dictionary<string, string> cacheItems = new Dictionary<string, string>();
        private Dictionary<string, int> cacheItemCharges = new Dictionary<string, int>();

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
            LoadItemPrices();
            EnsureGsiConfigExists();
            EnsureAutoStart();

            StartGsiServer();
            StartProcessMonitor();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(HwndHook);

            RegisterHotKey(handle, HOTKEY_ID, HOTKEY_MODS, HOTKEY_VK);
            ApplyLockState();
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

                    bool currentInMatch = false;
                    if (root.ContainsKey("hero"))
                    {
                        var heroProbe = root["hero"] as Dictionary<string, object>;
                        if (heroProbe != null && heroProbe.ContainsKey("name"))
                        {
                            var heroName = heroProbe["name"] as string;
                            currentInMatch = !string.IsNullOrEmpty(heroName);
                        }
                    }
                    if (!currentInMatch)
                    {
                        cacheGold = 0; cacheItems.Clear(); cacheItemCharges.Clear(); cacheShard = false; cacheScepterBuff = false; cacheMoonShard = false;
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
                            var slotKeys = new List<string>();
                            for (int i = 0; i < 9; i++) slotKeys.Add("slot" + i);
                            for (int i = 0; i < 6; i++) slotKeys.Add("stash" + i);

                            foreach (string slot in slotKeys)
                            {
                                if (items.ContainsKey(slot))
                                {
                                    var itemData = items[slot] as Dictionary<string, object>;
                                    if (itemData != null && itemData.ContainsKey("name"))
                                    {
                                        string name = itemData["name"] as string;
                                        cacheItems[slot] = string.IsNullOrEmpty(name) ? "empty" : name;
                                        if (itemData.ContainsKey("charges"))
                                        {
                                            try { cacheItemCharges[slot] = Convert.ToInt32(itemData["charges"]); }
                                            catch { cacheItemCharges.Remove(slot); }
                                        }
                                        else cacheItemCharges.Remove(slot);
                                    }
                                    else { cacheItems[slot] = "empty"; cacheItemCharges.Remove(slot); }
                                }
                            }
                        }
                    }

                    if (root.ContainsKey("hero"))
                    {
                        var hero = root["hero"] as Dictionary<string, object>;
                        if (hero != null)
                        {
                            cacheShard = false; cacheScepterBuff = false; cacheMoonShard = false;
                            if (hero.ContainsKey("permanent_buffs"))
                            {
                                var buffs = hero["permanent_buffs"] as Dictionary<string, object>;
                                if (buffs != null)
                                {
                                    if (buffs.ContainsKey("modifier_item_aghanims_shard")) cacheShard = true;
                                    if (buffs.ContainsKey("modifier_item_ultimate_scepter_consumed")) cacheScepterBuff = true;
                                    if (buffs.ContainsKey("modifier_item_moon_shard_consumed")) cacheMoonShard = true;
                                }
                            }
                        }
                    }

                    int totalNetWorth = cacheGold;

                    foreach (var kv in cacheItems)
                    {
                        string slot = kv.Key;
                        string item = kv.Value;
                        if (item == "empty") continue;

                        string apiName = item.Replace("item_", "");
                        if (!itemPrices.ContainsKey(apiName)) continue;

                        int price = itemPrices[apiName];
                        bool isConsumable = itemIsConsumable.ContainsKey(apiName) && itemIsConsumable[apiName];
                        if (isConsumable && itemMaxCharges.ContainsKey(apiName) && cacheItemCharges.ContainsKey(slot))
                        {
                            int maxCh = itemMaxCharges[apiName];
                            int curCh = cacheItemCharges[slot];
                            if (maxCh > 0 && curCh >= 0 && curCh <= maxCh)
                                price = price * curCh / maxCh;
                        }
                        totalNetWorth += price;
                    }

                    if (cacheShard && itemPrices.ContainsKey("aghanims_shard")) totalNetWorth += itemPrices["aghanims_shard"];
                    if (cacheScepterBuff && itemPrices.ContainsKey("ultimate_scepter")) totalNetWorth += itemPrices["ultimate_scepter"];
                    if (cacheMoonShard && itemPrices.ContainsKey("moon_shard")) totalNetWorth += itemPrices["moon_shard"];

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
                        if (valDict != null && valDict.ContainsKey("qual"))
                        {
                            itemIsConsumable[kvp.Key] = (valDict["qual"] as string) == "consumable";
                        }
                        if (valDict != null && valDict.ContainsKey("charges"))
                        {
                            var ch = valDict["charges"];
                            if (ch is int || ch is long || ch is double || ch is decimal)
                            {
                                try
                                {
                                    int maxCh = Convert.ToInt32(ch);
                                    if (maxCh > 0) itemMaxCharges[kvp.Key] = maxCh;
                                }
                                catch { }
                            }
                        }
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

                    // 兼容旧版本 config：跳过位置 3/4/5 的 hotkey 字段，dotaPath 位于位置 6
                    if (parts.Length >= 7)
                    {
                        dotaPath = parts[6];
                    }
                    else if (parts.Length >= 4)
                    {
                        dotaPath = parts[3];
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
                double l = this.Left;
                double t = this.Top;
                File.WriteAllText("config.txt", string.Format("{0},{1},{2},,,,{3}", l, t, this.isLocked, dotaPath)); 
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

        protected override void OnClosed(EventArgs e)
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID);
            base.OnClosed(e);
        }

        private void EnsureAutoStart()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    string existing = key.GetValue("Dota2NetWorth") as string;
                    if (!string.Equals(existing, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue("Dota2NetWorth", exePath);
                    }
                }
            }
            catch { }
        }
    }
}