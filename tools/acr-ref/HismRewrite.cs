using System.Buffers.Binary;

namespace AcrRef;

/// Replaces the instances of a cooked HierarchicalInstancedStaticMeshComponent export (UE 5.6) without changing its size.
/// Layout of the part after the unversioned property block, as `acr-ref export-hex` showed it on Hafren's PCG forests:
///   [LODData count = 0][bSerializeAsCookedData][MeshPaintTextureCooked]      3 x int32
///   [bCooked = 1][bHasSkipSerializationPropertiesData = 1]                   2 x int32
///   PerInstanceSMData      bulk array: int32 elementSize = 128, int32 count N, N x FMatrix (16 doubles, row major,
///                          rotation*scale in rows 0-2, translation in row 3)
///   PerInstanceSMCustomData bulk array: int32 elementSize = 4, int32 count C, C floats
///   [bHasCookedData = 0]                                                      int32 (no cooked render data)
///   ClusterTree            bulk array: int32 elementSize = 64, int32 count M, M x FClusterNode
///     FClusterNode (64 bytes): +0 BoundMin float3, +12 FirstChild i32, +16 BoundMax float3, +28 LastChild i32, +32 FirstInstance i32,
///                   +36 LastInstance i32, +40 MinInstanceScale float3, +52 MaxInstanceScale float3 (instance ranges are contiguous)
/// The instance count N stays the same, so the property block (SortedInstances, InstanceReorderTable, ...) stays valid;
/// only the matrices, every node's bounds and scales, and the bounds copies in the property block change.
internal static class HismRewrite
{
    public sealed record Layout(int BulkAt, int Count, int MatricesAt, int ClusterAt, int Nodes)
    {
        public int Flags => BulkAt - 8;   // bCooked, bHasSkipSerializationPropertiesData
    }

    public sealed record Instance(double X, double Y, double Z, double Yaw, double Scale);

