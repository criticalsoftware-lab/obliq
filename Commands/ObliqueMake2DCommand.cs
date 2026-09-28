using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using obliq.Core;

namespace obliq.Commands
{
    /// <summary>
    /// Oblique hidden-line drawing using Rhino's exact HiddenLineDrawing engine.
    ///
    /// A plan oblique is a straight top-down parallel view of sheared geometry,
    /// which is exactly what the Obliq display conduit draws. So each object is
    /// duplicated, made deformable (exact NURBS form) and sheared with the same
    /// matrix as the conduit, then Rhino's HLD engine computes the drawing from
    /// a top-down parallel viewport. Results are mapped from HLD coordinates
    /// back to world XY so the drawing registers with the plan.
    /// </summary>
    public class ObliqueMake2DCommand : Command
    {
        public ObliqueMake2DCommand()
        {
            Instance = this;
        }

        public static ObliqueMake2DCommand Instance { get; private set; }

        public override string EnglishName => "ObliqueMake2D";

        private const string ParentLayer = "ObliqueMake2D";

        // Option values persist for the Rhino session.
        private static bool s_hiddenLines = true;
        private static bool s_tangentEdges = false;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null)
                return Result.Failure;

            // ------------------------------------------------------------------
            // 1. Pick objects (Enter = all visible objects, as before)
            // ------------------------------------------------------------------
            List<RhinoObject> sources = new List<RhinoObject>();
            Result pick = GetSources(doc, sources);
            if (pick != Result.Success)
                return pick;

            // ------------------------------------------------------------------
            // 2. Shear exact copies of the geometry (same matrix as the conduit)
            // ------------------------------------------------------------------
            // Transform shear = ObliqueProjection.Shear(
            //     ObliqueProjection.DefaultAngleDeg, ObliqueProjection.DefaultScale);

            // Use the projection currently set by the Obliq command.
            ObliqueConduit oc = ObliqueConduit.Instance;
            ObliqueMode projMode = oc.Mode;
            Transform shear = ObliqueProjection.Shear(projMode, oc.AngleDeg, oc.Scale);
            RhinoApp.WriteLine("ObliqueMake2D: {0} projection, angle {1:0.##}, scale {2:0.###}.",
                projMode, oc.AngleDeg, oc.Scale);

            HiddenLineDrawingParameters hp = new HiddenLineDrawingParameters
            {
                AbsoluteTolerance = doc.ModelAbsoluteTolerance,
                IncludeTangentEdges = s_tangentEdges,
                IncludeTangentSeams = false,
                IncludeHiddenCurves = s_hiddenLines
            };

            // The HLD parameters reference these copies, so they must stay alive
            // until the drawing has been computed.
            List<GeometryBase> sheared = new List<GeometryBase>();
            BoundingBox box = BoundingBox.Empty;
            int skipped = 0;

            foreach (RhinoObject obj in sources)
                AddSheared(hp, obj, obj.Id, shear, sheared, ref box, ref skipped);

            if (skipped > 0)
                RhinoApp.WriteLine("ObliqueMake2D: {0} object(s) could not be sheared and were skipped.", skipped);

            if (sheared.Count == 0 || !box.IsValid)
            {
                RhinoApp.WriteLine("ObliqueMake2D: Nothing to draw.");
                return Result.Nothing;
            }

            // ------------------------------------------------------------------
            // 3. Top-down parallel camera framing the sheared scene
            // ------------------------------------------------------------------
            // hp.SetViewport(TopViewport(box));
            hp.SetViewport(ProjectionViewport(projMode, box));

            // ------------------------------------------------------------------
            // 4. Compute
            // ------------------------------------------------------------------
            RhinoApp.WriteLine("ObliqueMake2D: Computing hidden lines for {0} object(s)...", sources.Count);

