using System;
using System.Runtime.InteropServices;

namespace Dota2NetWorth.Services
{
    /// <summary>全局快捷键封装。固定 Ctrl+Alt+F10。</summary>
    internal sealed class HotKeyManager
    {
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        public const int HOTKEY_ID = 9000;
        public const uint MOD_ALT_CTRL = 0x0001 | 0x0002; // Alt + Ctrl
        public const uint VK_F10 = 0x79;
        public const int WM_HOTKEY = 0x0312;

        private IntPtr _hwnd;
        public event Action OnPressed;

        public void Register(IntPtr hwnd)
        {
            _hwnd = hwnd;
            if (!RegisterHotKey(hwnd, HOTKEY_ID, MOD_ALT_CTRL, VK_F10))
                Logger.Warn("注册全局快捷键 Ctrl+Alt+F10 失败（可能已被占用）。");
        }

        public bool HandleWmHotKey(int msg, IntPtr wParam)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                try { OnPressed?.Invoke(); }
                catch (Exception ex) { Logger.Warn("HotKey 回调异常: " + ex.Message); }
                return true;
            }
            return false;
        }

        public void Unregister()
        {
            try { UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
        }
    }
}
