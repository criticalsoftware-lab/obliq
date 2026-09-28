using System;
using Rhino.Display;
using Rhino.Geometry;

namespace obliq.Core
{
    /// <summary>
    /// Port of ObliqueConduit.h: a display conduit that pushes the oblique shear
    /// onto the pipeline's model-transform stack, so Rhino draws every object,
    /// selection highlight and overlay (gumball, dynamic draw) sheared.
    /// The geometry itself is never modified.
    /// </summary>
    public sealed class ObliqueConduit : DisplayConduit
    {
        private static ObliqueConduit s_instance;

        /// <summary>Shared instance (the C++ version used a function-static conduit).</summary>
        public static ObliqueConduit Instance
        {
            get
            {
                if (s_instance == null)
                    s_instance = new ObliqueConduit();
                return s_instance;
            }
        }

        private ObliqueMode _mode = ObliqueMode.Plan;
        private double _angleDeg = ObliqueProjection.DefaultAngleDeg;
        private double _scale = ObliqueProjection.DefaultScale;
        private Transform _shear = Transform.Identity;
        private int _pushCount;

        // --- Virtual camera -------------------------------------------------
        // Rhino culls against the REAL camera, which is a plain top/front view.
        // So the real camera is kept "parked" at the extents of the whole model
        // (nothing is ever outside it, so nothing is ever culled) and the user's
        // zoom/pan lives in _view instead, a camera-plane scale + offset applied
        // on top of the shear. What is drawn: _display = _view * _shear.
        private Transform _view = Transform.Identity;
        private Transform _display = Transform.Identity;
        private Rhino.RhinoDoc _doc;
        private bool _parked, _parkPending;
        private BoundingBox _parkBox = BoundingBox.Empty;
        private CameraFrame _parkFrame;

        public ObliqueConduit()
        {
            UpdateShearMatrix();
        }

        /// <summary>Viewport the shear applies to. Guid.Empty applies it to every viewport.</summary>
        public Guid ViewportId { get; set; } = Guid.Empty;

        public double AngleDeg => _angleDeg;
        public double Scale => _scale;

        public ObliqueMode Mode => _mode;

        /// <summary>Forget the virtual zoom/pan (call when (re)binding a viewport).</summary>
        public void ResetView()
        {
            _view = Transform.Identity;
            _parked = false;
            UpdateShearMatrix();
        }

        public void SetObliqueParams(ObliqueMode mode, double angleDeg, double scale)
        {
            _mode = mode;
            SetObliqueParams(angleDeg, scale);
        }

        public void SetObliqueParams(double angleDeg, double scale)
        {
            _angleDeg = angleDeg;
            _scale = scale;
            UpdateShearMatrix();
        }

        // ------------------------------------------------------------------
        // Channels
        // ------------------------------------------------------------------

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (!IsTarget(e.Viewport))
                return;

            // First channel of a new frame. RhinoCommon has no CN_PIPELINECLOSED
            // notifier, so the overlay push left over from the previous frame is
            // unwound here instead of at pipeline close.
            PopAll(e.Display);

            // Any change to the real camera since we parked it is the user
            // navigating (wheel zoom, pan, zoom window...). This frame is still
            // drawn correctly because _display is applied on top of whatever the
            // camera is; on idle the camera is parked again and the navigation
            // is moved into _view. Same if the model grew past the parked box.
            _doc = e.RhinoDoc;
            BoundingBox model = _doc != null ? _doc.Objects.BoundingBoxVisible : BoundingBox.Empty;
            if (!_parked
                || !CameraFrame.Of(e.Viewport).SameAs(_parkFrame)
                || (model.IsValid && !_parkBox.Contains(model)))
                RequestPark();

            // Expand the scene bbox to account for the shear so nothing gets clipped.
            BoundingBox bbox = e.BoundingBox;
            if (bbox.IsValid)
            {
                BoundingBox sheared = bbox;
                sheared.Transform(_shear); // transforms all 8 corners
                e.IncludeBoundingBox(sheared);
            }
        }

        protected override void CalculateBoundingBoxZoomExtents(CalculateBoundingBoxEventArgs e)
        {
            if (!IsTarget(e.Viewport))
                return;

            // Make Zoom Extents frame what is actually drawn (virtual zoom + shear).
            BoundingBox bbox = e.BoundingBox;
            if (bbox.IsValid)
            {
                BoundingBox shown = bbox;
                shown.Transform(_display);
                e.IncludeBoundingBox(shown);
            }
        }

        protected override void PreDrawObjects(DrawEventArgs e)
        {
            if (!IsTarget(e.Viewport))
                return;

            // Safety net in case CalculateBoundingBox was skipped this frame.
            PopAll(e.Display);

            Push(e.Display);
        }

        protected override void PreDrawObject(DrawObjectEventArgs e)
        {
            if (!IsTarget(e.Viewport))
                return;

            // Pipeline may reset model transform between objects.
            // If our shear got popped, re-push it.
            if (_pushCount == 0)
                Push(e.Display);
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (!IsTarget(e.Viewport))
                return;

            // Pop the PreDrawObjects push.
            Pop(e.Display);
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!IsTarget(e.Viewport))
                return;

