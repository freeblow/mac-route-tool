namespace SRM.Core;

public static class NicPriority
{
    public const int MinMetric = 1;
    public const int MaxMetric = 9999;

    public static List<(int Index, int Metric)> PlanMove(IReadOnlyList<NicInfo> ordered, int index, int step)
    {
        var changes = new List<(int Index, int Metric)>();
        var position = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Index == index) { position = i; break; }
        }
        if (position < 0) return changes;
        if (ordered[position].Metric is null)
        {
            throw new InvalidOperationException(Localization.Loc.T("Core.Nic.PriorityUnavailable"));
        }
        var peerPosition = position + step;
        if (peerPosition < 0 || peerPosition >= ordered.Count) return changes;
        if (ordered[peerPosition].Metric is not int peerMetric) return changes;
        if (step < 0)
        {
            if (peerMetric <= MinMetric)
            {
                changes.Add((ordered[peerPosition].Index, MinMetric + 1));
                changes.Add((index, MinMetric));
            }
            else
            {
                changes.Add((index, peerMetric - 1));
            }
        }
        else
        {
            if (peerMetric >= MaxMetric)
            {
                changes.Add((ordered[peerPosition].Index, MaxMetric - 1));
                changes.Add((index, MaxMetric));
            }
            else
            {
                changes.Add((index, peerMetric + 1));
            }
        }
        return changes;
    }
}
