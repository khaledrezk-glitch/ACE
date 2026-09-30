using System;
using System.Collections.Generic;
using System.Linq;
using AceRevitMcp.Util;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;

namespace AceRevitMcp.Coordination
{
    /// <summary>
    /// Temporary 3D graphics in the ACE Clash View (Revit DirectContext3D): the two clashing elements in their model
    /// colours and the intersection in gold, drawn over the dimmed model. Works for elements of links and of other
    /// open models, which Revit cannot colour one by one. Nothing is added to the model.
    /// </summary>
    internal sealed class ClashHighlight : IDirectContext3DServer
    {
        internal sealed class Shape
        {
            public List<XYZ[]> Triangles = new List<XYZ[]>();
            public List<XYZ[]> Edges = new List<XYZ[]>();
            public ColorWithTransparency Color;
            public bool Transparent => Color.GetTransparency() > 0;
        }

        private sealed class Batch
        {
            public VertexBuffer Vb; public IndexBuffer Ib; public VertexFormat Format; public EffectInstance Effect;
            public int Vertices, Indices, Primitives; public PrimitiveType Type; public bool Transparent;
        }

        private static readonly Guid Id = new Guid("5b7e0c9e-3a52-4d7e-9a1d-ace0c1a5b001");
        internal static ClashHighlight Instance { get; private set; }

        private List<Shape> _shapes = new List<Shape>();
        private List<Batch> _batches;
        private ElementId _viewId = ElementId.InvalidElementId;
        private Outline _bounds;

        /// <summary>Registers the server once (must run in a Revit API context).</summary>
        internal static ClashHighlight Ensure()
        {
            if (Instance != null) return Instance;
            var service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
            if (service == null) throw new InvalidOperationException("Revit's DirectContext3D service is not available.");
            var server = new ClashHighlight();
            service.AddServer(server);
            var active = service.GetActiveServerIds();
            if (!active.Contains(Id)) active.Add(Id);
            service.SetActiveServers(active);
            return Instance = server;
        }

