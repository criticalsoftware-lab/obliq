using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace obliq.Core
{
    // Port of ObliqueHiddenLine.h
    // Custom hidden-line engine for oblique projections.
    // Brute-force approach: no spatial acceleration, just direct
    // triangle-by-triangle occlusion testing.

    public enum HiddenLineVisibility
    {
        Visible,
        Hidden
    }

    public sealed class ClassifiedSegment
    {
        public Curve Curve;
        public HiddenLineVisibility Visibility = HiddenLineVisibility.Visible;
        public Guid SourceObject = Guid.Empty;
    }

    /// <summary>Projected triangle: 2D footprint + depth (Z in sheared space) for occlusion.</summary>
    internal struct ProjectedTriangle
    {
        public double X0, Y0, X1, Y1, X2, Y2;
        public double D0, D1, D2;
        public double MinX, MinY, MaxX, MaxY; // padded 2D bounds, early reject only
        public int SourceIndex;

        /// <summary>Barycentric point-in-triangle + depth interpolation.</summary>
        public bool TestPoint(double px, double py, out double depth)
        {
            depth = 0.0;

            double denom = (Y1 - Y2) * (X0 - X2) + (X2 - X1) * (Y0 - Y2);
            if (Math.Abs(denom) < 1e-12)
                return false;

            double u = ((Y1 - Y2) * (px - X2) + (X2 - X1) * (py - Y2)) / denom;
            double v = ((Y2 - Y0) * (px - X2) + (X0 - X2) * (py - Y2)) / denom;
            double w = 1.0 - u - v;

            if (u < -1e-9 || v < -1e-9 || w < -1e-9)
                return false;

            depth = u * D0 + v * D1 + w * D2;
            return true;
        }
    }

    public sealed class ObliqueHiddenLineEngine
    {
        // -- Config --

        private Transform _xform = Transform.Identity;
        private double _depthTol = 0.01;
        private int _sampleCount = 64;
        private double _edgeAngleRad = RhinoMath.ToRadians(20.0);

        public void SetTransform(Transform xf) { _xform = xf; }
        public void SetDepthTolerance(double tol) { _depthTol = tol; }
        public void SetSampleDensity(int n) { _sampleCount = n < 8 ? 8 : n; }
        public void SetEdgeAngleThreshold(double deg) { _edgeAngleRad = RhinoMath.ToRadians(deg); }

        // -- Data --

        private readonly List<Brep> _breps = new List<Brep>();
        private readonly List<int> _brepOwner = new List<int>();   // index into _objectIds
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<int> _meshOwner = new List<int>();   // index into _objectIds
        private readonly List<Guid> _objectIds = new List<Guid>();

        private ProjectedTriangle[] _tris = new ProjectedTriangle[0];
        private readonly List<ClassifiedSegment> _results = new List<ClassifiedSegment>();

        // -- Input --

        public void AddObjectsFromDoc(RhinoDoc doc)
        {
            if (doc == null) return;

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
                GeometryBase geo = obj.Geometry;
                if (geo == null) continue;

                int owner = _objectIds.Count;
                _objectIds.Add(obj.Id);

                // -- Brep for edge extraction --
                Extrusion extr = geo as Extrusion;
                Brep brep = geo as Brep;

                if (extr != null)
                {
                    Brep eb = extr.ToBrep(false);
                    if (eb != null) { _breps.Add(eb); _brepOwner.Add(owner); }
                }
                else if (brep != null)
                {
                    _breps.Add(brep);
                    _brepOwner.Add(owner);
                }

                // -- Render mesh for occlusion --
                Mesh asMesh = geo as Mesh;
                if (asMesh != null)
                {
                    // Mesh objects are their own occluders.
                    _meshes.Add(asMesh.DuplicateMesh());
                    _meshOwner.Add(owner);
                    continue;
                }

                // Force creation if not present
                obj.CreateMeshes(MeshType.Render, MeshingParameters.Default, false);
                Mesh[] meshList = obj.GetMeshes(MeshType.Render);
                if (meshList == null) continue;

                foreach (Mesh m in meshList)
                {
                    if (m == null) continue;
                    // Own copies: face normals get computed on them below.
                    _meshes.Add(m.DuplicateMesh());
                    _meshOwner.Add(owner);
                }
            }

            RhinoApp.WriteLine("HLD: {0} breps, {1} meshes collected.", _breps.Count, _meshes.Count);
        }

        // -- Compute --

        public bool Compute()
        {
            ClearResults();

            // Phase 1: Extract and transform edges
            List<Curve> edges = new List<Curve>();
            List<int> edgeOwner = new List<int>();
            ExtractEdges(edges, edgeOwner);

            RhinoApp.WriteLine("HLD: {0} edges extracted.", edges.Count);

            // Phase 2: Project mesh triangles
            ProjectTriangles();

            RhinoApp.WriteLine("HLD: {0} triangles projected.", _tris.Length);

            // Phase 3: Classify each edge. Edges are independent, so they run in
            // parallel; per-edge result lists keep the output order deterministic.
            List<ClassifiedSegment>[] perEdge = new List<ClassifiedSegment>[edges.Count];

            Parallel.For(0, edges.Count, i =>
            {
                Curve edge = edges[i];
                if (edge == null) return;

                Guid ownerId = Guid.Empty;
                int oi = edgeOwner[i];
                if (oi >= 0 && oi < _objectIds.Count)
                    ownerId = _objectIds[oi];

                perEdge[i] = ClassifyEdge(edge, ownerId);
                edge.Dispose();
            });

            foreach (List<ClassifiedSegment> list in perEdge)
                if (list != null) _results.AddRange(list);

            RhinoApp.WriteLine("HLD: {0} segments classified.", _results.Count);
            return true;
        }

        // -- Output --

        public int ResultCount => _results.Count;
        public ClassifiedSegment Result(int i) { return _results[i]; }

        /// <summary>Hands the results to the caller, who then owns the curves.</summary>
        public List<ClassifiedSegment> DetachResults()
        {
            List<ClassifiedSegment> outList = new List<ClassifiedSegment>(_results);
            _results.Clear();
            return outList;
        }

        // -- Lifecycle --

        public void Cleanup()
        {
            _breps.Clear();
            _brepOwner.Clear();
            foreach (Mesh m in _meshes) m.Dispose();
            _meshes.Clear();
            _meshOwner.Clear();
            _objectIds.Clear();
            _tris = new ProjectedTriangle[0];
            ClearResults();
        }

        private void ClearResults()
        {
            foreach (ClassifiedSegment r in _results)
                if (r.Curve != null) r.Curve.Dispose();
            _results.Clear();
        }

        // ================================================================
        // Phase 1: Edge extraction
        // ================================================================

        private void ExtractEdges(List<Curve> edgesOut, List<int> ownerOut)
        {
            // Precompute inverse-transpose for normal transformation
            Transform invT;
            if (!_xform.TryGetInverse(out invT))
                invT = Transform.Identity;
            invT = invT.Transpose();
            Vector3d viewDir = new Vector3d(0.0, 0.0, -1.0);

            // A. Brep sharp/boundary edges
            for (int bi = 0; bi < _breps.Count; bi++)
            {
                Brep brep = _breps[bi];
                if (brep == null) continue;

                foreach (BrepEdge edge in brep.Edges)
                {
                    if (edge.TrimCount < 2 || IsSharpEdge(brep, edge))
                    {
                        Curve dup = edge.DuplicateCurve();
                        if (dup != null)
                        {
                            dup.Transform(_xform);
                            edgesOut.Add(dup);
                            ownerOut.Add(_brepOwner[bi]);
                        }
                    }
                }
            }

            // B. Mesh silhouettes (catches curved surfaces like spheres)
            for (int mi = 0; mi < _meshes.Count; mi++)
            {
                Mesh mesh = _meshes[mi];
                if (mesh == null || mesh.Faces.Count == 0) continue;

                mesh.FaceNormals.ComputeFaceNormals();
                if (mesh.FaceNormals.Count != mesh.Faces.Count) continue;

                Rhino.Geometry.Collections.MeshTopologyEdgeList topo = mesh.TopologyEdges;
                Rhino.Geometry.Collections.MeshTopologyVertexList topoV = mesh.TopologyVertices;

                for (int ei = 0; ei < topo.Count; ei++)
                {
                    int[] faces = topo.GetConnectedFaces(ei);
                    bool isSil = false;

                    if (faces == null || faces.Length < 2)
                    {
                        isSil = true;
                    }
                    else
                    {
                        Vector3d n0 = new Vector3d(mesh.FaceNormals[faces[0]]);
                        Vector3d n1 = new Vector3d(mesh.FaceNormals[faces[1]]);
                        n0.Transform(invT);
                        n1.Transform(invT);

                        double d0 = n0 * viewDir;
                        double d1 = n1 * viewDir;

                        if ((d0 > 0.0 && d1 < 0.0) || (d0 < 0.0 && d1 > 0.0))
                            isSil = true;
                    }

                    if (isSil)
                    {
                        IndexPair tv = topo.GetTopologyVertices(ei);
                        Point3d p0 = new Point3d(topoV[tv.I]);
                        Point3d p1 = new Point3d(topoV[tv.J]);
                        p0.Transform(_xform);
                        p1.Transform(_xform);

                        edgesOut.Add(new LineCurve(p0, p1));
                        ownerOut.Add(_meshOwner[mi]);
                    }
                }
            }
        }

        private bool IsSharpEdge(Brep brep, BrepEdge edge)
        {
            if (edge.TrimCount < 2) return true;

            int[] ti = edge.TrimIndices();
            if (ti == null || ti.Length < 2) return true;

            BrepFace face0 = brep.Trims[ti[0]].Face;
            BrepFace face1 = brep.Trims[ti[1]].Face;
            if (face0 == null || face1 == null) return true;

            double tMid = edge.Domain.ParameterAt(0.5);
            Point3d ptMid = edge.PointAt(tMid);

            double u0, v0, u1, v1;
            if (!face0.ClosestPoint(ptMid, out u0, out v0) ||
                !face1.ClosestPoint(ptMid, out u1, out v1))
                return true;

            Vector3d n0 = face0.NormalAt(u0, v0);
            Vector3d n1 = face1.NormalAt(u1, v1);
            if (face0.OrientationIsReversed) n0 = -n0;
            if (face1.OrientationIsReversed) n1 = -n1;

            double dot = n0 * n1;
            if (dot < -1.0) dot = -1.0;
            else if (dot > 1.0) dot = 1.0;

            return Math.Acos(dot) > _edgeAngleRad;
        }

        // ================================================================
        // Phase 2: Project all mesh triangles
        // ================================================================

        private void ProjectTriangles()
        {
            List<ProjectedTriangle> tris = new List<ProjectedTriangle>();

            for (int mi = 0; mi < _meshes.Count; mi++)
            {
                Mesh mesh = _meshes[mi];
                if (mesh == null) continue;

                int vc = mesh.Vertices.Count;
                if (vc == 0) continue;

                // Transform all verts once
                Point3d[] xv = new Point3d[vc];
                for (int vi = 0; vi < vc; vi++)
                {
                    Point3d p = new Point3d(mesh.Vertices[vi]);
                    p.Transform(_xform);
                    xv[vi] = p;
                }

                int fc = mesh.Faces.Count;
                for (int fi = 0; fi < fc; fi++)
                {
                    MeshFace f = mesh.Faces[fi];
                    AddTri(tris, xv, f.A, f.B, f.C, mi);
                    if (f.IsQuad)
                        AddTri(tris, xv, f.A, f.C, f.D, mi);
                }
            }

            _tris = tris.ToArray();
        }

        private static void AddTri(List<ProjectedTriangle> tris, Point3d[] xv, int i0, int i1, int i2, int src)
        {
            int vc = xv.Length;
            if (i0 < 0 || i0 >= vc || i1 < 0 || i1 >= vc || i2 < 0 || i2 >= vc)
                return;

            Point3d a = xv[i0], b = xv[i1], c = xv[i2];

            // Skip degenerate
            double area = Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y));
            if (area < 1e-12)
                return;

            ProjectedTriangle tri = new ProjectedTriangle
            {
                X0 = a.X, Y0 = a.Y, D0 = a.Z,
                X1 = b.X, Y1 = b.Y, D1 = b.Z,
                X2 = c.X, Y2 = c.Y, D2 = c.Z,
                SourceIndex = src
            };

            // Bounds padded well beyond the barycentric epsilon so the
            // early reject can never discard a point TestPoint would accept.
            double minX = Math.Min(a.X, Math.Min(b.X, c.X));
            double maxX = Math.Max(a.X, Math.Max(b.X, c.X));
            double minY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            double maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            double pad = 1e-6 * Math.Max(maxX - minX, maxY - minY) + 1e-9;
            tri.MinX = minX - pad; tri.MaxX = maxX + pad;
            tri.MinY = minY - pad; tri.MaxY = maxY + pad;

            tris.Add(tri);
        }

        // ================================================================
        // Phase 3: Classify edges via brute-force sampling
        // ================================================================

        private List<ClassifiedSegment> ClassifyEdge(Curve edge, Guid ownerId)
        {
            List<ClassifiedSegment> outList = new List<ClassifiedSegment>();
            Interval dom = edge.Domain;
            int n = _sampleCount;

            // Sample visibility along the curve
            double[] t = new double[n + 1];
            bool[] vis = new bool[n + 1];
            for (int s = 0; s <= n; s++)
            {
                t[s] = dom.ParameterAt((double)s / n);
                vis[s] = IsPointVisible(edge.PointAt(t[s]));
            }

            // Walk samples, split at transitions
            int segStart = 0;
            double segT0 = t[0];
            for (int s = 1; s <= n; s++)
            {
                if (vis[s] != vis[segStart])
                {
                    double tEnd = RefineTransition(edge, t[s - 1], t[s], vis[segStart]);
                    Emit(outList, edge, segT0, tEnd, vis[segStart], ownerId);
                    segStart = s;
                    segT0 = tEnd;
                }
            }

            // Final run. Also covers a transition inside the last sample
            // interval, whose tail the C++ loop dropped.
            Emit(outList, edge, segT0, t[n], vis[segStart], ownerId);

            return outList;
        }

        private static void Emit(List<ClassifiedSegment> outList, Curve edge, double t0, double t1,
            bool visible, Guid ownerId)
        {
            if (t1 - t0 <= 1e-10)
                return;

            Curve seg = edge.Trim(new Interval(t0, t1));
            if (seg == null)
                return;

            outList.Add(new ClassifiedSegment
            {
                Curve = seg,
                Visibility = visible ? HiddenLineVisibility.Visible : HiddenLineVisibility.Hidden,
                SourceObject = ownerId
            });
        }

        private double RefineTransition(Curve edge, double t0, double t1, bool visAtT0, int iterations = 12)
        {
            for (int i = 0; i < iterations; i++)
            {
                double tm = 0.5 * (t0 + t1);
                bool v = IsPointVisible(edge.PointAt(tm));
                if (v == visAtT0)
                    t0 = tm;
                else
                    t1 = tm;
            }
            return 0.5 * (t0 + t1);
        }

        // Brute-force: test point against every triangle
        private bool IsPointVisible(Point3d pt)
        {
            double px = pt.X, py = pt.Y, limit = pt.Z + _depthTol;
            ProjectedTriangle[] tris = _tris;

            for (int i = 0; i < tris.Length; i++)
            {
                if (px < tris[i].MinX || px > tris[i].MaxX || py < tris[i].MinY || py > tris[i].MaxY)
                    continue;

                double triZ;
                // Higher Z = closer to camera in top-down -Z view
                if (tris[i].TestPoint(px, py, out triZ) && triZ > limit)
                    return false;
            }
            return true;
        }
    }
}