    static int I32(byte[] d, int p) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
    static double F64(byte[] d, int p) => BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(p));
    static float F32(byte[] d, int p) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p));
    static void Put(byte[] d, int p, double v) => BinaryPrimitives.WriteDoubleLittleEndian(d.AsSpan(p), v);
    static void Put(byte[] d, int p, float v) => BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(p), v);

    /// Finds the tail by its exact fit to the end of the export; null when the export is not in this layout
    /// (for example when it carries cooked render data).
    public static Layout? Parse(byte[] d)
    {
        for (int p = 8; p + 16 <= d.Length; p++)
        {
            if (I32(d, p) != 128 || I32(d, p - 4) != 1 || I32(d, p - 8) != 1) continue;
            int n = I32(d, p + 4);
            if (n < 1 || n > 5_000_000) continue;
            long cp = p + 8 + 128L * n;
            if (cp + 8 > d.Length || I32(d, (int)cp) != 4) continue;
            int c = I32(d, (int)cp + 4);
            if (c < 0) continue;
            long fp = cp + 8 + 4L * c;
            if (fp + 12 > d.Length || I32(d, (int)fp) != 0) continue;
            long tp = fp + 4;
            if (I32(d, (int)tp) != 64) continue;
            int m = I32(d, (int)tp + 4);
            if (m < 1 || tp + 8 + 64L * m != d.Length) continue;
            return new Layout(p, n, p + 8, (int)tp + 8, m);
        }
        return null;
    }

    /// Mesh bounds in instance-local space (unscaled), from the leaf nodes that hold a single instance: the old instance
    /// transform is known, and the node bounds are its transformed mesh bounds. Null when there is no such leaf.
    public static (double[] lo, double[] hi)? LocalBox(byte[] d, Layout L)
    {
        double[]? lo = null, hi = null;
        for (int k = 0; k < L.Nodes; k++)
        {
            int nb = L.ClusterAt + 64 * k;
            int first = I32(d, nb + 32), last = I32(d, nb + 36);
            if (I32(d, nb + 12) != -1 || first != last || first < 0 || first >= L.Count) continue;
            int mb = L.MatricesAt + 128 * first;
            double sx = Math.Sqrt(F64(d, mb) * F64(d, mb) + F64(d, mb + 8) * F64(d, mb + 8) + F64(d, mb + 16) * F64(d, mb + 16));
            if (sx < 1e-3) continue;
            double[] t = [F64(d, mb + 96), F64(d, mb + 104), F64(d, mb + 112)];
            double[] a = [F32(d, nb), F32(d, nb + 4), F32(d, nb + 8)], b = [F32(d, nb + 16), F32(d, nb + 20), F32(d, nb + 24)];
            // rotation about Z moves the plan extents, so keep a symmetric plan radius; z is unaffected up to the small tilt
            double rx = Math.Max(Math.Max(Math.Abs(a[0] - t[0]), Math.Abs(b[0] - t[0])), Math.Max(Math.Abs(a[1] - t[1]), Math.Abs(b[1] - t[1]))) / sx;
            double[] l = [-rx, -rx, (a[2] - t[2]) / sx], h = [rx, rx, (b[2] - t[2]) / sx];
            lo ??= l; hi ??= h;
            for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], l[i]); hi[i] = Math.Max(hi[i], h[i]); }
        }
        return lo is null ? null : (lo, hi!);
    }

    /// New export data (same length): `inst` must hold exactly L.Count instances, ordered so that every contiguous index
    /// range is spatially compact (the cluster tree's nodes keep their ranges). Instances with Scale 0 are dead and are
    /// ignored for bounds. `box` is the mesh's local bounds from LocalBox.
    public static byte[] Rewrite(byte[] old, Layout L, IReadOnlyList<Instance> inst, (double[] lo, double[] hi) box)
    {
        if (inst.Count != L.Count) throw new ArgumentException($"{inst.Count} instances for {L.Count} slots");
        var d = (byte[])old.Clone();

        // old root bounds (floats of node 0) and old component bounds, to find their copies in the property block
        double[] oldMin = [F32(old, L.ClusterAt), F32(old, L.ClusterAt + 4), F32(old, L.ClusterAt + 8)];
        double[] oldMax = [F32(old, L.ClusterAt + 16), F32(old, L.ClusterAt + 20), F32(old, L.ClusterAt + 24)];

        // matrices and per-instance bounds
        var imin = new double[L.Count][]; var imax = new double[L.Count][]; var scale = new double[L.Count];
        for (int i = 0; i < L.Count; i++)
        {
            var s = inst[i]; double c = Math.Cos(s.Yaw), sn = Math.Sin(s.Yaw), k = s.Scale;
            int mb = L.MatricesAt + 128 * i;
            double[] m = [c * k, sn * k, 0, 0, -sn * k, c * k, 0, 0, 0, 0, k, 0, s.X, s.Y, s.Z, 1];
            for (int j = 0; j < 16; j++) Put(d, mb + 8 * j, m[j]);
            scale[i] = k;
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (var (lx, ly) in new[] { (box.lo[0], box.lo[1]), (box.lo[0], box.hi[1]), (box.hi[0], box.lo[1]), (box.hi[0], box.hi[1]) })
            {
                double wx = s.X + k * (c * lx - sn * ly), wy = s.Y + k * (sn * lx + c * ly);
                x0 = Math.Min(x0, wx); x1 = Math.Max(x1, wx); y0 = Math.Min(y0, wy); y1 = Math.Max(y1, wy);
            }
            imin[i] = [x0, y0, s.Z + k * box.lo[2]]; imax[i] = [x1, y1, s.Z + k * box.hi[2]];
        }

        // cluster nodes: bounds and scale range over their instance range
        double[] rootMin = [double.MaxValue, double.MaxValue, double.MaxValue], rootMax = [double.MinValue, double.MinValue, double.MinValue];
        for (int n = 0; n < L.Nodes; n++)
        {
            int nb = L.ClusterAt + 64 * n;
            int first = I32(d, nb + 32), last = I32(d, nb + 36);
            if (first < 0 || last < first || last >= L.Count) continue;
            double[] mn = [double.MaxValue, double.MaxValue, double.MaxValue], mx = [double.MinValue, double.MinValue, double.MinValue];
            double smin = double.MaxValue, smax = double.MinValue;
            for (int i = first; i <= last; i++)
            {
                if (scale[i] <= 0) continue;
                for (int a = 0; a < 3; a++) { mn[a] = Math.Min(mn[a], imin[i][a]); mx[a] = Math.Max(mx[a], imax[i][a]); }
                smin = Math.Min(smin, scale[i]); smax = Math.Max(smax, scale[i]);
            }
            if (smin > smax)   // only dead instances: a small box at the first one, scale 1
            {
                double[] p = [inst[first].X, inst[first].Y, inst[first].Z];
                for (int a = 0; a < 3; a++) { mn[a] = p[a] - 1; mx[a] = p[a] + 1; }
                smin = smax = 1;
            }
            for (int a = 0; a < 3; a++) { Put(d, nb + 4 * a, (float)mn[a]); Put(d, nb + 16 + 4 * a, (float)mx[a]); }
            for (int a = 0; a < 3; a++) { Put(d, nb + 40 + 4 * a, (float)smin); Put(d, nb + 52 + 4 * a, (float)smax); }
            if (n == 0) { rootMin = mn; rootMax = mx; }
        }

        // bounds copies in the property block: FBox (6 doubles + valid byte) equal to the old root bounds, and
        // FBoxSphereBounds (origin, extent, radius) of the old box
        double[] oc = [(oldMin[0] + oldMax[0]) / 2, (oldMin[1] + oldMax[1]) / 2, (oldMin[2] + oldMax[2]) / 2];
        double[] oe = [(oldMax[0] - oldMin[0]) / 2, (oldMax[1] - oldMin[1]) / 2, (oldMax[2] - oldMin[2]) / 2];
        double[] nc = [(rootMin[0] + rootMax[0]) / 2, (rootMin[1] + rootMax[1]) / 2, (rootMin[2] + rootMax[2]) / 2];
        double[] ne = [(rootMax[0] - rootMin[0]) / 2, (rootMax[1] - rootMin[1]) / 2, (rootMax[2] - rootMin[2]) / 2];
        double radius = Math.Sqrt(ne[0] * ne[0] + ne[1] * ne[1] + ne[2] * ne[2]);
        bool Near(int p, double[] v, int n, double tol) { for (int i = 0; i < n; i++) if (Math.Abs(F64(old, p + 8 * i) - v[i]) > tol) return false; return true; }
        for (int p = 0; p + 56 <= L.Flags; p++)
        {
            if (p + 49 <= L.Flags && old[p + 48] == 1 && Near(p, [oldMin[0], oldMin[1], oldMin[2], oldMax[0], oldMax[1], oldMax[2]], 6, 2.0))
            {
                for (int a = 0; a < 3; a++) { Put(d, p + 8 * a, rootMin[a]); Put(d, p + 24 + 8 * a, rootMax[a]); }
                p += 48;
            }
            else if (Near(p, [oc[0], oc[1], oc[2], oe[0], oe[1], oe[2]], 6, 2.0) && Math.Abs(F64(old, p + 48) - Math.Sqrt(oe[0] * oe[0] + oe[1] * oe[1] + oe[2] * oe[2])) < 2.0)
            {
                for (int a = 0; a < 3; a++) { Put(d, p + 8 * a, nc[a]); Put(d, p + 24 + 8 * a, ne[a]); }
                Put(d, p + 48, radius);
                p += 55;
            }
        }
        return d;
    }

    /// Morton (Z-order) code of a plan position inside the box, 16 bits per axis.
    public static uint Morton(double x, double y, double x0, double y0, double x1, double y1)
    {
        static uint Spread(uint v) { v &= 0xFFFF; v = (v | (v << 8)) & 0x00FF00FF; v = (v | (v << 4)) & 0x0F0F0F0F; v = (v | (v << 2)) & 0x33333333; v = (v | (v << 1)) & 0x55555555; return v; }
        uint qx = (uint)Math.Clamp((x - x0) / Math.Max(1, x1 - x0) * 65535, 0, 65535), qy = (uint)Math.Clamp((y - y0) / Math.Max(1, y1 - y0) * 65535, 0, 65535);
        return Spread(qx) | (Spread(qy) << 1);
    }
}
