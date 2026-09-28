using System.Drawing;
using System.Globalization;
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
    /// Port of cmdObliqueCurveMake2D.cpp: projects selected curves through the
    /// oblique shear and flattens them to Z=0, adding the results as new geometry.
    /// </summary>
    public class ObliqueCurveMake2DCommand : Command
    {
        public ObliqueCurveMake2DCommand()
        {
            Instance = this;
        }

        public static ObliqueCurveMake2DCommand Instance { get; private set; }

        public override string EnglishName => "ObliqueCurveMake2D";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null)
                return Result.Failure;

            // 1. Select curves
            GetObject go = new GetObject();
            go.SetCommandPrompt("Select curves to project");
            go.GeometryFilter = ObjectType.Curve;
            go.SubObjectSelect = false;
            go.GetMultiple(1, 0);

            if (go.CommandResult() != Result.Success)
                return go.CommandResult();

            int count = go.ObjectCount;
            if (count == 0)
            {
                RhinoApp.WriteLine("ObliqueCurveMake2D: No curves selected.");
                return Result.Nothing;
            }

            // 2. Options. Defaults match the conduit: 90 deg angle, 1.0 scale
            OptionDouble optAngle = new OptionDouble(ObliqueProjection.DefaultAngleDeg, 0.0, 360.0);
            OptionDouble optScale = new OptionDouble(ObliqueProjection.DefaultScale, 0.001, 100.0);

            GetOption getOpt = new GetOption();
            getOpt.SetCommandPrompt("Oblique projection options. Press Enter to accept");
            getOpt.AcceptNothing(true);
            getOpt.AddOptionDouble("Angle", ref optAngle, "Shear angle in degrees");
            getOpt.AddOptionDouble("Scale", ref optScale, "Shear scale factor");

            for (;;)
            {
                GetResult res = getOpt.Get();
                if (res == GetResult.Option)
                    continue;
                if (res == GetResult.Cancel)
                    return Result.Cancel;
                break; // Nothing (Enter) or anything else accepts
            }

            double angleDeg = optAngle.CurrentValue;
            double scale = optScale.CurrentValue;

            // 3. Build the projection
            Transform projection = ObliqueProjection.Projection(angleDeg, scale);

            // 4. Create a layer for the output
            string layerName = string.Format(CultureInfo.InvariantCulture,
                "ObliqueCurveMake2D{0:F0}_{1:F2}", angleDeg, scale);

            int layerIndex = doc.Layers.FindByFullPath(layerName, -1);
            if (layerIndex < 0)
                layerIndex = doc.Layers.Add(new Layer { Name = layerName, Color = Color.Black });

            // 5. Project each selected curve
            int added = 0;
            for (int i = 0; i < count; i++)
            {
                Curve crv = go.Object(i).Curve();
                if (crv == null)
                    continue;

                Curve projected = ObliqueProjection.ProjectCurve(crv, projection);
                if (projected == null)
                    continue;

                ObjectAttributes attrs = new ObjectAttributes();
                if (layerIndex >= 0)
                    attrs.LayerIndex = layerIndex;

                // AddCurve copies the curve, so we dispose our copy after
                if (doc.Objects.AddCurve(projected, attrs) != System.Guid.Empty)
                    added++;
                projected.Dispose();
            }

            doc.Views.Redraw();

            RhinoApp.WriteLine("ObliqueCurveMake2D: Projected {0} curve(s) to layer \"{1}\"", added, layerName);
            return added > 0 ? Result.Success : Result.Nothing;
        }
    }
}