            int countVis = 0, countHid = 0;
            try
            {
                using (HiddenLineDrawing hld = HiddenLineDrawing.Compute(hp, true))
                {
                    if (hld == null)
                    {
                        RhinoApp.WriteLine("ObliqueMake2D: Hidden line computation failed.");
                        return Result.Failure;
                    }

                    // HLD results are in HLD coordinates. Map them back to the
                    // (sheared) world, then flatten to Z=0.
                    Transform toWorld;
                    if (!hld.WorldToHiddenLine.TryGetInverse(out toWorld))
                    {
                        RhinoApp.WriteLine("ObliqueMake2D: Could not map results back to world coordinates.");
                        return Result.Failure;
                    }
                    //Transform place = ObliqueProjection.Flatten() * toWorld;
                    Transform place = ObliqueProjection.ToDrawingPlane(projMode) * toWorld;

                    // --------------------------------------------------------------
                    // 5. Output
                    // --------------------------------------------------------------
                    int layerVisible = FindOrCreateLayer(doc, "Visible", Color.FromArgb(0, 0, 0));
                    int layerHidden = FindOrCreateLayer(doc, "Hidden", Color.FromArgb(128, 128, 128));
                    int dashIdx = doc.Linetypes.Find("Dashed");

                    ObjectAttributes visAttrs = MakeAttributes(layerVisible, Color.FromArgb(0, 0, 0), -1);
                    ObjectAttributes hidAttrs = MakeAttributes(layerHidden, Color.FromArgb(128, 128, 128), dashIdx);

                    foreach (HiddenLineDrawingSegment seg in hld.Segments)
                    {
                        if (seg == null || seg.ParentCurve == null ||
                            seg.ParentCurve.SilhouetteType == SilhouetteType.None)
                            continue;

                        ObjectAttributes attrs;
                        if (seg.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Visible)
                            attrs = visAttrs;
                        else if (seg.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Hidden)
                            attrs = hidAttrs;
                        else
                            continue; // Duplicate, Projecting, Clipped, Unset

                        Curve crv = seg.CurveGeometry == null ? null : seg.CurveGeometry.DuplicateCurve();
                        if (crv == null || !crv.Transform(place))
                            continue;

                        if (doc.Objects.AddCurve(crv, attrs) != Guid.Empty)
                        {
                            if (attrs == visAttrs) countVis++; else countHid++;
                        }
                        crv.Dispose();
                    }

                    foreach (HiddenLineDrawingPoint hpt in hld.Points)
                    {
                        if (hpt == null || !hpt.Location.IsValid)
                            continue;

                        ObjectAttributes attrs;
                        if (hpt.PointVisibility == HiddenLineDrawingPoint.Visibility.Visible)
                            attrs = visAttrs;
                        else if (hpt.PointVisibility == HiddenLineDrawingPoint.Visibility.Hidden)
                            attrs = hidAttrs;
                        else
                            continue;

                        Point3d pt = hpt.Location;
                        pt.Transform(place);
                        doc.Objects.AddPoint(pt, attrs);
                    }
                }
            }
            finally
            {
                foreach (GeometryBase g in sheared)
                    g.Dispose();
            }

            doc.Views.Redraw();

