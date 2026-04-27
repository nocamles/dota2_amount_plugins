namespace Dota2NetWorth.Services
{
    /// <summary>
    /// 根据 GSI 快照 + 价格表计算总资产。
    /// </summary>
    internal static class NetWorthCalculator
    {
        public static int Calculate(GsiSnapshot snap, ItemPriceDb db)
        {
            if (snap == null || !snap.InMatch) return 0;
            int total = snap.Gold;
            Logger.Debug("Calc start: gold=" + snap.Gold);

            foreach (var kv in snap.Items)
            {
                string slot = kv.Key;
                string item = kv.Value;
                if (item == "empty") continue;

                string apiName = item.StartsWith("item_") ? item.Substring(5) : item;
                ItemInfo info;
                if (!db.TryGet(apiName, out info))
                {
                    Logger.Debug("  [" + slot + "] " + item + " (无价格表条目，跳过)");
                    continue;
                }

                int price = info.Cost;
                int curCh;
                bool charged = info.IsConsumable && info.MaxCharges.HasValue
                               && snap.ItemCharges.TryGetValue(slot, out curCh)
                               && info.MaxCharges.Value > 0 && curCh >= 0 && curCh <= info.MaxCharges.Value;
                if (charged)
                {
                    int max = info.MaxCharges.Value;
                    int cur = snap.ItemCharges[slot];
                    int orig = price;
                    price = price * cur / max;
                    Logger.Debug("  [" + slot + "] " + item + " base=" + orig + " *" + cur + "/" + max + " = " + price);
                }
                else
                {
                    Logger.Debug("  [" + slot + "] " + item + " = " + price);
                }
                total += price;
            }

            ItemInfo shard, sc, ms;
            if (snap.HasShard && db.TryGet("aghanims_shard", out shard))
            {
                total += shard.Cost;
                Logger.Debug("  [buff] aghanims_shard = " + shard.Cost);
            }
            if (snap.HasScepterBuff && db.TryGet("ultimate_scepter", out sc))
            {
                total += sc.Cost;
                Logger.Debug("  [buff] ultimate_scepter (consumed) = " + sc.Cost);
            }
            if (snap.HasMoonShard && db.TryGet("moon_shard", out ms))
            {
                total += ms.Cost;
                Logger.Debug("  [buff] moon_shard (consumed) = " + ms.Cost);
            }

            Logger.Debug("Calc total = " + total);
            return total;
        }
    }
}
