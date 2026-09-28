using System;
using Rhino;
using Rhino.Geometry;

namespace obliq.Core
{
    /// <summary>
    /// Port of ObliqueMake2D.h: builds the oblique shear used by the display
    /// conduit and the Make2D commands, plus the flatten-to-Z=0 transform.
    /// </summary>
    /// <summary>Plan = military / plan oblique (top view). Cabinet = front elevation with receding depth.</summary>
    public enum ObliqueMode
    {
        Plan = 0,
        Cabinet = 1
    }
    public static class ObliqueProjection
    {
        /// <summary>Default shear angle used by the Obliq viewport and ObliqueMake2D.</summary>
        public const double DefaultAngleDeg = 90.0;

        /// <summary>Default shear scale used by the Obliq viewport and ObliqueMake2D.</summary>
        public const double DefaultScale = 1.0;

        /// <summary>Cabinet defaults: depth recedes at 45 degrees, at half scale.</summary>
        public const double CabinetAngleDeg = 45.0;
        public const double CabinetScale = 0.5;

        /// <summary>Shear for the given mode. Plan shears by Z (top view); Cabinet shears by Y (front view).</summary>
        public static Transform Shear(ObliqueMode mode, double angleDeg, double scale)
        {
            if (mode == ObliqueMode.Plan)
                return Shear(angleDeg, scale);

            // Front camera (+Y): X/Z elevation is untouched, depth (Y) recedes
            //   new_x = x + scale*cos(angle)*y
            //   new_z = z + scale*sin(angle)*y
            double alpha = RhinoMath.ToRadians(angleDeg);
            Transform xf = Transform.Identity;
            xf.M01 = scale * Math.Cos(alpha);
            xf.M21 = scale * Math.Sin(alpha);
            return xf;
        }

        /// <summary>
        /// Shear for a top-down (-Z) camera:
        ///   new_x = x + scale*cos(angle)*z
        ///   new_y = y + scale*sin(angle)*z
        /// </summary>
        public static Transform Shear(double angleDeg, double scale)
        {
            double alpha = RhinoMath.ToRadians(angleDeg);
            Transform xf = Transform.Identity;
            xf.M02 = scale * Math.Cos(alpha);
            xf.M12 = scale * Math.Sin(alpha);
            return xf;
        }

        /// <summary>Zeroes the Z row so everything lands on Z=0.</summary>
        public static Transform Flatten()
        {
            Transform xf = Transform.Identity;
            xf.M20 = 0.0;
            xf.M21 = 0.0;
            xf.M22 = 0.0;
            xf.M23 = 0.0;
            return xf;
        }

        /// <summary>
        /// Maps sheared world coordinates onto the XY drawing plane.
        /// Plan: drop Z. Cabinet: the XZ elevation plane becomes XY (x, z) -> (x, y).
        /// </summary>
        public static Transform ToDrawingPlane(ObliqueMode mode)
        {
            if (mode == ObliqueMode.Plan)
                return Flatten();

            Transform xf = Transform.Identity;
            xf.M11 = 0.0; xf.M12 = 1.0;   // y' = z
            xf.M22 = 0.0;                 // z' = 0
            return xf;
        }
        
        /// <summary>Combined = flatten * shear (shear first, then squash Z).</summary>
        public static Transform Projection(double angleDeg, double scale)
        {
            return Flatten() * Shear(angleDeg, scale);
        }

        /// <summary>Duplicates and projects a curve. Returns null on failure.</summary>
        public static Curve ProjectCurve(Curve crv, Transform projection)
        {
            if (crv == null)
                return null;

            Curve dup = crv.DuplicateCurve();
            if (dup == null)
                return null;

            if (!dup.Transform(projection))
            {
                dup.Dispose();
                return null;
            }
            return dup;
        }
    }
}
