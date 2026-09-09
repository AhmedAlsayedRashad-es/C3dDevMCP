using System.Collections.Generic;

namespace C3dMCP.Engine
{
    /// <summary>The order in which an instance tries ports: a fixed range, starting at an offset
    /// derived from the process id so several instances spread out, wrapping around. Pure logic.</summary>
    public static class PortPlan
    {
        public const int RangeStart = 48200;
        public const int RangeSize = 100;

        public static IEnumerable<int> Candidates(int pid, int rangeStart = RangeStart, int rangeSize = RangeSize)
        {
            if (rangeSize <= 0) yield break;
            int offset = ((pid % rangeSize) + rangeSize) % rangeSize;
            for (int i = 0; i < rangeSize; i++)
                yield return rangeStart + (offset + i) % rangeSize;
        }
    }
}
