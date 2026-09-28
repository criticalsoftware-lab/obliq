using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using obliq.Core;
using Rhino.Input;
using Rhino.Input.Custom;

namespace obliq.Commands
{
    /// <summary>
    /// Port of cmdObliq.cpp: creates a floating parallel top-down viewport and
    /// binds the oblique shear conduit to it (plan oblique / military projection).
    /// </summary>
    public class ObliqCommand : Command
    {
        public ObliqCommand()
        {
            Instance = this;
        }

        public static ObliqCommand Instance { get; private set; }

        public override string EnglishName => "Obliq";

        // Last-used settings, remembered for the Rhino session.
        private static ObliqueMode s_mode = ObliqueMode.Plan;
        private static double s_angle = ObliqueProjection.DefaultAngleDeg;
        private static double s_scale = ObliqueProjection.DefaultScale;

        private static readonly string[] ProjectionNames = { "Plan", "Cabinet" };

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null)
                return Result.Failure;

            // Options: Projection=Plan|Cabinet, Angle, Scale
            ObliqueMode obliqueMode = s_mode;
            double angle = s_angle, scale = s_scale;

            GetOption gp = new GetOption();
            gp.SetCommandPrompt("Oblique projection. Press Enter to accept");
            gp.AcceptNothing(true);

            for (;;)
            {
                gp.ClearCommandOptions();
                OptionDouble optAngle = new OptionDouble(angle, 0.0, 360.0);
                OptionDouble optScale = new OptionDouble(scale, 0.001, 100.0);
                int iProj = gp.AddOptionList("Projection", ProjectionNames, (int)obliqueMode);
                gp.AddOptionDouble("Angle", ref optAngle, "Receding angle in degrees");
                gp.AddOptionDouble("Scale", ref optScale, "Receding scale factor");

                GetResult res = gp.Get();
                if (res == GetResult.Cancel)
                    return Result.Cancel;
                if (res != GetResult.Option)
                    break;

                angle = optAngle.CurrentValue;
                scale = optScale.CurrentValue;

                if (gp.OptionIndex() == iProj)
                {
                    // Switching projection resets angle/scale to that projection's defaults.
                    obliqueMode = (ObliqueMode)gp.Option().CurrentListOptionIndex;
                    bool cab = obliqueMode == ObliqueMode.Cabinet;
                    angle = cab ? ObliqueProjection.CabinetAngleDeg : ObliqueProjection.DefaultAngleDeg;
                    scale = cab ? ObliqueProjection.CabinetScale : ObliqueProjection.DefaultScale;
                }
            }

            s_mode = obliqueMode; s_angle = angle; s_scale = scale;
            bool cabinet = obliqueMode == ObliqueMode.Cabinet;
            string title = cabinet ? "Cabinet" : "Oblique";

            // ViewTable.Add returns the new view directly, which replaces the
            // C++ "diff the viewport ID list" lookup.
            // RhinoView view = doc.Views.Add("Oblique", DefinedViewportProjection.Top,
            //     new Rectangle(100, 100, 800, 600), true);

            RhinoView view = doc.Views.Add(title,
                cabinet ? DefinedViewportProjection.Front : DefinedViewportProjection.Top,
                new Rectangle(100, 100, 800, 600), true);

            if (view == null)
            {
                RhinoApp.WriteLine("Obliq: Failed to create viewport.");
                return Result.Failure;
            }

            RhinoViewport vp = view.ActiveViewport;

            ViewportInfo vi = new ViewportInfo(vp);
            vi.ChangeToParallelProjection(true);
            // vi.SetCameraLocation(new Point3d(0.0, 0.0, 100.0));
            // vi.SetCameraDirection(new Vector3d(0.0, 0.0, -1.0));
            // vi.SetCameraUp(new Vector3d(0.0, 1.0, 0.0));
            if (cabinet)
            {
                // Front elevation: looking +Y, Z up. Depth (Y) is what gets sheared.
                vi.SetCameraLocation(new Point3d(0.0, -100.0, 0.0));
                vi.SetCameraDirection(new Vector3d(0.0, 1.0, 0.0));
                vi.SetCameraUp(new Vector3d(0.0, 0.0, 1.0));
            }
            else
            {
                vi.SetCameraLocation(new Point3d(0.0, 0.0, 100.0));
                vi.SetCameraDirection(new Vector3d(0.0, 0.0, -1.0));
                vi.SetCameraUp(new Vector3d(0.0, 1.0, 0.0));
            }

            const double hw = 30.0, hh = 30.0;
            vi.SetFrustum(-hw, hw, -hh, hh, 0.1, 1000.0);
            vi.TargetPoint = Point3d.Origin;

            vp.SetViewProjection(vi, true);
            vp.Name = title;

            ObliqueConduit conduit = ObliqueConduit.Instance;
            //conduit.SetObliqueParams(ObliqueProjection.DefaultAngleDeg, ObliqueProjection.DefaultScale);
            conduit.SetObliqueParams(obliqueMode, angle, scale);
            conduit.ResetView();
            conduit.ViewportId = vp.Id; // track which viewport
            conduit.Enabled = true;

            view.Redraw();
            return Result.Success;
        }
    }
}
