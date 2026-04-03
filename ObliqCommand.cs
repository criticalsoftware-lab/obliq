using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;

namespace Obliq
{
    public class ObliqCommand : Command
    {
        public ObliqCommand()
        {
            Instance = this;
        }

        public static ObliqCommand Instance { get; private set; }

        public override string EnglishName => "Obliq";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null)
                return Result.Failure;

            RhinoApp.WriteLine("ObliqCommand: Starting execution...");

            RhinoView newView = doc.Views.ActiveView;
            if (newView == null)
            {
                RhinoApp.WriteLine("ObliqCommand: No active view found!");
                return Result.Failure;
            }

            // If the conduit is already running in this viewport, toggle it OFF
            if (_conduit != null && _conduit.Enabled && _conduit.ViewportId == newView.ActiveViewportID)
            {
                RhinoApp.WriteLine($"ObliqCommand: Disabling oblique mode in '{newView.ActiveViewport.Name}'...");
                _conduit.Enabled = false;
                newView.Redraw();
                return Result.Success;
            }

            // Otherwise, set it up and toggle it ON
            RhinoApp.WriteLine($"ObliqCommand: Enabling oblique view on '{newView.ActiveViewport.Name}'...");

            var onvp = newView.ActiveViewport;
            onvp.ChangeToParallelProjection(true);

                onvp.SetCameraLocation(new Point3d(0.0, 0.0, 100.0), true);
                onvp.SetCameraDirection(new Vector3d(0.0, 0.0, -1.0), true);
                onvp.CameraUp = new Vector3d(0.0, 1.0, 0.0);
                onvp.SetCameraTarget(new Point3d(0.0, 0.0, 0.0), true);

                newView.ActiveViewport.Name = "Oblique";
                
                if (_conduit == null)
                {
                    RhinoApp.WriteLine("ObliqCommand: Creating new DisplayConduit...");
                    _conduit = new ObliqueConduit();
                }
                
                RhinoApp.WriteLine("ObliqCommand: Applying shear transform conduit...");
                _conduit.SetObliqueParams(90.0, 1.0);
                _conduit.SetViewportId(newView.ActiveViewportID);
                _conduit.Enabled = true;

                newView.Redraw();
                RhinoApp.WriteLine("ObliqCommand: Finished.");
                return Result.Success;
        }

        private static ObliqueConduit _conduit;
    }
}
