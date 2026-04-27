using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Dota2NetWorth.Services
{
    /// <summary>
    /// 本地 HTTP 服务器，接收 Dota 2 GSI POST。
    /// 通过 CancellationToken 支持优雅关闭。
    /// </summary>
    internal sealed class GsiServer : IDisposable
    {
        private readonly string _prefix;
        private readonly HttpListener _listener = new HttpListener();
        private CancellationTokenSource _cts;
        private Task _loopTask;

        /// <summary>最近一次成功收到 payload 的本地时间。</summary>
        public DateTime LastReceivedAt { get; private set; } = DateTime.MinValue;

        public event Action<string> OnPayload;

        public GsiServer(string prefix = "http://127.0.0.1:3000/")
        {
            _prefix = prefix;
            _listener.Prefixes.Add(_prefix);
        }

        public void Start()
        {
            try
            {
                _listener.Start();
                _cts = new CancellationTokenSource();
                _loopTask = Task.Run(() => LoopAsync(_cts.Token));
                Logger.Info("GSI 服务已监听 " + _prefix);
            }
            catch (Exception ex)
            {
                Logger.Error("GSI 服务启动失败（端口可能被占用）", ex);
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex) { Logger.Warn("GetContext 失败: " + ex.Message); continue; }

                var task = HandleAsync(ctx);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                string json;
                using (var reader = new StreamReader(ctx.Request.InputStream))
                    json = await reader.ReadToEndAsync().ConfigureAwait(false);

                ctx.Response.StatusCode = 200;
                ctx.Response.Close();

                LastReceivedAt = DateTime.Now;
                Logger.Debug("GSI raw (" + (json == null ? 0 : json.Length) + "B): " + Truncate(json, 2000));

                var handler = OnPayload;
                if (handler != null) handler(json);
            }
            catch (Exception ex) { Logger.Warn("处理 GSI 请求失败: " + ex.Message); }
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return string.Empty;
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...<truncated>";
        }

        public void Dispose()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { _listener.Stop(); _listener.Close(); } catch { }
            try { if (_loopTask != null) _loopTask.Wait(500); } catch { }
        }
    }
}
