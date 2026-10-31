using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed partial class AgentPanel
    {
        string transientRuntimeHome;
        Task sessionMaintenance = Task.FromResult(0);
        internal Task SessionMaintenance { get { return sessionMaintenance; } }
        internal string RuntimeDataDirectory
        {
            get
            {
                if (!String.IsNullOrEmpty(AppDataDirectory)) return AppDataDirectory;
                if (transientRuntimeHome == null)
                    transientRuntimeHome = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "harness-data", Guid.NewGuid().ToString("N"));
                HarnessBrowserCleanup.ValidateRunDirectory(Path.GetDirectoryName(transientRuntimeHome), transientRuntimeHome);
                return transientRuntimeHome;
            }
        }

        void RetireSdkController()
        {
            if (sdk != null)
            {
                sdk.RevokeFileAccess();
                sdk.Activity -= OnActivity;
                sdk.Dispose();
                sdk = null;
            }
            controllerScope = controllerConfig = "";
            resumedHarnessId = null;
        }

        void QueueSessionMaintenance(Func<Task> action)
        {
            sessionMaintenance = sessionMaintenance.ContinueWith(_ => RunSessionMaintenanceAsync(action),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }

        async Task RunSessionMaintenanceAsync(Func<Task> action)
        {
            Exception failure = null;
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            if (failure == null) return;
            // Failure to clear memory must not leave an old context available.
            await SdkAgentController.StopSharedAsync().ConfigureAwait(false);
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(new Action(delegate {
                runtimeReady = false;
                conversation.AddNotice("旧上下文清理失败，已释放运行时；下次发送将重新连接。");
            }));
        }

        internal void UseMemoryTestEnvironment(string historyDirectory, string runtimeHome, Func<ModelConfig> config)
        {
            UseTestEnvironment(historyDirectory, runtimeHome, config);
            store = new AgentSessionStore(historyDirectory, true);
            history.SetTemporary(true);
        }

        async Task ReleaseRuntimeDirectoryAsync()
        {
            string directory = AppDataDirectory ?? transientRuntimeHome;
            if (directory != null)
                await HarnessBrowserCleanup.CloseAsync(null, null, Path.GetDirectoryName(directory), directory).ConfigureAwait(true);
        }
    }
}
