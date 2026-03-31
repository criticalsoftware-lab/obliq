using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using System;

namespace Obliq
{
    public class ObliqueConduit : DisplayConduit
    {
        private double _angleDeg;
        private double _scale;
        private Transform _shear;
        private Guid _viewportId = Guid.Empty;

        public ObliqueConduit()
        {
            _angleDeg = 45.0;
            _scale = 1.0;
            UpdateShearMatrix();
        }

        public void SetObliqueParams(double angleDeg, double scale)
        {
            _angleDeg = angleDeg;
            _scale = scale;
            UpdateShearMatrix();
        }

        public void SetViewportId(Guid id)
        {
            _viewportId = id;
        }

        public Guid ViewportId => _viewportId;

        private void UpdateShearMatrix()
        {
            double alpha = _angleDeg * Math.PI / 180.0;
            double shx = _scale * Math.Cos(alpha);
            double shy = _scale * Math.Sin(alpha);

            _shear = Transform.Identity;
            _shear[0, 2] = shx;
            _shear[1, 2] = shy;
        }

        private bool IsTargetViewport(DisplayPipeline pipeline)
        {
            if (_viewportId != Guid.Empty && pipeline.Viewport.Id != _viewportId)
                return false;
            return true;
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (!IsTargetViewport(e.Display)) return;

            BoundingBox bbox = e.BoundingBox;
            if (bbox.IsValid)
            {
                BoundingBox shearedBbox = BoundingBox.Empty;
                Point3d[] corners = bbox.GetCorners();
                foreach (Point3d corner in corners)
                {
                    Point3d sheared = corner;
                    sheared.Transform(_shear);
                    if (shearedBbox.IsValid)
                        shearedBbox.Union(sheared);
                    else
                        shearedBbox = new BoundingBox(sheared, sheared);
                }
                e.BoundingBox.Union(shearedBbox);
                e.IncludeBoundingBox(shearedBbox);
            }
            base.CalculateBoundingBox(e);
        }

        protected override void PreDrawObjects(DrawEventArgs e)
        {
            if (!IsTargetViewport(e.Display)) return;
            e.Display.PushModelTransform(_shear);
            base.PreDrawObjects(e);
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (!IsTargetViewport(e.Display)) return;
            e.Display.PopModelTransform();
            base.PostDrawObjects(e);
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!IsTargetViewport(e.Display)) return;
            e.Display.PushModelTransform(_shear);
            base.DrawForeground(e);
            e.Display.PopModelTransform();
        }
        
        protected override void DrawOverlay(DrawEventArgs e)
        {
            if (!IsTargetViewport(e.Display)) return;
            e.Display.PushModelTransform(_shear);
            base.DrawOverlay(e);
            e.Display.PopModelTransform();
        }
    }
}
