using System.Collections.Generic;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 静的な点群に対する nearest-neighbor 検索用の均一空間グリッド索引。
/// ドナー頂点群（数千〜2万点）を想定。
/// </summary>
internal sealed class SpatialGridIndex
{
    private readonly Vector3[] _points;
    private readonly Vector3 _origin;
    private readonly float _cellSize;
    private readonly float _invCellSize;
    private readonly Dictionary<long, List<int>> _cells;

    // セル座標を long にパックする際のビット幅（各軸 21bit / 符号ビット 1）
    private const int Bits = 21;
    private const int Bias = 1 << (Bits - 1); // 1<<20 で負の座標をオフセット

    public SpatialGridIndex(Vector3[] points)
    {
        _points = points;

        // 空の点群は非エラーとして扱う（FindNearest は -1 を返す）
        if (points.Length == 0)
        {
            _origin = Vector3.zero;
            _cellSize = 1f;
            _invCellSize = 1f;
            _cells = new Dictionary<long, List<int>>();
            return;
        }

        // 1. AABB
        Vector3 min = points[0], max = points[0];
        for (int i = 1; i < points.Length; i++)
        {
            var p = points[i];
            if (p.x < min.x) min.x = p.x; else if (p.x > max.x) max.x = p.x;
            if (p.y < min.y) min.y = p.y; else if (p.y > max.y) max.y = p.y;
            if (p.z < min.z) min.z = p.z; else if (p.z > max.z) max.z = p.z;
        }
        _origin = min;

        // 2. cellSize: AABB 対角 / cbrt(N)
        float diag = (max - min).magnitude;
        if (diag < 1e-6f) diag = 1e-6f;
        float n = Mathf.Pow(Mathf.Max(points.Length, 1), 1f / 3f);
        _cellSize = Mathf.Max(diag / Mathf.Max(n, 1f), 1e-5f);
        _invCellSize = 1f / _cellSize;

        // 3. ビン詰め
        _cells = new Dictionary<long, List<int>>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            long key = CellKey(points[i]);
            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<int>(4);
                _cells[key] = list;
            }
            list.Add(i);
        }
    }

    /// <summary>
    /// クエリ点 q に最も近い登録点の index を返す。点群が空の場合 -1。
    /// </summary>
    public int FindNearest(Vector3 q)
    {
        if (_points.Length == 0) return -1;

        int qx = (int)Mathf.Floor((q.x - _origin.x) * _invCellSize);
        int qy = (int)Mathf.Floor((q.y - _origin.y) * _invCellSize);
        int qz = (int)Mathf.Floor((q.z - _origin.z) * _invCellSize);

        int bestIdx = -1;
        float bestSq = float.MaxValue;

        const int MaxRadius = 1024; // セーフティ
        for (int r = 0; r <= MaxRadius; r++)
        {
            ScanShell(qx, qy, qz, r, q, ref bestIdx, ref bestSq);

            if (bestIdx >= 0)
            {
                // 現在の半径 r より外側のシェルにある点は、距離が少なくとも r*cellSize 以上ある。
                // best を超える可能性が無くなったら打ち切る。
                float bestDist = Mathf.Sqrt(bestSq);
                if (r * _cellSize >= bestDist) return bestIdx;
            }
        }
        return bestIdx;
    }

    private void ScanShell(int cx, int cy, int cz, int r, Vector3 q, ref int bestIdx, ref float bestSq)
    {
        if (r == 0)
        {
            ScanCell(cx, cy, cz, q, ref bestIdx, ref bestSq);
            return;
        }
        // シェル: max(|dx|,|dy|,|dz|) == r となる立方体の表面のみ
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        for (int dz = -r; dz <= r; dz++)
        {
            int ax = dx < 0 ? -dx : dx;
            int ay = dy < 0 ? -dy : dy;
            int az = dz < 0 ? -dz : dz;
            int m = ax > ay ? ax : ay;
            if (az > m) m = az;
            if (m != r) continue; // 内側は前のシェルで走査済み
            ScanCell(cx + dx, cy + dy, cz + dz, q, ref bestIdx, ref bestSq);
        }
    }

    private void ScanCell(int cx, int cy, int cz, Vector3 q, ref int bestIdx, ref float bestSq)
    {
        if (!_cells.TryGetValue(PackKey(cx, cy, cz), out var list)) return;
        for (int i = 0; i < list.Count; i++)
        {
            int idx = list[i];
            var p = _points[idx];
            float ex = p.x - q.x, ey = p.y - q.y, ez = p.z - q.z;
            float sq = ex * ex + ey * ey + ez * ez;
            if (sq < bestSq) { bestSq = sq; bestIdx = idx; }
        }
    }

    private long CellKey(Vector3 p)
    {
        int cx = (int)Mathf.Floor((p.x - _origin.x) * _invCellSize);
        int cy = (int)Mathf.Floor((p.y - _origin.y) * _invCellSize);
        int cz = (int)Mathf.Floor((p.z - _origin.z) * _invCellSize);
        return PackKey(cx, cy, cz);
    }

    private static long PackKey(int cx, int cy, int cz)
    {
        // [-Bias, Bias) を超える cx はキー衝突を起こすのでクランプする。
        // 退化メッシュ（cellSize 極小）+ クエリ点が AABB 外側で発生し得るため。
        // 遠方点を端セルに集約しても、最近傍候補ではないので正しさは保たれる。
        return ClampAndBias(cx)
             | (ClampAndBias(cy) << Bits)
             | (ClampAndBias(cz) << (Bits * 2));
    }

    private static long ClampAndBias(int c)
    {
        const int Max = (1 << (Bits - 1)) - 1; // 2^20 - 1
        const int Min = -(1 << (Bits - 1));    // -2^20
        if (c > Max) c = Max;
        else if (c < Min) c = Min;
        return (long)(c + Bias); // [0, 2^21)
    }
}
