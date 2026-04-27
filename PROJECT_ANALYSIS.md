# Dota2 资产插件 — 代码分析报告

## 一、项目功能与作用

### 1.1 定位
一个基于 **C# / WPF (.NET Framework)** 的 **Dota 2 实时总资产 (Net Worth) 桌面悬浮窗** 工具。在游戏过程中以无边框、置顶、可点击穿透的小窗口形式，实时展示当前英雄的「金钱 + 所有装备价值 + 永久 buff（魔晶/神杖/月之碎片）」之和。

### 1.2 数据流
```
Dota2 客户端
   │  (GSI 推送 JSON, POST)
   ▼
本地 HttpListener  http://127.0.0.1:3000/
   │  反序列化 (JavaScriptSerializer)
   ▼
GSI 缓存池 (cacheGold / cacheItems / cacheItemCharges / cacheShard ...)
   │  与 item_price.json 中的单价匹配累加
   ▼
Dispatcher.Invoke → TextLabel.Text  (悬浮窗 UI)
```

### 1.3 核心模块（均集中在 `MainWindow.xaml.cs`）
| 模块 | 入口 | 作用 |
|---|---|---|
| GSI 配置自动写入 | `EnsureGsiConfigExists` / `TryCreateGsiFile` | 自动定位 Dota 2 安装目录并写入 `gamestate_integration_networth.cfg` |
| Dota 安装路径自动检测 | `AutoDetectDotaPath` | 通过注册表 `HKCU\Software\Valve\Steam` + `libraryfolders.vdf` + 卸载注册表三重定位 |
| 本地 HTTP 服务 | `StartGsiServer` / `ProcessGsiRequest` | 监听 3000 端口，解析 GSI 数据并更新缓存 |
| 进程监控 | `StartProcessMonitor` | 每 3 秒轮询 `dota2.exe`，控制窗口显隐 |
| 全局快捷键 | `RegisterHotKey` + `HwndHook` | 固定 `Ctrl+Alt+F10` 切换锁定 / 解锁（拖拽）状态 |
| 点击穿透 | `ApplyLockState` + `WS_EX_TRANSPARENT` | 锁定时鼠标穿透，不影响游戏点击 |
| 配置持久化 | `LoadConfig` / `SaveConfig` | `config.txt` 保存窗口位置、锁定状态、Dota 路径 |
| 开机自启 | `EnsureAutoStart` | 写入 `HKCU\...\Run` 注册表项 |

### 1.4 价格库 `item_price.json`
按物品 API 名为 key，包含 `cost`（基础价）、`qual`（quality，识别 consumable）、`charges`（最大充能数）。对消耗品按 `当前充能 / 最大充能` 比例折价。

---

## 二、严重问题 (Bugs / 风险)

### 🔴 B1. 命名空间分裂 — 项目结构混乱
- `App.xaml`：`x:Class="dota2_plugins_amount.App"`，`App.xaml.cs` 使用 `namespace dota2_plugins_amount`
- `MainWindow.xaml.cs`：`namespace Dota2NetWorth`
- `MainWindow.xaml` 的 `x:Class` 必须也是 `Dota2NetWorth.MainWindow` 才能编译通过

**问题**：两个命名空间并存，没有统一标识，后续维护极易踩坑（资源 URI、`pack://` 路径、反射查找等都会受影响）。建议统一为 `Dota2NetWorth`。

### 🔴 B2. README 与实现严重不符
README 宣传：
> *"**系统托盘支持**: 支持托盘运行、右键菜单退出及快捷键修改"*

但代码中：
- 没有任何 `NotifyIcon` / 托盘图标实现
- `HOTKEY_MODS` / `HOTKEY_VK` 是 `private const`，**硬编码无法修改**
- `LoadConfig` 注释里写 _"跳过位置 3/4/5 的 hotkey 字段"_，说明历史曾支持过但已被砍掉

**建议**：要么补回托盘 + 快捷键修改 UI；要么修改 README 删除该宣传。

### 🔴 B3. 全部异常被静默吞掉 (`catch { }`)
全文出现 **8+ 处** 空 catch：
- `StartGsiServer` — 端口 3000 被占用时无任何提示，UI 永远显示 `Ready`
- `ProcessGsiRequest` — JSON 解析失败无日志
- `LoadItemPrices` — 价格表加载失败也静默
- `LoadConfig` / `SaveConfig` / `EnsureAutoStart`

**风险**：用户报 Bug 时无任何线索，开发者无法复现。**建议**：至少写入本地日志文件 `error.log`，或记入 Windows 事件日志。

