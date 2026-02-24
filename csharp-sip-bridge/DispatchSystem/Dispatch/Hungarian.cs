namespace DispatchSystem.Dispatch;

/// <summary>
/// Hungarian (Kuhn-Munkres) algorithm for optimal assignment.
/// Solves a square cost matrix for minimum cost 1:1 assignment.
/// Convert utility to cost: cost = 1 - utility.
/// Returns assignment[row] = col, or -1 if unassigned.
/// </summary>
public static class Hungarian
{
    public static int[] Solve(double[,] cost)
    {
        int n = cost.GetLength(0);
        int m = cost.GetLength(1);
        int N = Math.Max(n, m);

        // Pad to square if needed
        var c = new double[N, N];
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
                c[i, j] = (i < n && j < m) ? cost[i, j] : 1.0;

        // 1-indexed arrays for standard Hungarian
        var u = new double[N + 1];
        var v = new double[N + 1];
        var p = new int[N + 1];
        var way = new int[N + 1];

        for (int i = 1; i <= N; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = new double[N + 1];
            var used = new bool[N + 1];
            for (int j = 1; j <= N; j++) { minv[j] = double.PositiveInfinity; used[j] = false; }

            do
            {
                used[j0] = true;
                int i0 = p[j0];
                double delta = double.PositiveInfinity;
                int j1 = 0;

                for (int j = 1; j <= N; j++)
                {
                    if (used[j]) continue;
                    double cur = c[i0 - 1, j - 1] - u[i0] - v[j];
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }

                for (int j = 0; j <= N; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else { minv[j] -= delta; }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        var assignment = Enumerable.Repeat(-1, n).ToArray();
        for (int j = 1; j <= N; j++)
        {
            if (p[j] != 0 && p[j] - 1 < n && j - 1 < m)
                assignment[p[j] - 1] = j - 1;
        }
        return assignment;
    }
}