            // Selected/highlighted wireframes and selection dots.
            Push(e.Display);
        }

        protected override void DrawOverlay(DrawEventArgs e)
        {
            if (!IsTarget(e.Viewport))
                return;

            // Pop the foreground push if it's still on the stack,
            // then push fresh for overlay drawing (dynamic draw, gumballs, etc.).
            Pop(e.Display);
            Push(e.Display);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private bool IsTarget(RhinoViewport vp)
        {
            if (ViewportId == Guid.Empty)
                return true;
            return vp != null && vp.Id == ViewportId;
        }

        private void Push(DisplayPipeline dp)
        {
            dp.PushModelTransform(_display);
            _pushCount++;
        }

        private void Pop(DisplayPipeline dp)
        {
            if (_pushCount <= 0)
                return;

            // If the pipeline already reset its stack, there is nothing of ours
            // left to pop; just drop the count so the stack never underflows.
            if (!dp.ModelTransformIsIdentity)
                dp.PopModelTransform();
            _pushCount--;
        }

        private void PopAll(DisplayPipeline dp)
        {
            while (_pushCount > 0)
                Pop(dp);
        }

        private void UpdateShearMatrix()
        {
            _shear = ObliqueProjection.Shear(_mode, _angleDeg, _scale);
            _display = _view * _shear;
        }

        // ------------------------------------------------------------------
        // Parking the real camera
        // ------------------------------------------------------------------

        private void RequestPark()
        {
            if (_parkPending)
                return;
            _parkPending = true;
            Rhino.RhinoApp.Idle += OnIdle;
        }

        private void OnIdle(object sender, EventArgs e)
        {
            Rhino.RhinoApp.Idle -= OnIdle;
            _parkPending = false;
            if (!Enabled || _doc == null || ViewportId == Guid.Empty)
                return;

            RhinoView view = _doc.Views.Find(ViewportId);
            if (view != null)
                Park(view);
        }

        /// <summary>
        /// Moves the real camera to the extents of the whole model (so Rhino
        /// culls nothing) and folds the camera change into _view, so the image
        /// on screen does not move.
        /// </summary>
        private void Park(RhinoView view)
        {
            RhinoViewport vp = view.ActiveViewport;
            BoundingBox box = _doc.Objects.BoundingBoxVisible;
            if (vp == null || !box.IsValid)
                return;

            BoundingBox sheared = box;
            sheared.Transform(_shear);
            box.Union(sheared);
            box.Inflate(0.25 * box.Diagonal.Length + 1.0);

            CameraFrame before = CameraFrame.Of(vp);
            vp.ZoomBoundingBox(box);
            CameraFrame after = CameraFrame.Of(vp);

            _view = CameraFrame.Map(after, before) * _view;
            _parkBox = box;
            _parkFrame = after;
            _parked = true;
            UpdateShearMatrix();

            // Rhino draws the grid and axes before our transform, at the parked
            // scale, so they would not line up with the virtual zoom.
            vp.ConstructionGridVisible = false;
            vp.ConstructionAxesVisible = false;
            vp.WorldAxesVisible = false;

            view.Redraw();
        }

        /// <summary>A parallel camera: location, image-plane axes and frustum rectangle.</summary>
        private struct CameraFrame
        {
            public Point3d O;
            public Vector3d X, Y;
            public double L, R, B, T;

            public static CameraFrame Of(RhinoViewport vp)
            {
                CameraFrame f = new CameraFrame { O = vp.CameraLocation, X = vp.CameraX, Y = vp.CameraY };
                double n, fa;
                vp.GetFrustum(out f.L, out f.R, out f.B, out f.T, out n, out fa);
                return f;
            }

            public bool SameAs(CameraFrame o)
            {
                double tol = 1e-9 * (Math.Abs(R - L) + Math.Abs(T - B) + 1.0);
                return O.DistanceTo(o.O) <= tol
                    && Math.Abs(L - o.L) <= tol && Math.Abs(R - o.R) <= tol
                    && Math.Abs(B - o.B) <= tol && Math.Abs(T - o.T) <= tol
                    && (X - o.X).Length <= 1e-9 && (Y - o.Y).Length <= 1e-9;
            }

            /// <summary>
            /// World transform A with: a-screen(A p) == b-screen(p) for every p,
            /// and depth along the view direction unchanged. Assumes both frames
            /// share the same orientation (parking only zooms/pans).
            /// </summary>
            public static Transform Map(CameraFrame a, CameraFrame b)
            {
                double kx = (a.R - a.L) / (b.R - b.L);
                double ky = (a.T - a.B) / (b.T - b.B);
                Vector3d dO = a.O - b.O;
                double cx = a.L + kx * (dO * a.X - b.L);
                double cy = a.B + ky * (dO * a.Y - b.B);

                Transform d = Transform.Identity;
                d.M00 = kx; d.M03 = cx;
                d.M11 = ky; d.M13 = cy;

                Plane cam = new Plane(a.O, a.X, a.Y);
                return Transform.PlaneToPlane(Plane.WorldXY, cam) * d * Transform.PlaneToPlane(cam, Plane.WorldXY);
            }
        }
    }
}