            RhinoApp.WriteLine("ObliqueMake2D: Done - {0} visible, {1} hidden segments.", countVis, countHid);
            return Result.Success;
        }

        // ======================================================================
        // Input
        // ======================================================================

        private static Result GetSources(RhinoDoc doc, List<RhinoObject> sources)
        {
            OptionToggle optHidden = new OptionToggle(s_hiddenLines, "No", "Yes");
            OptionToggle optTangent = new OptionToggle(s_tangentEdges, "No", "Yes");

            GetObject go = new GetObject();
            go.SetCommandPrompt("Select objects to draw. Press Enter for all visible objects");
            go.GeometryFilter =
                ObjectType.Point | ObjectType.PointSet | ObjectType.Curve |
                ObjectType.Surface | ObjectType.PolysrfFilter | ObjectType.Extrusion |
                ObjectType.Mesh | ObjectType.SubD | ObjectType.InstanceReference;
            go.GroupSelect = true;
            go.SubObjectSelect = false;
            go.EnablePreSelect(true, true);
            go.AcceptNothing(true);
            go.AddOptionToggle("HiddenLines", ref optHidden);
            go.AddOptionToggle("TangentEdges", ref optTangent);

            for (;;)
            {
                GetResult res = go.GetMultiple(1, 0);

                if (res == GetResult.Option)
                {
                    go.EnablePreSelect(false, true);
                    continue;
                }

                if (res == GetResult.Nothing)
                {
                    sources.AddRange(AllVisibleObjects(doc));
                    break;
                }

                if (res == GetResult.Object)
                {
                    foreach (ObjRef r in go.Objects())
                    {
                        RhinoObject o = r.Object();
                        if (o != null) sources.Add(o);
                    }
                    break;
                }

                return go.CommandResult();
            }

            s_hiddenLines = optHidden.CurrentValue;
            s_tangentEdges = optTangent.CurrentValue;

            if (sources.Count == 0)
            {
                RhinoApp.WriteLine("ObliqueMake2D: No objects to draw.");
                return Result.Nothing;
            }
            return Result.Success;
        }

        /// <summary>All visible objects, skipping previous oblique Make2D output.</summary>
        private static IEnumerable<RhinoObject> AllVisibleObjects(RhinoDoc doc)
        {
            ObjectEnumeratorSettings settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = false,
                DeletedObjects = false,
                VisibleFilter = true,
                IncludeLights = false,
                IncludeGrips = false
            };

            foreach (RhinoObject obj in doc.Objects.GetObjectList(settings))
            {
                int li = obj.Attributes.LayerIndex;
                if (li >= 0 && li < doc.Layers.Count)
                {
                    string path = doc.Layers[li].FullPath ?? string.Empty;
                    if (path == ParentLayer ||
                        path.StartsWith(ParentLayer + "::", StringComparison.Ordinal) ||
                        path.StartsWith("ObliqueCurveMake2D", StringComparison.Ordinal))
                        continue;
                }
                yield return obj;
            }
        }

        // ======================================================================
        // Geometry
        // ======================================================================

        /// <summary>
        /// Duplicates the object's geometry in a form HLD accepts, converts it to
        /// an exact deformable (NURBS) form, applies the shear and adds it.
        /// Block instances are exploded recursively (their parts keep the
        /// instance's id as tag).
        /// </summary>
        private static void AddSheared(HiddenLineDrawingParameters hp, RhinoObject obj, Guid tag,
            Transform shear, List<GeometryBase> keepAlive, ref BoundingBox box, ref int skipped)
        {
            if (obj == null)
                return;

            if (obj.ObjectType == ObjectType.InstanceReference)
            {
                RhinoObject[] parts = obj.GetSubObjects();
                if (parts != null)
                    foreach (RhinoObject part in parts)
                        AddSheared(hp, part, tag, shear, keepAlive, ref box, ref skipped);
                return;
            }

            GeometryBase g = ToHldGeometry(obj.Geometry);
            if (g == null)
                return; // annotations, text dots, hatches, lights etc.

            // Planes, surfaces of revolution, arcs, etc. can't represent a shear;
            // their NURBS forms can, exactly.
            if (!g.IsDeformable)
                g.MakeDeformable();

            if (!g.Transform(shear) || !hp.AddGeometry(g, tag))
            {
                g.Dispose();
                skipped++;
                return;
            }

            keepAlive.Add(g);
            box.Union(g.GetBoundingBox(true));
        }

        /// <summary>
        /// HLD supports Brep, Curve, Mesh, Point and PointCloud. Extrusions and
        /// SubDs are converted to Breps (as in McNeel's cmdSampleMake2D).
        /// Always returns a new object owned by the caller.
        /// </summary>
        private static GeometryBase ToHldGeometry(GeometryBase g)
        {
            if (g == null)
                return null;

            Extrusion ext = g as Extrusion;
            if (ext != null)
                return ext.ToBrep(true);

            SubD subd = g as SubD;
            if (subd != null)
                return subd.ToBrep(SubDToBrepOptions.DefaultUnpacked);

            if (g is Brep || g is Curve || g is Mesh || g is Rhino.Geometry.Point || g is PointCloud)
                return g.Duplicate();

            return null;
        }
        
        /// <summary>
        /// Parallel, world-axis-aligned viewport framing the sheared box.
        /// Plan: looking down -Z, Y up. Cabinet: looking along +Y (front), Z up.
        /// </summary>
        private static ViewportInfo ProjectionViewport(ObliqueMode mode, BoundingBox box)
        {
            double pad = 0.1 * box.Diagonal.Length + 1.0;
            Point3d c = box.Center;

            ViewportInfo vi = new ViewportInfo();
            vi.ChangeToParallelProjection(true);

            double hw, hh, depth;
            if (mode == ObliqueMode.Cabinet)
            {
                hw = 0.5 * (box.Max.X - box.Min.X) + pad;
                hh = 0.5 * (box.Max.Z - box.Min.Z) + pad;
                depth = (box.Max.Y - box.Min.Y) + 2.0 * pad;
                vi.SetCameraLocation(new Point3d(c.X, box.Min.Y - pad, c.Z));
                vi.SetCameraDirection(new Vector3d(0.0, 1.0, 0.0));
                vi.SetCameraUp(new Vector3d(0.0, 0.0, 1.0));
            }
            else
            {
                hw = 0.5 * (box.Max.X - box.Min.X) + pad;
                hh = 0.5 * (box.Max.Y - box.Min.Y) + pad;
                depth = (box.Max.Z - box.Min.Z) + 2.0 * pad;
                vi.SetCameraLocation(new Point3d(c.X, c.Y, box.Max.Z + pad));
                vi.SetCameraDirection(new Vector3d(0.0, 0.0, -1.0));
                vi.SetCameraUp(new Vector3d(0.0, 1.0, 0.0));
            }

            vi.SetFrustum(-hw, hw, -hh, hh, 0.5 * pad, depth);
            return vi;
        }

        /// <summary>Straight-down parallel viewport, world-axis aligned, framing the box.</summary>
        private static ViewportInfo TopViewport(BoundingBox box)
        {
            double pad = 0.1 * box.Diagonal.Length + 1.0;
            double hw = 0.5 * (box.Max.X - box.Min.X) + pad;
            double hh = 0.5 * (box.Max.Y - box.Min.Y) + pad;
            double depth = (box.Max.Z - box.Min.Z) + 2.0 * pad;

            ViewportInfo vi = new ViewportInfo();
            vi.ChangeToParallelProjection(true);
            vi.SetCameraLocation(new Point3d(box.Center.X, box.Center.Y, box.Max.Z + pad));
            vi.SetCameraDirection(new Vector3d(0.0, 0.0, -1.0));
            vi.SetCameraUp(new Vector3d(0.0, 1.0, 0.0));
            vi.SetFrustum(-hw, hw, -hh, hh, 0.5 * pad, depth);
            return vi;
        }

        // ======================================================================
        // Output helpers
        // ======================================================================

        private static ObjectAttributes MakeAttributes(int layerIndex, Color color, int linetypeIndex)
        {
            ObjectAttributes attrs = new ObjectAttributes
            {
                LayerIndex = layerIndex,
                ObjectColor = color,
                ColorSource = ObjectColorSource.ColorFromObject
            };
            if (linetypeIndex >= 0)
            {
                attrs.LinetypeIndex = linetypeIndex;
                attrs.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
            }
            return attrs;
        }

        /// <summary>Finds or creates ObliqueMake2D::childName.</summary>
        private static int FindOrCreateLayer(RhinoDoc doc, string childName, Color color)
        {
            string fullPath = ParentLayer + "::" + childName;
            int idx = doc.Layers.FindByFullPath(fullPath, -1);
            if (idx >= 0)
                return idx;

            int parentIdx = doc.Layers.FindByFullPath(ParentLayer, -1);
            if (parentIdx < 0)
                parentIdx = doc.Layers.Add(new Layer { Name = ParentLayer, Color = Color.Black });

            Layer child = new Layer { Name = childName, Color = color };
            if (parentIdx >= 0)
                child.ParentLayerId = doc.Layers[parentIdx].Id;

            return doc.Layers.Add(child);
        }
    }
}
