using System.Collections.Generic;

namespace Dota2NetWorth.Services
{
    /// <summary>一次 GSI 推送累积形成的快照。</summary>
    internal sealed class GsiSnapshot
    {
        /// <summary>是否处于"应当显示插件"的比赛状态（PRE_GAME / GAME_IN_PROGRESS）。</summary>
        public bool InMatch;
        /// <summary>原始 game_state 字符串，便于日志排查；可能为 null。</summary>
        public string GameState;

        public int Gold;
        /// <summary>slot → item_xxx 名（"empty" 表示空格）</summary>
        public Dictionary<string, string> Items = new Dictionary<string, string>();
        /// <summary>slot → 当前 charges</summary>
        public Dictionary<string, int> ItemCharges = new Dictionary<string, int>();

        public bool HasShard;
        public bool HasScepterBuff;
        public bool HasMoonShard;

        public void ResetMatchData()
        {
            Gold = 0;
            Items.Clear();
            ItemCharges.Clear();
            HasShard = false;
            HasScepterBuff = false;
            HasMoonShard = false;
        }
    }
}
