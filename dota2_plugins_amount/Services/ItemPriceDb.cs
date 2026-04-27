using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Dota2NetWorth.Services
{
    internal sealed class ItemInfo
    {
        public int Cost;
        public bool IsConsumable;
        public int? MaxCharges;
    }

    /// <summary>
    /// item_price.json 加载 + 价格查询。
    /// </summary>
    internal sealed class ItemPriceDb
    {
        private readonly Dictionary<string, ItemInfo> _items = new Dictionary<string, ItemInfo>();

        public bool TryGet(string apiName, out ItemInfo info)
        {
            return _items.TryGetValue(apiName, out info);
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(PathProvider.ItemPriceJson))
                {
                    Logger.Error("找不到价格表 " + PathProvider.ItemPriceJson);
                    return;
                }
                string json = File.ReadAllText(PathProvider.ItemPriceJson);
                var jss = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var dict = jss.Deserialize<Dictionary<string, object>>(json);
                if (dict == null) return;

                foreach (var kvp in dict)
                {
                    var v = kvp.Value as Dictionary<string, object>;
                    if (v == null) continue;

                    var info = new ItemInfo();
                    object costObj;
                    if (v.TryGetValue("cost", out costObj))
                    {
                        try { info.Cost = Convert.ToInt32(costObj); } catch { continue; }
                    }
                    object qualObj;
                    if (v.TryGetValue("qual", out qualObj))
                    {
                        info.IsConsumable = (qualObj as string) == "consumable";
                    }
                    object chObj;
                    if (v.TryGetValue("charges", out chObj) &&
                        (chObj is int || chObj is long || chObj is double || chObj is decimal))
                    {
                        try
                        {
                            int max = Convert.ToInt32(chObj);
                            if (max > 0) info.MaxCharges = max;
                        }
                        catch { }
                    }
                    _items[kvp.Key] = info;
                }
                Logger.Info("已加载物品价格表：" + _items.Count + " 条。");
            }
            catch (Exception ex) { Logger.Error("加载价格表失败", ex); }
        }
    }
}
