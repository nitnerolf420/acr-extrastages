using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using EgoEngineLibrary.Graphics;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Dr2Extract;

/// <summary>
/// Ground bake fallback for stages whose full-detail terrain does not use <c>terrain_array.fx</c> (the "WSM" shaders of
/// Finland, Sweden, Australia, Monte Carlo, ...): instead of re-implementing those shaders, the colour DR2 itself
/// pre-baked for its low-LOD terrain (<c>LOW_i_j</c> nodes, one diffuse texture per tile) is rasterised top-down into
/// the same tile format and manifest (<c>ground_bake/1</c>, low tier only). It is lower detail than the real bake
/// (about the resolution of DR2's own distant terrain) but it carries DR2's real ground colour instead of one tiling
/// grass texture.
/// </summary>
internal static partial class TerrainBake
{
    private struct LowTri
    {
        public int Tex;
        public Vector3 P0, P1, P2;
        public Vector2 U0, U1, U2;
    }

    /// <summary>Runs the fallback; any failure becomes an <see cref="InvalidDataException"/> so the caller soft-skips the bake as before.</summary>
    private static object LowLodFallback(PssgFile pssg, TextureCache textures, RouteCorridor corridor, StageRef st,
        string pssgPath, string dir, GroundBakeOptions o, Stopwatch timer)
    {
        try
        {
            return LowLodBake(pssg, textures, corridor, st, pssgPath, dir, o, timer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var pattern in new[] { "*.dds", "*.png", "manifest.json" })
            {
                foreach (var f in Directory.GetFiles(dir, pattern))
                {
                    File.Delete(f);
                }
            }

            throw new InvalidDataException(
                $"ground bake: no terrain_array triangles in the corridor (low-LOD fallback: {ex.GetType().Name}: {ex.Message})");
        }
    }

