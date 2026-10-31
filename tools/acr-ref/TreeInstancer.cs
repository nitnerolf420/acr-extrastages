using System.Text.Json.Nodes;

namespace AcrRef;

/// ACR's own tree and ground cover meshes (config/scenery.toml [trees]). The persistent level of the host stage (Hafren
/// South) holds forest components (HierarchicalInstancedStaticMeshComponent); their instance data is replaced (HismRewrite
/// keeps the export's size) and they are left visible when the rest of the Hafren scenery is hidden.
///   trees: components in `reuse` get the DR2 trees (their own mesh stays: a spruce)
///   cover: components in `cover_reuse` get the DR2 bush / ground cover spots, and their mesh reference is swapped to one of
///          `cover_meshes` (grass, shrubs ...: a new import is added to the level, the export's preload dependency follows)
/// VisualObjects records the DR2 trees and plants (and skips their proxies) while Enabled / CoverEnabled; Scenery.Build
/// applies the result to the persistent package.
internal static class TreeInstancer
{
    public sealed record Tree(double X, double Y, double Z, double Yaw, double Height);
    public sealed record Plant(double X, double Y, double Z, double Yaw, double Radius);
    public sealed record Target(int Export, string Key, HismRewrite.Layout Layout);

    public static readonly List<Tree> Trees = new();
    public static readonly List<Plant> Plants = new();
    public static bool Enabled, CoverEnabled;
    public static int Budget, CoverBudget;

    static double Num(Toml cfg, string key, double fallback) => cfg["trees"].TryGetValue(key, out var v) && v is double d ? d : fallback;
    static string[] Strs(Toml cfg, string key) => cfg["trees"].TryGetValue(key, out var v) && v is object[] a ? a.OfType<string>().ToArray() : [];

    public static void Reset() { Trees.Clear(); Plants.Clear(); Enabled = CoverEnabled = false; Budget = CoverBudget = 0; }

    /// "actor|component" export names: the key used in [trees] reuse / cover_reuse.
    public static string KeyOf(LegacyPackage pkg, int comp)
    {
        int o = pkg.ExportOuter(comp);
        return $"{(o > 0 ? pkg.ExportName(o - 1) : "")}|{pkg.ExportName(comp)}";
    }

    public static List<Target> Find(LegacyPackage pkg, Toml cfg, string listKey = "reuse", ISet<string>? exclude = null)
    {
        var want = Strs(cfg, listKey).ToHashSet();
        var list = new List<Target>();
        for (int e = 0; e < pkg.ExportCount; e++)
        {
            if (pkg.ExportClass(e) != "HierarchicalInstancedStaticMeshComponent") continue;
            var key = KeyOf(pkg, e);
            if (!want.Contains(key) || exclude?.Contains(key) == true) continue;
            var layout = HismRewrite.Parse(pkg.GetExportData(e));
            if (layout is null) { Console.WriteLine($"trees: {key}: export layout not recognised (cooked render data?), not reused"); continue; }
            list.Add(new Target(e, key, layout));
        }
        return list;
    }

    /// Before the visuals are built: how many trees / plants fit into the configured components.
    public static void Prepare(string legacyRoot, Toml cfg)
    {
        Reset();
        if (Num(cfg, "enabled", 0) == 0) return;
        bool trees = Strs(cfg, "reuse").Length > 0;
        bool cover = Num(cfg, "cover", 0) != 0 && Strs(cfg, "cover_reuse").Length > 0 && Strs(cfg, "cover_meshes").Length > 0;
        if (!trees && !cover) return;
        var pkg = new LegacyPackage(HostPaths.Resolve(legacyRoot, Scenery.Persistent));
        var treeTargets = trees ? Find(pkg, cfg) : [];
        Budget = (int)Math.Min(treeTargets.Sum(t => (long)t.Layout.Count), (long)Num(cfg, "max_trees", 30000));
        Enabled = Budget > 0;
        if (cover)
        {
            var coverTargets = Find(pkg, cfg, "cover_reuse", treeTargets.Select(t => t.Key).ToHashSet());
            CoverBudget = (int)Math.Min(coverTargets.Sum(t => (long)t.Layout.Count), (long)Num(cfg, "max_plants", 30000));
            CoverEnabled = CoverBudget > 0;
        }
        Console.WriteLine($"trees: room for {Budget} trees; ground cover: room for {CoverBudget} plants");
    }

    /// Rewrites the targets' export data in `pkg`.
    public static JsonObject Apply(LegacyPackage pkg, Toml cfg, List<Target> targets, List<Target> cover)
    {
        var report = new JsonObject();
        var reused = new JsonArray();
        int placedTrees = targets.Count > 0 ? ApplyTrees(pkg, cfg, targets, reused) : 0;
        var coverReport = cover.Count > 0 ? ApplyCover(pkg, cfg, cover, reused) : null;
        report["reused"] = reused;
        report["trees_placed"] = placedTrees;
        report["trees_recorded"] = Trees.Count;
        report["capacity"] = targets.Sum(t => (long)t.Layout.Count);
        if (coverReport is not null) report["cover"] = coverReport;
        Console.WriteLine($"trees: {placedTrees} DR2 trees placed into {targets.Count} forest component(s) ({Trees.Count} recorded)");
        return report;
    }

