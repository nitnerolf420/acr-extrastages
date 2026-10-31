using System.Text.Json.Nodes;

namespace AcrRef;

/// Real trees from ACR's own meshes (config/scenery.toml [trees]): the persistent level of the host stage (Hafren South)
/// holds big PCG forest components (HierarchicalInstancedStaticMeshComponent, e.g. HISM_SM_SpruceWales01_0 of a PCGStamp
/// actor). Their instance data is replaced by the DR2 trees of the stage (HismRewrite keeps the export's size), and they are
/// left visible when the rest of the Hafren scenery is hidden. VisualObjects records the DR2 trees (and skips their
/// prism / crown proxies) while Enabled; Scenery.Build applies the result to the persistent package.
internal static class TreeInstancer
{
    public sealed record Tree(double X, double Y, double Z, double Yaw, double Height);
    public sealed record Target(int Export, string Key, HismRewrite.Layout Layout);

    public static readonly List<Tree> Trees = new();
    public static bool Enabled;
    public static int Budget;

    static double Num(Toml cfg, string key, double fallback) => cfg["trees"].TryGetValue(key, out var v) && v is double d ? d : fallback;
    static string[] Reuse(Toml cfg) => cfg["trees"].TryGetValue("reuse", out var v) && v is object[] a ? a.OfType<string>().ToArray() : [];

    public static void Reset() { Trees.Clear(); Enabled = false; Budget = 0; }

    /// "actor|component" export names: the key used in [trees] reuse.
    public static string KeyOf(LegacyPackage pkg, int comp)
    {
        int o = pkg.ExportOuter(comp);
        return $"{(o > 0 ? pkg.ExportName(o - 1) : "")}|{pkg.ExportName(comp)}";
    }

    public static List<Target> Find(LegacyPackage pkg, Toml cfg)
    {
        var want = Reuse(cfg).ToHashSet();
        var list = new List<Target>();
        for (int e = 0; e < pkg.ExportCount; e++)
        {
            if (pkg.ExportClass(e) != "HierarchicalInstancedStaticMeshComponent") continue;
            var key = KeyOf(pkg, e);
            if (!want.Contains(key)) continue;
            var layout = HismRewrite.Parse(pkg.GetExportData(e));
            if (layout is null) { Console.WriteLine($"trees: {key}: export layout not recognised (cooked render data?), not reused"); continue; }
            list.Add(new Target(e, key, layout));
        }
        return list;
    }

    /// Before the visuals are built: how many trees fit into the configured components.
    public static void Prepare(string legacyRoot, Toml cfg)
    {
        Reset();
        if (Num(cfg, "enabled", 0) == 0 || Reuse(cfg).Length == 0) return;
        var pkg = new LegacyPackage(HostPaths.Resolve(legacyRoot, Scenery.Persistent));
        var targets = Find(pkg, cfg);
        Budget = (int)Math.Min(targets.Sum(t => (long)t.Layout.Count), (long)Num(cfg, "max_trees", 30000));
        Enabled = Budget > 0;
        Console.WriteLine($"trees: {targets.Count} persistent forest components can be reused, room for {Budget} trees");
    }

    /// Rewrites the targets' export data in `pkg`: the recorded trees are spread over the components (random, weighted by
    /// room), ordered along a Z-order curve inside each, the remaining slots become dead instances far below the stage.
    public static JsonObject Apply(LegacyPackage pkg, Toml cfg, List<Target> targets)
    {
        var report = new JsonObject();
        var trees = Trees.Take(Math.Max(0, Budget)).ToList();
        var rnd = new Random((int)Num(cfg, "seed", 1));
        double smin = Num(cfg, "scale_min", 0.7), smax = Num(cfg, "scale_max", 1.4);
        double x0 = trees.Count == 0 ? 0 : trees.Min(t => t.X), x1 = trees.Count == 0 ? 1 : trees.Max(t => t.X);
        double y0 = trees.Count == 0 ? 0 : trees.Min(t => t.Y), y1 = trees.Count == 0 ? 1 : trees.Max(t => t.Y);
        double deadZ = (trees.Count == 0 ? 0 : trees.Min(t => t.Z)) - 20000;

        var room = targets.Select(t => t.Layout.Count).ToArray();
        var buckets = targets.Select(_ => new List<Tree>()).ToList();
        foreach (var tree in trees.OrderBy(_ => rnd.Next()))
        {
            long left = room.Sum(x => (long)x);
            if (left <= 0) break;
            double r = rnd.NextDouble() * left;
            int k = 0;
            for (; k < room.Length - 1; k++) { if (r < room[k]) break; r -= room[k]; }
            if (room[k] <= 0) k = Array.FindIndex(room, x => x > 0);
            buckets[k].Add(tree); room[k]--;
        }

        var reused = new JsonArray();
        int placed = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var data = pkg.GetExportData(t.Export);
            var box = HismRewrite.LocalBox(data, t.Layout) ?? (new double[] { -250, -250, 0 }, new double[] { 250, 250, 2000 });
            double refHeight = Math.Max(100, box.hi[2] - box.lo[2]);
            var ordered = buckets[i].OrderBy(b => HismRewrite.Morton(b.X, b.Y, x0, y0, x1, y1)).ToList();
            var inst = new List<HismRewrite.Instance>(t.Layout.Count);
            foreach (var b in ordered)
            {
                double s = b.Height > 0 ? Math.Clamp(b.Height / refHeight, smin, smax) : 1.0;
                inst.Add(new HismRewrite.Instance(b.X, b.Y, b.Z, rnd.NextDouble() * 2 * Math.PI, s * (0.92 + 0.16 * rnd.NextDouble())));
            }
            double lastX = ordered.Count > 0 ? ordered[^1].X : 0, lastY = ordered.Count > 0 ? ordered[^1].Y : 0;
            while (inst.Count < t.Layout.Count) inst.Add(new HismRewrite.Instance(lastX, lastY, deadZ, 0, 0));
            pkg.SetExportData(t.Export, HismRewrite.Rewrite(data, t.Layout, inst, box));
            placed += ordered.Count;
            reused.Add(t.Key);
        }
        report["reused"] = reused;
        report["trees_placed"] = placed;
        report["trees_recorded"] = Trees.Count;
        report["capacity"] = targets.Sum(t => (long)t.Layout.Count);
        Console.WriteLine($"trees: {placed} DR2 trees placed into {targets.Count} persistent forest component(s) ({Trees.Count} recorded)");
        return report;
    }
}
