using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Dota2NetWorth.ViewModels
{
    internal sealed class NetWorthViewModel : INotifyPropertyChanged
    {
        private string _displayText = "Ready";
        public string DisplayText
        {
            get => _displayText;
            set { if (_displayText != value) { _displayText = value; Raise(); } }
        }

        public void UpdateNetWorth(int total) => DisplayText = total.ToString("N0");
        public void Reset() => DisplayText = "Ready";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