### 🔴 B4. `async void` + 死循环，缺少取消机制
```csharp
private async void StartProcessMonitor() { while (true) { ... await Task.Delay(3000); } }
private async void StartGsiServer()      { while (true) { ... } }
private async void ProcessGsiRequest(...) { ... }
```
- `async void` 抛异常会**直接终止进程**（不可被外层 try 捕获）
- `OnClosed` 中只 `UnregisterHotKey`，**没有** `gsiListener.Stop()`，也没有 `CancellationToken`，关闭窗口后线程仍在运行
- 应改为 `async Task` + `CancellationTokenSource`，并在 `OnClosed` 调用 `Cancel()` + `gsiListener.Close()`

### 🔴 B5. 工作目录依赖 — 开机自启时大概率失效
```csharp
File.ReadAllText("item_price.json");        // 相对路径
File.WriteAllText("config.txt", ...);       // 相对路径
```
开机自启 (`EnsureAutoStart`) 由 `explorer.exe` 拉起时，**工作目录不一定是 exe 所在目录**（某些场景为 `System32`），会导致 `item_price.json` 读不到、`config.txt` 写到奇怪位置。

**修复**：
```csharp
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
File.ReadAllText(Path.Combine(baseDir, "item_price.json"));
```

### 🔴 B6. 强制开机自启，无用户开关
`MainWindow` 构造里**无条件**调用 `EnsureAutoStart()`，覆盖写入注册表。
- 用户从未授权该行为
- 卸载后注册表残留
- 部分企业环境可能误报为流氓软件

**建议**：默认关闭，提供托盘菜单开关；或首次运行弹窗征询。

### 🟠 B7. `LoadConfig` 文化无关性 / 越界
```csharp
double l = double.Parse(parts[0]);   // 未指定 CultureInfo
this.isLocked = bool.Parse(parts[2]);
```
- 在德语 / 法语等使用逗号作为小数点的系统上，`double.Parse` 会把保存值解析失败（虽然这里因为整数 px 大概不会出小数，但仍是隐患）
- 数组长度未校验：`parts[0..2]` 在 config 损坏时抛 `IndexOutOfRange`（被外层 catch 兜住但默默丢配置）

**修复**：使用 `double.Parse(s, CultureInfo.InvariantCulture)`，并先判断 `parts.Length`。

### 🟠 B8. 配置文件格式脆弱
逗号分隔 + `dotaPath` 字段在第 7 位：
```
left,top,locked,,,,dotaPath
```
若 `dotaPath` 含逗号（极少但可能），SaveConfig / LoadConfig 全部错位。**建议**改用 JSON 或 INI。

### 🟠 B9. `inMatch` 误判
仅以 `hero.name` 非空判定"对局中"，**观战 / 录像 / 教程任务** 也会进入显示状态；而英雄选择阶段 `hero.name` 已被填充，金钱 0 也会显示一个 "0"。

### 🟠 B10. `JavaScriptSerializer` 已过时
属于 `System.Web.Extensions`，性能差，对大 JSON 容易踩坑（默认 `MaxJsonLength = 2MB` 上限）。**建议** `Newtonsoft.Json` 或 .NET 4.6.1+ 引用 `System.Text.Json` 包。

### 🟠 B11. UI 线程阻塞
```csharp
Dispatcher.Invoke(new Action(() => TextLabel.Text = ...));
```
`Invoke` 是同步阻塞，每收到一次 GSI 包（默认 throttle 0.1s 即 10Hz）就阻塞一次后台线程。**建议**改 `Dispatcher.BeginInvoke` 异步派发。

### 🟠 B12. `HttpListener` 单线程串行处理
`while(true)` 中 `await GetContextAsync()` → `await ProcessGsiRequest()`，但 `ProcessGsiRequest` 是 `async void`，实际并不会被 await，存在请求并发风险（GSI 一般串行所以问题不大，但代码意图不清）。

---

## 三、可优化点 (代码质量)

### 🟡 O1. `Dictionary` 双重查表
全文大量：
```csharp
if (itemPrices.ContainsKey(apiName)) { int p = itemPrices[apiName]; ... }
```
两次哈希查找。**优化**：
```csharp
if (itemPrices.TryGetValue(apiName, out int price)) { ... }
```

### 🟡 O2. `slotKeys` 每次请求重新 new
```csharp
var slotKeys = new List<string>();
for (int i = 0; i < 9; i++) slotKeys.Add("slot" + i);
for (int i = 0; i < 6; i++) slotKeys.Add("stash" + i);
```
应提为 `static readonly string[]`。

### 🟡 O3. 物品槽位覆盖不全
当前只取 `slot0~slot8` + `stash0~stash5`。Dota 2 GSI 实际还可能有：
- `neutral0`（中立物品槽，**有售价**，应计入）
- `teleport0`（TP，便宜可忽略）
- 背包是 `slot6/7/8`，已覆盖 ✓

