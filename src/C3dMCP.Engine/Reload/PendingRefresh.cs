using C3dMCP.Sdk;
using System;

namespace C3dMCP.Engine.Reload
{
    public sealed class PendingRefresh
    {
        public PendingRefresh(
            ExecutorGeneration previous,
            ExecutorGeneration candidate,
            RefreshStage stage = RefreshStage.AwaitingRefresh,
            ReloadState refreshData = null)
        {
            Previous = previous ?? throw new ArgumentNullException(nameof(previous));
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
            Stage = stage;
            RefreshData = refreshData;
        }

        public ExecutorGeneration Previous { get; }

        public ExecutorGeneration Candidate { get; }

        public RefreshStage Stage { get; }

        public ReloadState RefreshData { get; }

        public PendingRefresh PreviousStopped(ReloadState refreshData)
        {
            return new PendingRefresh(
                Previous,
                Candidate,
                RefreshStage.PreviousStopped,
                refreshData ?? throw new ArgumentNullException(nameof(refreshData)));
        }

        public PendingRefresh Captured(ReloadState refreshData)
        {
            return new PendingRefresh(
                Previous,
                Candidate,
                RefreshStage.Captured,
                refreshData ?? throw new ArgumentNullException(nameof(refreshData)));
        }

        public PendingRefresh CandidateActivated()
        {
            if (RefreshData == null)
                throw new InvalidOperationException("A candidate cannot be activated without captured refresh data.");

            return new PendingRefresh(
                Previous,
                Candidate,
                RefreshStage.CandidateActivated,
                RefreshData);
        }
    }
}