    private static object LowLodBake(PssgFile pssg, TextureCache textures, RouteCorridor corridor, StageRef st,
        string pssgPath, string dir, GroundBakeOptions o, Stopwatch timer)
    {
        Console.WriteLine("ground bake: no terrain_array triangles; trying DR2's low-LOD terrain colour (LOW_ nodes) instead");
        var images = new List<BakeTextureImage>();
        var imageIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tris = new List<LowTri>();
        var seen = new Dictionary<string, int>();
        var library = pssg.Elements<PssgLibrary>().First(x => x.Type == "NODE");
        foreach (var root in library.ChildElements.OfType<PssgNode>())
        {
            Walk(root, Matrix4x4.Identity);
        }

        void Walk(PssgNode node, Matrix4x4 parent)
        {
            var world = node.ChildElements.OfType<PssgTransform>().FirstOrDefault()?.Transform * parent ?? parent;
            if (node is PssgVisibleRenderNode visible && node.Id.StartsWith("LOW_", StringComparison.Ordinal))
            {
                foreach (var stream in visible.RenderInstances.OfType<PssgRenderStreamInstance>())
                {
                    var shader = stream.TryGetShaderInstance();
                    if (shader is null)
                    {
                        continue;
                    }

                    var group = shader.ShaderGroup.TrimStart('#');
                    var defs = shader.TryGetShaderGroup()!.InputDefinitions.ToList();
                    var names = shader.Inputs.Where(i => i.Type == "texture")
                        .Select(i => (Name: defs[(int)i.ParameterId].InputName, Tex: (string?)i.Texture)).ToList();
                    string? texName = null;
                    foreach (var want in new[] { "TDiffuseAlphaMap", "TDiffuseMap", "TDiffuse" })
                    {
                        texName = names.FirstOrDefault(x => x.Name == want).Tex;
                        if (texName is not null)
                        {
                            break;
                        }
                    }

                    var key = $"{group} [{string.Join(",", names.Select(x => x.Name))}]";
                    seen[key] = seen.GetValueOrDefault(key) + 1;
                    if (texName is null)
                    {
                        continue;
                    }

                    var tex = textures.Get(texName);
                    if (tex is null)
                    {
                        continue;
                    }

                    if (!imageIndex.TryGetValue(texName, out var ti))
                    {
                        ti = images.Count;
                        images.Add(tex);
                        imageIndex[texName] = ti;
                    }

                    var rds = new RdsReader(stream.GetRenderDataSource());
                    foreach (var (a, b, c) in rds.Triangles())
                    {
                        var p0 = Vector3.Transform(rds.Position(a), world);
                        var p1 = Vector3.Transform(rds.Position(b), world);
                        var p2 = Vector3.Transform(rds.Position(c), world);
                        if (!corridor.Contains((p0 + p1 + p2) / 3))
                        {
                            continue;
                        }

                        tris.Add(new LowTri
                        {
                            Tex = ti, P0 = p0, P1 = p1, P2 = p2,
                            U0 = rds.Uv(a, 0), U1 = rds.Uv(b, 0), U2 = rds.Uv(c, 0),
                        });
                    }
                }
            }

            foreach (var child in node.ChildElements.OfType<PssgNode>())
            {
                Walk(child, world);
            }
        }

        Console.WriteLine("ground bake: low-LOD shaders seen: " +
                          (seen.Count == 0 ? "none" : string.Join("; ", seen.Select(s => $"{s.Key} x{s.Value}"))));
        if (tris.Count == 0)
        {
            throw new InvalidDataException("no usable low-LOD terrain triangles (no LOW_ node with a diffuse texture inside the corridor)");
        }

        // Extent of the triangles and how far a single triangle reaches (as in the full bake: tile margin).
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        var half = new List<float>(tris.Count);
        foreach (var t in tris)
        {
            var c = (t.P0 + t.P1 + t.P2) / 3;
            var h = 0f;
            foreach (var p in new[] { t.P0, t.P1, t.P2 })
            {
                min = Vector2.Min(min, new Vector2(p.X, p.Z));
                max = Vector2.Max(max, new Vector2(p.X, p.Z));
                h = MathF.Max(h, MathF.Max(MathF.Abs(p.X - c.X), MathF.Abs(p.Z - c.Z)));
            }

            half.Add(h);
        }

        half.Sort();
        var reach = half[(int)(0.999 * (half.Count - 1))];

        List<Tile> Layout(double res)
        {
            var margin = Math.Clamp((int)Math.Ceiling(reach / res) + 2, 8, o.TilePx / 8);
            var core = (o.TilePx - 2 * margin) * res;
            var nx = Math.Max(1, (int)Math.Ceiling((max.X - min.X) / core));
            var nz = Math.Max(1, (int)Math.Ceiling((max.Y - min.Y) / core));
            var used = new HashSet<(int, int)>();
            foreach (var t in tris)
            {
                var x0 = Math.Min(t.P0.X, Math.Min(t.P1.X, t.P2.X));
                var x1 = Math.Max(t.P0.X, Math.Max(t.P1.X, t.P2.X));
                var z0 = Math.Min(t.P0.Z, Math.Min(t.P1.Z, t.P2.Z));
                var z1 = Math.Max(t.P0.Z, Math.Max(t.P1.Z, t.P2.Z));
                var i0 = Math.Clamp((int)Math.Floor((x0 - min.X) / core), 0, nx - 1);
                var i1 = Math.Clamp((int)Math.Floor((x1 - min.X) / core), 0, nx - 1);
                var j0 = Math.Clamp((int)Math.Floor((z0 - min.Y) / core), 0, nz - 1);
                var j1 = Math.Clamp((int)Math.Floor((z1 - min.Y) / core), 0, nz - 1);
                for (var j = j0; j <= j1; j++)
                {
                    for (var i = i0; i <= i1; i++)
                    {
                        used.Add((i, j));
                    }
                }
            }

            var list = new List<Tile>();
            for (var j = 0; j < nz; j++)
            {
                for (var i = 0; i < nx; i++)
                {
                    if (!used.Contains((i, j)))
                    {
                        continue;
                    }

                    var cx = min.X + i * core;
                    var cz = min.Y + j * core;
                    list.Add(new Tile($"low_{i:D2}_{j:D2}", "low", cx - margin * res, cz - margin * res, o.TilePx * res,
                        o.TilePx, cx, cz, core));
                }
            }

            return list;
        }

        var lowRes = o.LowMPerPx;
        var tileBytes = Bc1Bytes(o.TilePx);
        List<Tile> tiles;
        while (true)
        {
            tiles = Layout(lowRes);
            var mb = tiles.Count * tileBytes / 1e6;
            Console.WriteLine($"ground bake: low-LOD fallback at {lowRes * 100:F1} cm/px: {tiles.Count} tiles = {mb:F0} MB");
            if (mb <= o.BudgetMb || lowRes >= 4)
            {
                break;
            }

            lowRes = Math.Round(lowRes * 1.2, 4);
        }

        var manifestTiles = new List<object>();
        var px = o.TilePx;
        foreach (var tile in tiles)
        {
            var res = tile.Size / tile.Px;
            var colour = new Rgb24[px * px];
            var depth = new float[px * px];
            Array.Fill(depth, float.MinValue);
            foreach (var t in tris)
            {
                if (MathF.Max(t.P0.X, MathF.Max(t.P1.X, t.P2.X)) < tile.MinX || MathF.Min(t.P0.X, MathF.Min(t.P1.X, t.P2.X)) > tile.MinX + tile.Size ||
                    MathF.Max(t.P0.Z, MathF.Max(t.P1.Z, t.P2.Z)) < tile.MinZ || MathF.Min(t.P0.Z, MathF.Min(t.P1.Z, t.P2.Z)) > tile.MinZ + tile.Size)
                {
                    continue;
                }

                var img = images[t.Tex];
                var (u0, u1, u2) = (t.U0, t.U1, t.U2);
                Raster.Triangle(t.P0, t.P1, t.P2, tile.MinX, tile.MinZ, res, px, px, (i, j, b0, b1, b2, y) =>
                {
                    var k = j * px + i;
                    if (y <= depth[k])
                    {
                        return;
                    }

                    depth[k] = y;
                    var s = img.Sample(u0 * b0 + u1 * b1 + u2 * b2, 0, false);
                    colour[k] = Srgb(new Vector3(s.X, s.Y, s.Z));
                });
            }

            // Pixels no triangle reached (outside the corridor or in a gap): the tile's mean colour, so mips and
            // neighbouring triangles never see black.
            long sr = 0, sg = 0, sb = 0, n = 0;
            for (var k = 0; k < colour.Length; k++)
            {
                if (depth[k] > float.MinValue)
                {
                    sr += colour[k].R;
                    sg += colour[k].G;
                    sb += colour[k].B;
                    n++;
                }
            }

            var fill = n > 0 ? new Rgb24((byte)(sr / n), (byte)(sg / n), (byte)(sb / n)) : new Rgb24(96, 104, 72);
            for (var k = 0; k < colour.Length; k++)
            {
                if (depth[k] <= float.MinValue)
                {
                    colour[k] = fill;
                }
            }

            var dds = $"{tile.Name}.dds";
            var png = $"{tile.Name}.png";
            using (var image = Image.LoadPixelData<Rgb24>(colour, px, px))
            {
                WriteBc1(image, Path.Combine(dir, dds));
                using var preview = image.Clone(x => x.Resize(256, 256));
                preview.SaveAsPng(Path.Combine(dir, png));
            }

            manifestTiles.Add(new
            {
                name = tile.Name,
                tier = tile.Tier,
                dds,
                preview = png,
                px = tile.Px,
                m_per_px = Math.Round(res, 5),
                size_m = Math.Round(tile.Size, 4),
                bounds = new { min_x = Math.Round(tile.MinX, 4), min_z = Math.Round(tile.MinZ, 4), max_x = Math.Round(tile.MinX + tile.Size, 4), max_z = Math.Round(tile.MinZ + tile.Size, 4) },
                assign_bounds = new
                {
                    min_x = Math.Round(tile.CoreMinX, 4), min_z = Math.Round(tile.CoreMinZ, 4),
                    max_x = Math.Round(tile.CoreMinX + tile.CoreSize, 4), max_z = Math.Round(tile.CoreMinZ + tile.CoreSize, 4),
                },
            });
            Console.WriteLine($"  {tile.Name}: {res * 100:F1} cm/px at ({tile.MinX:F0}, {tile.MinZ:F0}), {timer.Elapsed.TotalSeconds:F0} s");
        }

        var totalBytes = Directory.GetFiles(dir, "*.dds").Sum(f => new FileInfo(f).Length);
        var manifest = new
        {
            schema = "ground_bake/1",
            stage = st.Id,
            source = pssgPath,
            fallback = "dr2_low_lod",
            coordinates = "IR (DR2 world) metres. Texture of a tile spans bounds; u = (x - bounds.min_x) / size_m, " +
                          "v = (z - bounds.min_z) / size_m, v = 0 is the first (top) row of the DDS. Assign each terrain " +
                          "triangle to the tile whose assign_bounds contain its centroid; bounds extend assign_bounds by a " +
                          "margin so neighbouring triangles stay inside the texture.",
            format = "BC1 (DXT1) DDS, sRGB colour, full mip chain",
            formula = "colour = DR2's own low-LOD terrain diffuse (LOW_ nodes) rasterised top-down; used when the stage's " +
                      "full-detail terrain has no terrain_array.fx (WSM shaders)",
            uv_mode = o.UvMode,
            road = new { excluded = false, layers = Array.Empty<int>(), mask = "none (low-LOD fallback)" },
            decals = false,
            tiers = new object[] { new { tier = "low", m_per_px = lowRes, tiles = tiles.Count, corridor_m = o.Corridor } },
            tile_px = o.TilePx,
            dds_bytes = totalBytes,
            triangles = new { total = tris.Count },
            previews = Array.Empty<string>(),
            tiles = manifestTiles,
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, Json.Options));
        Console.WriteLine($"ground bake: low-LOD fallback: {tiles.Count} low tiles ({lowRes * 100:F0} cm/px), {tris.Count} source triangles, " +
                          $"{totalBytes / 1e6:F1} MB DDS, {timer.Elapsed.TotalSeconds:F0} s -> {dir}");
        return new
        {
            manifest = "render/ground_bake/manifest.json",
            fallback = "dr2_low_lod",
            high_tiles = 0,
            low_tiles = tiles.Count,
            high_m_per_px = 0.0,
            low_m_per_px = lowRes,
            dds_bytes = totalBytes,
        };
    }
}
