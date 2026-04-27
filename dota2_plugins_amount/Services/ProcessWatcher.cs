using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Dota2NetWorth.Services
{
    /// <summary>轮询 dota2.exe 进程；通过事件通知运行状态变化。</summary>
    internal sealed class ProcessWatcher : IDisposable
    {
        private readonly string _processName;
        private readonly TimeSpan _interval;
        private CancellationTokenSource _cts;
        private Task _task;

        public bool IsRunning { get; private set; }
        public event Action<bool> OnRunningChanged;

        public ProcessWatcher(string processName = "dota2", int intervalMs = 3000)
        {
            _processName = processName;
            _interval = TimeSpan.FromMilliseconds(intervalMs);
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => LoopAsync(_cts.Token));
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    bool now = Process.GetProcessesByName(_processName).Length > 0;
                    if (now != IsRunning)
                    {
                        IsRunning = now;
                        try { OnRunningChanged?.Invoke(now); }
                        catch (Exception ex) { Logger.Warn("OnRunningChanged 处理异常: " + ex.Message); }
                    }
                }
                catch (Exception ex) { Logger.Warn("ProcessWatcher: " + ex.Message); }

                try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _task?.Wait(500); } catch { }
        }
    }
}
