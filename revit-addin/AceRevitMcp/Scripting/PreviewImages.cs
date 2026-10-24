using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;

namespace AceRevitMcp.Scripting
{
    /// <summary>
    /// Pictures of a previewed change, taken inside the dry run before it is rolled back: a plan (and a 3D view)
    /// around the added and modified elements, with those elements drawn in the accent colour. The temporary
    /// views disappear with the rollback, so the model is untouched.
    /// </summary>
    internal static class PreviewImages
    {
        /// <summary>Crops the white margin around an exported image (keeps a small border).</summary>
        private static byte[] Trim(byte[] png)
        {
            try
            {
                var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(png),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                var src = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);
                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var i = y * stride + x * 4;
                        if (px[i + 3] > 10 && (px[i] < 245 || px[i + 1] < 245 || px[i + 2] < 245))
                        { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                    }
                if (maxX < 0) return png;
                var pad = 16;
                minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad); maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
                var crop = new System.Windows.Media.Imaging.CroppedBitmap(src, new System.Windows.Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch { return png; }
        }

        public static JsonArray Capture(Document doc, ICollection<long> added, ICollection<long> modified)
        {
            var images = new JsonArray();
            var ids = added.Concat(modified).Distinct()
                .Select(i => doc.GetElement(new ElementId(i)))
                .Where(e => e != null && !(e is View) && !(e is ElementType) && e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .ToList();
            if (ids.Count == 0) return images;

            BoundingBoxXYZ box = null;
            foreach (var e in ids)
            {
                var b = e.get_BoundingBox(null);
                if (b == null) continue;
                if (box == null) box = new BoundingBoxXYZ { Min = b.Min, Max = b.Max };
                else
                {
                    box.Min = new XYZ(Math.Min(box.Min.X, b.Min.X), Math.Min(box.Min.Y, b.Min.Y), Math.Min(box.Min.Z, b.Min.Z));
                    box.Max = new XYZ(Math.Max(box.Max.X, b.Max.X), Math.Max(box.Max.Y, b.Max.Y), Math.Max(box.Max.Z, b.Max.Z));
                }
            }
            if (box == null) return images;

            var level = ids.Where(e => e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
                .GroupBy(e => e.LevelId.Value).OrderByDescending(g => g.Count()).Select(g => doc.GetElement(new ElementId(g.Key)) as Level).FirstOrDefault();
            var margin = Lengths.Ft(2500);
            var highlight = ViewTools.Highlight(doc, Branding.Accent, fill: false, lineWeight: 6);
            var highlightIds = ids.Select(e => e.Id).ToList();

            using (var t = new Transaction(doc, "ACE preview images"))
            {
                t.Start();
                var views = new List<(View view, string label)>();
                if (level != null)
                {
                    var planType = ViewTools.ViewType(doc, ViewFamily.FloorPlan);
                    if (planType != null)
                    {
                        var plan = ViewPlan.Create(doc, planType.Id, level.Id);
                        var size = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) + 2 * margin;
                        plan.Scale = size < 40 ? 50 : size < 120 ? 100 : 200;   // feet
                        plan.DetailLevel = ViewDetailLevel.Fine;
                        plan.CropBoxActive = true;
                        plan.CropBoxVisible = false;
                        var cb = plan.CropBox;
                        cb.Min = new XYZ(box.Min.X - margin, box.Min.Y - margin, cb.Min.Z);
                        cb.Max = new XYZ(box.Max.X + margin, box.Max.Y + margin, cb.Max.Z);
                        plan.CropBox = cb;
                        views.Add((plan, "Plan"));
                    }
                }
                var type3D = ViewTools.ViewType(doc, ViewFamily.ThreeDimensional);
                if (type3D != null)
                {
                    var iso = View3D.CreateIsometric(doc, type3D.Id);
                    iso.DetailLevel = ViewDetailLevel.Fine;
                    iso.DisplayStyle = DisplayStyle.ShadingWithEdges;
                    var zTop = level != null ? Math.Max(box.Max.Z, level.ProjectElevation + Lengths.Ft(3000)) : box.Max.Z;
                    iso.SetSectionBox(new BoundingBoxXYZ
                    {
                        Min = new XYZ(box.Min.X - margin, box.Min.Y - margin, box.Min.Z - Lengths.Ft(300)),
                        Max = new XYZ(box.Max.X + margin, box.Max.Y + margin, Math.Min(zTop, box.Min.Z + Lengths.Ft(2400))),
                    });
                    views.Add((iso, "3D"));
                }
                foreach (var (view, _) in views)
                    foreach (var id in highlightIds)
                        try { view.SetElementOverrides(id, highlight); } catch { }
                t.Commit();

                var dir = Path.Combine(Path.GetTempPath(), "ace-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    foreach (var (view, label) in views)
                    {
                        try
                        {
                            var file = ViewTools.ExportPng(doc, view.Id, dir, label, 1400);
                            if (file == null) continue;
                            images.Add(new JsonObject
                            {
                                ["view"] = label, ["mimeType"] = "image/png",
                                ["base64"] = Convert.ToBase64String(Trim(File.ReadAllBytes(file))),
                            });
                            File.Delete(file);
                        }
                        catch (Exception ex) { Log.Warn($"Preview image ({label}): {ex.Message}"); }
                    }
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
            return images;
        }
    }
}