        internal void Show(ElementId viewId, List<Shape> shapes)
        {
            _viewId = viewId;
            _shapes = shapes ?? new List<Shape>();
            DisposeBatches();
            var pts = _shapes.SelectMany(s => s.Triangles.SelectMany(t => t)).ToList();
            _bounds = pts.Count == 0 ? null : new Outline(
                new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)),
                new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z)));
        }

        internal void Clear() { _shapes = new List<Shape>(); DisposeBatches(); _bounds = null; }

        // ---- IDirectContext3DServer --------------------------------------------------------------------------------
        public Guid GetServerId() => Id;
        public string GetVendorId() => "ACE";
        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName() => "ACE clash highlight";
        public string GetDescription() => "Draws the selected clash (both elements and their intersection) in the ACE Clash View.";
        public string GetApplicationId() => "";
        public string GetSourceId() => "";
        public bool UsesHandles() => false;
        public bool CanExecute(View view) => view != null && view.Id == _viewId && _shapes.Count > 0;
        public Outline GetBoundingBox(View view) => _bounds;
        public bool UseInTransparentPass(View view) => _shapes.Any(s => s.Transparent);

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            try
            {
                if (_batches == null) _batches = Build(_shapes);
                var transparentPass = DrawContext.IsTransparentPass();
                foreach (var b in _batches.Where(b => b.Transparent == transparentPass))
                    DrawContext.FlushBuffer(b.Vb, b.Vertices, b.Ib, b.Indices, b.Format, b.Effect, b.Type, 0, b.Primitives);
            }
            catch (Exception ex) { Log.Warn($"Clash highlight: {ex.Message}"); }
        }

        // ---- buffers ---------------------------------------------------------------------------------------------------
        private const int MaxTrianglesPerBatch = 20000;   // 60 000 vertices: within 16-bit indices

        private static List<Batch> Build(List<Shape> shapes)
        {
            var batches = new List<Batch>();
            foreach (var s in shapes)
            {
                for (var start = 0; start < s.Triangles.Count; start += MaxTrianglesPerBatch)
                {
                    var tris = s.Triangles.Skip(start).Take(MaxTrianglesPerBatch).ToList();
                    var vcount = tris.Count * 3;
                    var vb = new VertexBuffer(VertexPositionNormalColored.GetSizeInFloats() * vcount);
                    vb.Map(VertexPositionNormalColored.GetSizeInFloats() * vcount);
                    var vs = vb.GetVertexStreamPositionNormalColored();
                    foreach (var t in tris)
                    {
                        var n = (t[1] - t[0]).CrossProduct(t[2] - t[0]);
                        n = n.GetLength() > 1e-12 ? n.Normalize() : XYZ.BasisZ;
                        foreach (var p in t) vs.AddVertex(new VertexPositionNormalColored(p, n, s.Color));
                    }
                    vb.Unmap();
                    var ib = new IndexBuffer(IndexTriangle.GetSizeInShortInts() * tris.Count);
                    ib.Map(IndexTriangle.GetSizeInShortInts() * tris.Count);
                    var ist = ib.GetIndexStreamTriangle();
                    for (var i = 0; i < tris.Count; i++) ist.AddTriangle(new IndexTriangle(3 * i, 3 * i + 1, 3 * i + 2));
                    ib.Unmap();
                    batches.Add(new Batch
                    {
                        Vb = vb, Ib = ib, Vertices = vcount, Indices = tris.Count * 3, Primitives = tris.Count, Type = PrimitiveType.TriangleList,
                        Format = new VertexFormat(VertexFormatBits.PositionNormalColored), Effect = new EffectInstance(VertexFormatBits.PositionNormalColored),
                        Transparent = s.Transparent,
                    });
                }
                if (s.Edges.Count > 0)
                {
                    var edges = s.Edges.Take(30000).ToList();
                    var vcount = edges.Count * 2;
                    var edgeColor = new ColorWithTransparency(s.Color.GetRed() / 2, s.Color.GetGreen() / 2, s.Color.GetBlue() / 2, 0);
                    var vb = new VertexBuffer(VertexPositionColored.GetSizeInFloats() * vcount);
                    vb.Map(VertexPositionColored.GetSizeInFloats() * vcount);
                    var vs = vb.GetVertexStreamPositionColored();
                    foreach (var e in edges) { vs.AddVertex(new VertexPositionColored(e[0], edgeColor)); vs.AddVertex(new VertexPositionColored(e[1], edgeColor)); }
                    vb.Unmap();
                    var ib = new IndexBuffer(IndexLine.GetSizeInShortInts() * edges.Count);
                    ib.Map(IndexLine.GetSizeInShortInts() * edges.Count);
                    var il = ib.GetIndexStreamLine();
                    for (var i = 0; i < edges.Count; i++) il.AddLine(new IndexLine(2 * i, 2 * i + 1));
                    ib.Unmap();
                    batches.Add(new Batch
                    {
                        Vb = vb, Ib = ib, Vertices = vcount, Indices = edges.Count * 2, Primitives = edges.Count, Type = PrimitiveType.LineList,
                        Format = new VertexFormat(VertexFormatBits.PositionColored), Effect = new EffectInstance(VertexFormatBits.PositionColored),
                        Transparent = false,
                    });
                }
            }
            return batches;
        }

        private void DisposeBatches()
        {
            if (_batches == null) return;
            foreach (var b in _batches) { try { b.Vb.Dispose(); b.Ib.Dispose(); b.Format.Dispose(); b.Effect.Dispose(); } catch { } }
            _batches = null;
        }

        // ---- geometry helpers --------------------------------------------------------------------------------------------

        /// <summary>
        /// Triangles and edges of solids, moved into the active model's coordinates and pushed out a few millimetres
        /// along each face so they draw over the real (dimmed) element instead of flickering with it.
        /// </summary>
        internal static Shape FromSolids(IEnumerable<Solid> solids, ColorWithTransparency color, double offsetFeet, int maxTriangles = 60000)
        {
            var shape = new Shape { Color = color };
            foreach (var solid in solids)
            {
                foreach (Face f in solid.Faces)
                {
                    Mesh m;
                    try { m = f.Triangulate(); } catch { continue; }
                    if (m == null) continue;
                    for (var i = 0; i < m.NumTriangles && shape.Triangles.Count < maxTriangles; i++)
                    {
                        var t = m.get_Triangle(i);
                        var a = t.get_Vertex(0); var b = t.get_Vertex(1); var c = t.get_Vertex(2);
                        var n = (b - a).CrossProduct(c - a);
                        var off = n.GetLength() > 1e-12 ? n.Normalize() * offsetFeet : XYZ.Zero;
                        shape.Triangles.Add(new[] { a + off, b + off, c + off });
                    }
                }
                foreach (Edge e in solid.Edges)
                {
                    try
                    {
                        var pts = e.Tessellate();
                        for (var i = 1; i < pts.Count && shape.Edges.Count < 30000; i++) shape.Edges.Add(new[] { pts[i - 1], pts[i] });
                    }
                    catch { }
                }
            }
            return shape;
        }
    }
}
