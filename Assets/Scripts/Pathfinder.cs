using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 도로 그래프 위 A* 경로 탐색. 휴리스틱 = 직선거리(미터), 비용 = 도로 길이.
/// </summary>
public static class Pathfinder
{
    public static List<Vector3> FindPath(RoadNetwork net, long startId, long goalId)
    {
        if (startId < 0 || goalId < 0) return null;
        Vector3 goalPos = net.NodePos[goalId];

        var open = new MinHeap();
        var gScore = new Dictionary<long, float>();
        var cameFrom = new Dictionary<long, long>();
        var closed = new HashSet<long>();

        gScore[startId] = 0f;
        open.Push(Vector3.Distance(net.NodePos[startId], goalPos), startId);

        while (open.Count > 0)
        {
            long cur = open.Pop();
            if (cur == goalId) return Reconstruct(net, cameFrom, cur);
            if (!closed.Add(cur)) continue;

            if (!net.Adj.TryGetValue(cur, out var neighbors)) continue;
            float g = gScore[cur];

            foreach (var (to, cost) in neighbors)
            {
                if (closed.Contains(to)) continue;
                float ng = g + cost;
                if (gScore.TryGetValue(to, out float old) && old <= ng) continue;

                gScore[to] = ng;
                cameFrom[to] = cur;
                open.Push(ng + Vector3.Distance(net.NodePos[to], goalPos), to);
            }
        }
        return null; // 연결 안 됨 (섬, 데이터 누락 등)
    }

    static List<Vector3> Reconstruct(RoadNetwork net, Dictionary<long, long> cameFrom, long cur)
    {
        var path = new List<Vector3> { net.NodePos[cur] };
        while (cameFrom.TryGetValue(cur, out long prev))
        {
            cur = prev;
            path.Add(net.NodePos[cur]);
        }
        path.Reverse();
        return path;
    }

    /// <summary>단순 이진 최소힙 (f값 기준)</summary>
    class MinHeap
    {
        readonly List<(float f, long id)> heap = new(1024);
        public int Count => heap.Count;

        public void Push(float f, long id)
        {
            heap.Add((f, id));
            int i = heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (heap[p].f <= heap[i].f) break;
                (heap[p], heap[i]) = (heap[i], heap[p]);
                i = p;
            }
        }

        public long Pop()
        {
            long top = heap[0].id;
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, s = i;
                if (l < heap.Count && heap[l].f < heap[s].f) s = l;
                if (r < heap.Count && heap[r].f < heap[s].f) s = r;
                if (s == i) break;
                (heap[s], heap[i]) = (heap[i], heap[s]);
                i = s;
            }
            return top;
        }
    }
}