    /// Trees spread over the components (random, weighted by room), ordered along a Z-order curve inside each, the
    /// remaining slots become dead instances far below the stage.
    static int ApplyTrees(LegacyPackage pkg, Toml cfg, List<Target> targets, JsonArray reused)
    {
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
        return placed;
    }

    /// Plants: every cover component gets one of the cover meshes (cycled), the plants are spread over the components.
    static JsonObject ApplyCover(LegacyPackage pkg, Toml cfg, List<Target> cover, JsonArray reused)
    {
        var meshes = Strs(cfg, "cover_meshes");
        var rnd = new Random((int)Num(cfg, "seed", 1) + 7);
        var plants = Plants.Take(Math.Max(0, CoverBudget)).ToList();
        double x0 = plants.Count == 0 ? 0 : plants.Min(t => t.X), x1 = plants.Count == 0 ? 1 : plants.Max(t => t.X);
        double y0 = plants.Count == 0 ? 0 : plants.Min(t => t.Y), y1 = plants.Count == 0 ? 1 : plants.Max(t => t.Y);
        double deadZ = (plants.Count == 0 ? 0 : plants.Min(t => t.Z)) - 20000;

        // 1. swap each component's mesh reference
        var imported = new Dictionary<string, int>();
        var data = new byte[cover.Count][];
        var swapped = new bool[cover.Count];
        var meshOf = new string[cover.Count];
        for (int i = 0; i < cover.Count; i++)
        {
            var t = cover[i];
            data[i] = pkg.GetExportData(t.Export);
            var copy = (byte[])data[i].Clone();
            var deps = pkg.ExportDeps(t.Export);
            int oldRef = 0;
            foreach (var (_, _, index) in deps)
                if (index < 0 && pkg.ImportName(-index - 1).StartsWith("SM_", StringComparison.Ordinal)) { oldRef = index; break; }
            string path = meshes[i % meshes.Length];
            meshOf[i] = path;
            if (oldRef == 0) { Console.WriteLine($"trees: {t.Key}: no static mesh import found, left empty"); continue; }
            if (!imported.TryGetValue(path, out var newRef))
            {
                int cut = -oldRef - 1;
                int pkgImp = pkg.AddImport("/Script/CoreUObject", "Package", 0, path);
                newRef = pkg.AddImport(pkg.ImportClassPackage(cut), pkg.ImportClassName(cut), pkgImp, path[(path.LastIndexOf('/') + 1)..]);
                imported[path] = newRef;
            }
            if (!HismRewrite.SwapReference(copy, t.Layout, oldRef, newRef)) { Console.WriteLine($"trees: {t.Key}: mesh reference not found exactly once, left empty"); continue; }
            foreach (var (_, pos, index) in deps) if (index == oldRef) pkg.SetDep(pos, newRef);
            data[i] = copy;
            swapped[i] = true;
        }

        // 2. spread the plants over the swapped components
        var room = cover.Select((t, i) => swapped[i] ? t.Layout.Count : 0).ToArray();
        var buckets = cover.Select(_ => new List<Plant>()).ToList();
        foreach (var plant in plants.OrderBy(_ => rnd.Next()))
        {
            long left = room.Sum(x => (long)x);
            if (left <= 0) break;
            double r = rnd.NextDouble() * left;
            int k = 0;
            for (; k < room.Length - 1; k++) { if (r < room[k]) break; r -= room[k]; }
            if (room[k] <= 0) k = Array.FindIndex(room, x => x > 0);
            buckets[k].Add(plant); room[k]--;
        }

        int placed = 0;
        for (int i = 0; i < cover.Count; i++)
        {
            var t = cover[i];
            var old = pkg.GetExportData(t.Export);
            var box = HismRewrite.LocalBox(old, t.Layout) ?? (new double[] { -250, -250, 0 }, new double[] { 250, 250, 2000 });
            var ordered = buckets[i].OrderBy(b => HismRewrite.Morton(b.X, b.Y, x0, y0, x1, y1)).ToList();
            var inst = new List<HismRewrite.Instance>(t.Layout.Count);
            foreach (var b in ordered)
                inst.Add(new HismRewrite.Instance(b.X, b.Y, b.Z, rnd.NextDouble() * 2 * Math.PI, Math.Clamp(b.Radius / 120.0, 0.7, 1.7) * (0.9 + 0.2 * rnd.NextDouble())));
            double lastX = ordered.Count > 0 ? ordered[^1].X : 0, lastY = ordered.Count > 0 ? ordered[^1].Y : 0;
            while (inst.Count < t.Layout.Count) inst.Add(new HismRewrite.Instance(lastX, lastY, deadZ, 0, 0));
            // the swapped reference lives in data[i]; the instance data is written over it
            pkg.SetExportData(t.Export, HismRewrite.Rewrite(data[i], t.Layout, inst, box));
            placed += ordered.Count;
            reused.Add(t.Key);
        }
        Console.WriteLine($"trees: {placed} ground cover plants placed into {cover.Count(c => true)} component(s), {imported.Count} meshes imported");
        return new JsonObject { ["plants_placed"] = placed, ["plants_recorded"] = Plants.Count, ["meshes"] = new JsonArray(imported.Keys.Select(k => (JsonNode)k).ToArray()), ["components"] = cover.Count };
    }
}
