using System;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Obliq
{
    public class ObliqueMake2DCommand : Command
    {
        public ObliqueMake2DCommand()
        {
            Instance = this;
        }

        public static ObliqueMake2DCommand Instance { get; private set; }

        public override string EnglishName => "ObliqueMake2D";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;

            double angleDeg = 90.0;
            double scale = 1.0;

            double alpha = angleDeg * Math.PI / 180.0;
            double shx = scale * Math.Cos(alpha);
            double shy = scale * Math.Sin(alpha);

            Transform shear = Transform.Identity;
            shear[0, 2] = shx;
            shear[1, 2] = shy;

            RhinoApp.WriteLine("ObliqueMake2D: Collecting geometry...");

            ObliqueHiddenLineEngine hld = new ObliqueHiddenLineEngine();
            hld.SetTransform(shear);
            hld.SetSampleDensity(128);
            hld.SetDepthTolerance(1e-3);
            hld.SetEdgeAngleThreshold(9.0);
            hld.AddObjectsFromDoc(doc);

            RhinoApp.WriteLine("ObliqueMake2D: Computing hidden lines...");

            if (!hld.Compute())
            {
                RhinoApp.WriteLine("ObliqueMake2D: Computation failed.");
                return Result.Failure;
            }

            int total = hld.ResultCount;
            if (total == 0)
            {
                RhinoApp.WriteLine("ObliqueMake2D: No edges found.");
                return Result.Nothing;
            }

            RhinoApp.WriteLine($"ObliqueMake2D: {total} segments computed.");

            int layerVisible = FindOrCreateLayer(doc, "ObliqueMake2D::Visible", Color.Black);
            int layerHidden = FindOrCreateLayer(doc, "ObliqueMake2D::Hidden", Color.Gray);

            var results = hld.DetachResults();
            int countVis = 0, countHid = 0;

            int dashIdx = doc.Linetypes.Find("Dashed");

            foreach (var seg in results)
            {
                if (seg.Curve == null) continue;

                FlattenCurveTo2D(seg.Curve);

                ObjectAttributes attrs = new ObjectAttributes();
                
                if (seg.Visibility == HiddenLineVisibility.Visible)
                {
                    attrs.LayerIndex = layerVisible;
                    attrs.ObjectColor = Color.Black;
                    countVis++;
                }
                else
                {
                    attrs.LayerIndex = layerHidden;
                    attrs.ObjectColor = Color.Gray;
                    if (dashIdx >= 0) attrs.LinetypeIndex = dashIdx;
                    countHid++;
                }

                attrs.ColorSource = ObjectColorSource.ColorFromObject;
                attrs.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;

                doc.Objects.AddCurve(seg.Curve, attrs);
            }

            doc.Views.Redraw();
            RhinoApp.WriteLine($"ObliqueMake2D: Done - {countVis} visible, {countHid} hidden segments.");

            return Result.Success;
        }

        private int FindOrCreateLayer(RhinoDoc doc, string name, Color color)
        {
            int idx = doc.Layers.FindByFullPath(name, -1);
            if (idx >= 0) return idx;

            Layer layer = new Layer
            {
                Name = name,
                Color = color
            };
            return doc.Layers.Add(layer);
        }

        private void FlattenCurveTo2D(Curve crv)
        {
            if (crv == null) return;
            Transform flatten = Transform.Identity;
            flatten[2, 2] = 0.0;
            crv.Transform(flatten);
        }
    }
}