**建议**：补 `neutral0`。

### 🟡 O4. `DispatcherTimer` 替代 Process 轮询
`Process.GetProcessesByName("dota2")` 每 3 秒调用一次，会枚举系统全部进程。可：
- 用 `ManagementEventWatcher` 监听 `Win32_ProcessStartTrace` / `StopTrace` 事件
- 或将间隔扩大到 5–10 秒（用户感知不明显）

### 🟡 O5. 价格表/规则数据结构应合并
`itemPrices` / `itemIsConsumable` / `itemMaxCharges` 三个并行字典，本质是同一物品的多个属性，应合并为：
```csharp
class ItemInfo { public int Cost; public bool IsConsumable; public int? MaxCharges; }
Dictionary<string, ItemInfo> itemDb;
```

### 🟡 O6. 魔法数字 / 字符串
- `HOTKEY_ID = 9000`、端口 `3000`、`AppID 570` 散落代码中，应集中到常量类
- buff 名称字符串 `"modifier_item_aghanims_shard"` 等可以做成 `Dictionary<string, string buffName→priceKey>` 的映射，便于日后扩展（比如新增"乙太之魂"等永久 buff）

### 🟡 O7. WPF 资源未使用 Binding
`TextLabel.Text = totalNetWorth.ToString("N0")` 直接操作控件。改用 MVVM + INotifyPropertyChanged + Binding 可以：
- 移除 `Dispatcher.Invoke`（Binding 自动 marshaling）
- UI/逻辑解耦，便于单元测试

### 🟡 O8. 单文件 700+ 行 — 拆分职责
`MainWindow.xaml.cs` 同时承担：UI、HTTP server、GSI 解析、注册表、文件 IO、进程监控、快捷键。**建议**至少拆为：
```
Services/
  GsiServer.cs
  GsiParser.cs
  DotaPathLocator.cs
  ConfigStore.cs
  ProcessWatcher.cs
  HotKeyManager.cs
ViewModels/
  NetWorthViewModel.cs
MainWindow.xaml.cs    // 仅做 View 绑定
```

### 🟡 O9. `EnsureAutoStart` 路径
```csharp
Process.GetCurrentProcess().MainModule.FileName
```
`MainModule` 在 32/64 位互访时可能抛 `Win32Exception`。建议用：
```csharp
System.Reflection.Assembly.GetEntryAssembly().Location
```
或 `Environment.ProcessPath`（.NET 6+）。

### 🟡 O10. UI 上未处理 DPI 缩放
`SystemParameters.VirtualScreenWidth` 默认 DIP 单位，但用户在多 DPI 屏切换时窗口可能漂移。可在 `App.manifest` 声明 PerMonitorV2，或动态判断屏幕区域。

---

## 四、安全与依赖

| 项 | 现状 | 建议 |
|---|---|---|
| HttpListener 绑定 | `127.0.0.1` ✅ 已限本机 | OK |
| GSI URI | `http://127.0.0.1:3000/` 明文 | 局域网无嗅探风险，可保留 |
| 注册表写入 | HKCU 自启 + 路径检测 | 加用户授权开关 |
| 文件写入 | 直接写 Dota 2 安装目录 | 已有 try/catch + 剪贴板兜底 ✅ |
| 第三方库 | 仅引用 .NET Framework BCL | 0 依赖 ✅ |
| `JavaScriptSerializer` | 弃用 API | 迁移 `System.Text.Json` |

---

## 五、修复优先级建议

| 优先级 | 项 |
|---|---|
| P0 | B1 命名空间统一、B3 异常日志、B5 工作目录、B6 自启用户开关 |
| P1 | B2 README 与实现对齐、B4 取消机制、B11 BeginInvoke |
| P2 | O1/O2/O5/O6 重构、B10 替换 JSON 库 |
| P3 | O8 分层、O7 MVVM、O3 neutral 槽位 |

---

## 六、总体评价

**优点**
- 单工程零依赖，开箱即用
- 自动定位 Dota 路径 + 自动写 GSI cfg + 剪贴板兜底，**用户侧体验很好**
- 锁定/解锁、点击穿透、屏幕越界纠正等细节考虑到位
- 多盘符 Steam 库 (`libraryfolders.vdf`) 解析周到

**主要短板**
- 工程结构「面条式」，所有逻辑挤在 `MainWindow.xaml.cs`
- 防御性编程过头（满屏空 catch），导致问题排查困难
- README 与实现存在功能落差（托盘 / 快捷键修改）
- 强制开机自启 + 无配置开关，不够"克制"
