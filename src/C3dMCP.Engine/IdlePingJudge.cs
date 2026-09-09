using System;

namespace C3dMCP.Engine
{
    /// <summary>What the host measured for one sentinel probe.</summary>
    public struct IdleProbe
    {
        public DateTime AtUtc;        // when the sentinel was sent
        public double ElapsedMs;      // send → CommandEnded
        public bool TimedOut;         // no CommandEnded inside the probe budget (main thread busy)
        public long LogSeq;           // run log seq at send time
        public long CommandCount;     // non-sentinel commands started so far in this run
        public long DbChangeCount;    // ObjectAppended/Modified/Erased seen so far in this run
    }

    public enum IdleVerdict { Busy, MainThreadBusy, Idle, Complete }

    /// <summary>Decides when a draining run may be inferred complete. A single fast sentinel only
    /// proves the input queue was empty at that instant, so completion needs
    /// <see cref="RequiredStreak"/> consecutive idle probes spanning at least <see cref="MinSpan"/>
    /// during which the activity counters did not move. Any movement resets the streak. Pure logic.</summary>
    public sealed class IdlePingJudge
    {
        public IdlePingJudge(int requiredStreak = 3, TimeSpan? minSpan = null, double idleThresholdMs = 300)
        {
            RequiredStreak = Math.Max(1, requiredStreak);
            MinSpan = minSpan ?? TimeSpan.FromSeconds(10);
            IdleThresholdMs = idleThresholdMs;
        }

        public int RequiredStreak { get; }
        public TimeSpan MinSpan { get; }
        public double IdleThresholdMs { get; }

        public int Streak { get; private set; }
        public DateTime? StreakStartedUtc { get; private set; }
        public int Probes { get; private set; }
        public double LastMs { get; private set; }
        public bool IsComplete { get; private set; }

        private bool _haveLast;
        private IdleProbe _last;

        public IdleVerdict Observe(IdleProbe p)
        {
            Probes++;
            LastMs = p.ElapsedMs;
            if (IsComplete) return IdleVerdict.Complete;

            if (p.TimedOut) { Reset(); return IdleVerdict.MainThreadBusy; }
            bool idle = p.ElapsedMs < IdleThresholdMs;
            if (!idle) { Reset(); RememberLast(p); return IdleVerdict.Busy; }

            bool moved = _haveLast && (p.LogSeq != _last.LogSeq || p.CommandCount != _last.CommandCount || p.DbChangeCount != _last.DbChangeCount);
            if (moved || Streak == 0)
            {
                Streak = 1;
                StreakStartedUtc = p.AtUtc;
            }
            else
            {
                Streak++;
            }
            RememberLast(p);

            if (Streak >= RequiredStreak && StreakStartedUtc.HasValue && p.AtUtc - StreakStartedUtc.Value >= MinSpan)
            {
                IsComplete = true;
                return IdleVerdict.Complete;
            }
            return IdleVerdict.Idle;
        }

        /// <summary>Activity observed between probes (a log line, a command, a db change) — the host
        /// may call this eagerly so the palette shows the streak reset at once.</summary>
        public void Reset()
        {
            Streak = 0;
            StreakStartedUtc = null;
        }

        private void RememberLast(IdleProbe p) { _last = p; _haveLast = true; }
    }
}
