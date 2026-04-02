using System.Windows;
using System.Windows.Input;

namespace Dota2NetWorth
{
    public partial class HotkeyWindow : Window
    {
        public uint Modifiers { get; private set; }
        public uint VirtualKey { get; private set; }
        public string HotkeyName { get; private set; }
        public bool IsSuccess { get; private set; }

        public HotkeyWindow(string currentHotkey)
        {
            InitializeComponent();
            KeyTextBox.Text = currentHotkey;
            IsSuccess = false;
        }

        private void KeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true; // 拦截键盘事件，防止乱输入字符

            // 过滤掉单独按下的 Ctrl/Alt/Shift 键，等待加上主按键
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            uint mods = 0;
            string name = "";

            // 判断组合修饰键
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) { mods |= 0x0002; name += "Ctrl + "; }
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) { mods |= 0x0001; name += "Alt + "; }
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) { mods |= 0x0004; name += "Shift + "; }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            name += key.ToString();

            // 记录下解析结果
            Modifiers = mods;
            VirtualKey = vk;
            HotkeyName = name;

            KeyTextBox.Text = name;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (VirtualKey != 0)
            {
                IsSuccess = true;
                this.Close();
            }
            else
            {
                MessageBox.Show("快捷键不能为空！请按下组合键。");
            }
        }
    }
}