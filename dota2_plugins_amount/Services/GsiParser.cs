using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace Dota2NetWorth.Services
{
    /// <summary>
    /// 解析 GSI JSON 增量包，并把数据合并入 cache snapshot（GSI 是 delta update）。
    ///
    /// 关键设计原则（修复 BUG 2）：
    ///   - delta 包中"字段不存在" → 保留旧值；只在"字段存在但表示空/重置"时才清除。
    ///   - 比赛是否进行中以 map.game_state 为准（不再依赖 hero.name 是否为空）。
    ///   - 离开比赛（POST_GAME / 无 map / 无 hero）时清装备，但避免被偶发 delta 误清。
    /// </summary>
    internal sealed class GsiParser
    {
        private static readonly string[] SlotKeys = BuildSlotKeys();

        private static string[] BuildSlotKeys()
        {
            var list = new List<string>(16);
            for (int i = 0; i < 9; i++) list.Add("slot" + i);
            for (int i = 0; i < 6; i++) list.Add("stash" + i);
            list.Add("neutral0"); // 中立物品
            return list.ToArray();
        }

        // 仅这两个状态下才显示资产
        private static readonly HashSet<string> InMatchStates = new HashSet<string>
        {
            "DOTA_GAMERULES_STATE_PRE_GAME",
            "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS"
        };

        // 这些状态明确表示"已离开比赛"，应清装备缓存
        private static readonly HashSet<string> OutOfMatchStates = new HashSet<string>
        {
            "DOTA_GAMERULES_STATE_POST_GAME",
            "DOTA_GAMERULES_STATE_DISCONNECT"
        };

        private readonly GsiSnapshot _cache = new GsiSnapshot();
        public GsiSnapshot Snapshot { get { return _cache; } }

        /// <summary>解析一帧 GSI 数据；返回 true 代表数据有更新。</summary>
        public bool Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var jss = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var root = jss.Deserialize<Dictionary<string, object>>(json);
                if (root == null) return false;

                // ---------- map.game_state ----------
                bool sawMap = false;
                string newGameState = _cache.GameState;
                object mapObj;
                if (root.TryGetValue("map", out mapObj))
                {
                    sawMap = true;
                    var map = mapObj as Dictionary<string, object>;
                    object stateObj;
                    if (map != null && map.TryGetValue("game_state", out stateObj))
                    {
                        newGameState = stateObj as string;
                    }
                }

                // 决定 inMatch（基于权威 game_state）
                bool inMatchByState;
                if (!string.IsNullOrEmpty(newGameState))
                {
                    inMatchByState = InMatchStates.Contains(newGameState);
                }
                else
                {
                    // 没有 game_state 信息（比如旧 cfg 没启用 map 字段），回退到原启发式：hero.name 非空
                    inMatchByState = HasNonEmptyHeroName(root) || _cache.InMatch;
                }

                bool isExplicitOut = !string.IsNullOrEmpty(newGameState) && OutOfMatchStates.Contains(newGameState);
                _cache.GameState = newGameState;

                // 状态切换：进入 → 重置；离开（明确）→ 重置
                bool prevInMatch = _cache.InMatch;
                _cache.InMatch = inMatchByState;
                if (!prevInMatch && inMatchByState)
                {
                    _cache.ResetMatchData();
                    Logger.Info("进入比赛 (game_state=" + (newGameState ?? "<null>") + ")");
                }
                else if (prevInMatch && !inMatchByState)
                {
                    Logger.Info("离开比赛 (game_state=" + (newGameState ?? "<null>") + ", explicitOut=" + isExplicitOut + ")");
                    if (isExplicitOut) _cache.ResetMatchData();
                    // 否则保留缓存，直到 watchdog 超时或下一次明确状态
                }

                // 不在比赛中：保留缓存（除非明确 POST_GAME），但允许 watchdog 通过 InMatch=false 隐藏 UI
                // 仍需更新可见到的 player/items/hero（POST_GAME 期间装备其实还有，但 InMatch=false 会让 UI 隐藏）

                // ---------- player.gold（仅当字段出现才覆盖）----------
                object playerObj;
                if (root.TryGetValue("player", out playerObj))
                {
                    var player = playerObj as Dictionary<string, object>;
                    object goldObj;
                    if (player != null && player.TryGetValue("gold", out goldObj))
                    {
                        try
                        {
                            int newGold = Convert.ToInt32(goldObj);
                            if (_cache.Gold != newGold) Logger.Debug("Gold: " + _cache.Gold + " -> " + newGold);
                            _cache.Gold = newGold;
                        }
                        catch { }
                    }
                }

                // ---------- items（delta：仅遍历出现的 slot 字段）----------
                object itemsObj;
                if (root.TryGetValue("items", out itemsObj))
                {
                    var items = itemsObj as Dictionary<string, object>;
                    if (items != null) UpdateItemsFromDelta(items);
                }

                // ---------- hero.permanent_buffs（delta：字段缺失就保留）----------
                object heroObj;
                if (root.TryGetValue("hero", out heroObj))
                {
                    var hero = heroObj as Dictionary<string, object>;
                    if (hero != null)
                    {
                        object buffsObj;
                        if (hero.TryGetValue("permanent_buffs", out buffsObj))
                        {
                            var buffs = buffsObj as Dictionary<string, object>;
                            if (buffs != null)
                            {
                                bool s = buffs.ContainsKey("modifier_item_aghanims_shard");
                                bool sc = buffs.ContainsKey("modifier_item_ultimate_scepter_consumed");
                                bool ms = buffs.ContainsKey("modifier_item_moon_shard_consumed");
                                if (s != _cache.HasShard) Logger.Debug("Buff Shard: " + _cache.HasShard + " -> " + s);
                                if (sc != _cache.HasScepterBuff) Logger.Debug("Buff ScepterConsumed: " + _cache.HasScepterBuff + " -> " + sc);
                                if (ms != _cache.HasMoonShard) Logger.Debug("Buff MoonShard: " + _cache.HasMoonShard + " -> " + ms);
                                _cache.HasShard = s;
                                _cache.HasScepterBuff = sc;
                                _cache.HasMoonShard = ms;
                            }
                            // permanent_buffs 不是字典（理论 GSI 不会发，但防御）：保留旧值
                        }
                        // hero 字段无 permanent_buffs 子字段：保留旧 buff
                    }
                }

                Logger.Debug("Parsed: state=" + (_cache.GameState ?? "<null>") +
                             ", inMatch=" + _cache.InMatch +
                             ", gold=" + _cache.Gold +
                             ", items=" + _cache.Items.Count +
                             ", buffs=[shard=" + _cache.HasShard + ",sc=" + _cache.HasScepterBuff + ",moon=" + _cache.HasMoonShard + "]");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("GSI 解析失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// items delta 更新：
        ///   - 槽位字段不存在 → 保留旧值
        ///   - 槽位 name="empty" → 标记为空
        ///   - 槽位 charges 字段缺失 → 保留旧 charges（避免误清）
        /// </summary>
        private void UpdateItemsFromDelta(Dictionary<string, object> items)
        {
            foreach (string slot in SlotKeys)
            {
                object itemBox;
                if (!items.TryGetValue(slot, out itemBox)) continue; // 字段缺失 → 保留

                var itemData = itemBox as Dictionary<string, object>;
                if (itemData == null)
                {
                    // 槽位为 null：视为空格
                    if (!_cache.Items.ContainsKey(slot) || _cache.Items[slot] != "empty")
                        Logger.Debug("Slot " + slot + ": -> empty (null)");
                    _cache.Items[slot] = "empty";
                    _cache.ItemCharges.Remove(slot);
                    continue;
                }

                object n;
                if (itemData.TryGetValue("name", out n))
                {
                    string name = n as string;
                    string newName = string.IsNullOrEmpty(name) ? "empty" : name;
                    string oldName;
                    if (!_cache.Items.TryGetValue(slot, out oldName) || oldName != newName)
                        Logger.Debug("Slot " + slot + ": " + (oldName ?? "<none>") + " -> " + newName);
                    _cache.Items[slot] = newName;
                }
                // name 字段缺失：保留旧物品名

                object chObj;
                if (itemData.TryGetValue("charges", out chObj))
                {
                    try { _cache.ItemCharges[slot] = Convert.ToInt32(chObj); }
                    catch { }
                }
                // charges 字段缺失：保留旧值（不再 Remove）
            }
        }

        private static bool HasNonEmptyHeroName(Dictionary<string, object> root)
        {
            object heroObj;
            if (!root.TryGetValue("hero", out heroObj)) return false;
            var hero = heroObj as Dictionary<string, object>;
            object n;
            if (hero == null || !hero.TryGetValue("name", out n)) return false;
            return !string.IsNullOrEmpty(n as string);
        }

        /// <summary>watchdog 触发：长时间未收 GSI，强制视为离开比赛。</summary>
        public void MarkOffline()
        {
            if (_cache.InMatch)
            {
                Logger.Warn("GSI 数据超时未收到，强制标记为离开比赛。");
            }
            _cache.InMatch = false;
            _cache.GameState = null;
            _cache.ResetMatchData();
        }
    }
}
