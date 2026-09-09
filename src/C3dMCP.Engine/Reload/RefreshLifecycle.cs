using C3dMCP.Sdk;
using System;

namespace C3dMCP.Engine.Reload
{
    public sealed class RefreshLifecycle
    {
        private readonly object _sync = new object();
        private ExecutorGeneration _runningGeneration;
        private PendingRefresh _pending;
        private bool _isShuttingDown;

        public RefreshLifecycle(ExecutorGeneration runningGeneration)
        {
            _runningGeneration = runningGeneration ?? throw new ArgumentNullException(nameof(runningGeneration));
        }

        public bool HasPending
        {
            get
            {
                lock (_sync)
                    return _pending != null;
            }
        }

        public PendingRefresh Pending
        {
            get
            {
                lock (_sync)
                    return _pending;
            }
        }

        public ExecutorGeneration RequireRunningGeneration()
        {
            lock (_sync)
            {
                ThrowIfShuttingDown();
                if (_runningGeneration == null)
                    throw new InvalidOperationException("No add-in generation is currently running.");

                return _runningGeneration;
            }
        }

        public void BeginRefresh(ExecutorGeneration candidate)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));

            lock (_sync)
            {
                ThrowIfShuttingDown();
                if (_pending != null)
                    throw new InvalidOperationException("A refresh request is already pending.");
                if (_runningGeneration == null)
                    throw new InvalidOperationException("A refresh requires a running add-in generation.");

                _pending = new PendingRefresh(_runningGeneration, candidate);
            }
        }

        public void ExecutePending(object appContext)
        {
            if (appContext == null)
                throw new ArgumentNullException(nameof(appContext));

            var pending = GetPendingForExecution();

            if (pending.Stage == RefreshStage.AwaitingRefresh)
            {
                var refreshData = pending.Previous.PatchService.Capture(
                    pending.Previous.Application);
                ValidateRefreshData(refreshData);

                pending = pending.Captured(refreshData);
                SetCaptured(pending);
            }

            if (pending.Stage == RefreshStage.Captured)
            {
                pending.Previous.PatchService.SafeEnd(
                    pending.Previous.Application,
                    appContext,
                    pending.RefreshData);

                pending = pending.PreviousStopped(pending.RefreshData);
                SetStopped(pending);
            }

            if (pending.Stage == RefreshStage.PreviousStopped)
            {
                pending.Candidate.PatchService.Refresh(
                    pending.Candidate.Application,
                    appContext,
                    pending.RefreshData);

                pending = pending.CandidateActivated();
                SetActivated(pending);
            }

            Complete(pending);
        }

        public ExecutorGeneration BeginShutdown()
        {
            lock (_sync)
            {
                _isShuttingDown = true;
                return _runningGeneration;
            }
        }

        public bool IsShuttingDown
        {
            get
            {
                lock (_sync)
                    return _isShuttingDown;
            }
        }

        private PendingRefresh GetPendingForExecution()
        {
            lock (_sync)
            {
                ThrowIfShuttingDown();
                return _pending ?? throw new InvalidOperationException("No refresh request is pending.");
            }
        }

        private void SetStopped(PendingRefresh pending)
        {
            lock (_sync)
            {
                EnsureCurrentRequest(pending.Previous, pending.Candidate);
                _runningGeneration = null;
                _pending = pending;
            }
        }

        private void SetCaptured(PendingRefresh pending)
        {
            lock (_sync)
            {
                EnsureCurrentRequest(pending.Previous, pending.Candidate);
                _runningGeneration = null;
                _pending = pending;
            }
        }

        private void SetActivated(PendingRefresh pending)
        {
            lock (_sync)
            {
                EnsureCurrentRequest(pending.Previous, pending.Candidate);
                _runningGeneration = pending.Candidate;
                _pending = pending;
            }
        }

        private void Complete(PendingRefresh pending)
        {
            lock (_sync)
            {
                EnsureCurrentRequest(pending.Previous, pending.Candidate);
                if (pending.Stage != RefreshStage.CandidateActivated)
                    throw new InvalidOperationException("Only an activated refresh can be completed.");

                _pending = null;
            }
        }

        private void EnsureCurrentRequest(ExecutorGeneration previous, ExecutorGeneration candidate)
        {
            if (_pending == null
                || !ReferenceEquals(_pending.Previous, previous)
                || !ReferenceEquals(_pending.Candidate, candidate))
            {
                throw new InvalidOperationException("The pending refresh request changed during execution.");
            }
        }

        private void ThrowIfShuttingDown()
        {
            if (_isShuttingDown)
                throw new InvalidOperationException("The patcher is shutting down.");
        }

        private static void ValidateRefreshData(ReloadState refreshData)
        {
            if (refreshData == null)
                throw new InvalidOperationException("The previous patch service returned no refresh state.");
            if (refreshData.CapturedAt == default(DateTime))
                throw new InvalidOperationException("The captured refresh state must have a valid capture timestamp.");
        }
    }
}
