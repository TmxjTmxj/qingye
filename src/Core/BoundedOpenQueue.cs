using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Qingye.Core
{
    // Owned by the window dispatcher; four waiting requests, plus one active request.
    internal sealed class BoundedOpenQueue
    {
        sealed class Request
        {
            public Func<Task> Work;
            public readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>();
        }
        readonly Queue<Request> waiting = new Queue<Request>();
        readonly Func<bool> canRun;
        readonly Action overflow;
        readonly Action<Exception> failed;
        public bool IsRunning { get; private set; }
        public int Count { get { return waiting.Count; } }

        public BoundedOpenQueue(Func<bool> ready, Action dropped, Action<Exception> onError)
        {
            canRun = ready;
            overflow = dropped;
            failed = onError;
        }
        public Task Enqueue(Func<Task> work)
        {
            if (waiting.Count == 4)
            {
                waiting.Dequeue().Completion.TrySetResult(false);
                overflow();
            }
            var request = new Request { Work = work };
            waiting.Enqueue(request);
            Resume();
            return request.Completion.Task;
        }
        public async void Resume()
        {
            if (IsRunning || !canRun()) return;
            IsRunning = true;
            try
            {
                while (waiting.Count > 0 && canRun())
                {
                    var request = waiting.Dequeue();
                    try
                    {
                        await request.Work();
                        request.Completion.TrySetResult(true);
                    }
                    catch (Exception error)
                    {
                        request.Completion.TrySetResult(false);
                        failed(error);
                    }
                }
            }
            finally { IsRunning = false; }
        }
        public void CancelWaiting()
        {
            while (waiting.Count > 0) waiting.Dequeue().Completion.TrySetResult(false);
        }
    }
}
