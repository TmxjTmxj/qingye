using System;
using System.Windows.Threading;
namespace Qingye.UI {
    public partial class MainWindow {
        DispatcherOperation pendingFooter;
        void QueueFooterStatus()
        {
            if (closing || pendingFooter != null) return;
            // Merge caret/selection notifications until queued input has been handled.
            pendingFooter = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                pendingFooter = null;
                if (!closing && Current != null) footer.Update(Current, active, openQueueNotice);
            }));
        }
        void CancelFooterStatus()
        {
            if (pendingFooter == null) return;
            if (pendingFooter.Status == DispatcherOperationStatus.Pending) pendingFooter.Abort();
            pendingFooter = null;
        }
    }
}
