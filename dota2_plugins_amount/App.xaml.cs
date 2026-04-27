using System;
using System.Windows;
using System.Windows.Threading;
using Dota2NetWorth.Services;

namespace Dota2NetWorth
{
    /// <summary>
    /// App.xaml 的交互逻辑（命名空间已统一为 Dota2NetWorth）。
    /// 增加全局未捕获异常处理，避免 async void 异常导致进程静默退出。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Logger.Info("===== App Start =====");
            this.DispatcherUnhandledException += OnDispatcherUnhandled;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnTaskUnobserved;
        }

        private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Error("UI 线程未捕获异常", e.Exception);
            e.Handled = true;
        }
        private void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
            => Logger.Error("AppDomain 未捕获异常", e.ExceptionObject as Exception);
        private void OnTaskUnobserved(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            Logger.Error("Task 未观察异常", e.Exception);
            e.SetObserved();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Info("===== App Exit =====");
            base.OnExit(e);
        }
    }
}
