using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using System;
using System.Collections.Generic;

namespace Obliq
{
    public enum HiddenLineVisibility
    {
        Visible,
        Hidden
    }

    public class ClassifiedSegment
    {
        public Curve Curve { get; set; }
        public HiddenLineVisibility Visibility { get; set; }
        public Guid SourceObject { get; set; }
    }

    public struct ProjectedTriangle
    {
        public Point2d[] V2d { get; set; }
        public double[] Depth { get; set; }
        public int SourceIdx { get; set; }

        public bool TestPoint(Point2d p, out double depthOut)
        {
            depthOut = 0;
            double x0 = V2d[0].X, y0 = V2d[0].Y;
            double x1 = V2d[1].X, y1 = V2d[1].Y;
            double x2 = V2d[2].X, y2 = V2d[2].Y;

            double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (Math.Abs(denom) < 1e-12) return false;

            double u = ((y1 - y2) * (p.X - x2) + (x2 - x1) * (p.Y - y2)) / denom;
            double v = ((y2 - y0) * (p.X - x2) + (x0 - x2) * (p.Y - y2)) / denom;
            double w = 1.0 - u - v;

            if (u < -1e-9 || v < -1e-9 || w < -1e-9) return false;

            depthOut = u * Depth[0] + v * Depth[1] + w * Depth[2];
            return true;
        }
    }

    public class ObliqueHiddenLineEngine
    {
        private Transform _xform = Transform.Identity;
        private double _depthTol = 0.01;
        private int _sampleCount = 64;
        private double _edgeAngleRad = 20.0 * Math.PI / 180.0;

        private List<Brep> _breps = new List<Brep>();
        private List<Mesh> _meshes = new List<Mesh>();
        private List<Guid> _objectIds = new List<Guid>();

        private List<ProjectedTriangle> _tris = new List<ProjectedTriangle>();
        private List<ClassifiedSegment> _results = new List<ClassifiedSegment>();

        public void SetTransform(Transform xf) { _xform = xf; }
        public void SetDepthTolerance(double tol) { _depthTol = tol; }
        public void SetSampleDensity(int n) { _sampleCount = n < 8 ? 8 : n; }
        public void SetEdgeAngleThreshold(double deg) { _edgeAngleRad = deg * Math.PI / 180.0; }

        public int ResultCount => _results.Count;
        public List<ClassifiedSegment> DetachResults()
        {
            var r = new List<ClassifiedSegment>(_results);
            _results.Clear();
            return r;
        }

        public void AddObjectsFromDoc(RhinoDoc doc)
        {
            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = false,
                IncludeLights = false,
                IncludeGrips = false,
                DeletedObjects = false
            };

            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                var geo = obj.Geometry;
                if (geo == null) continue;

                if (geo is Extrusion ext)
                {
                    var brep = ext.ToBrep(false);
                    if (brep != null) _breps.Add(brep);
                }
                else if (geo is Brep b)
                {
                    _breps.Add(b);
                }

                var meshes = obj.GetMeshes(MeshType.Render);
                if (meshes.Length > 0)
                {
                    foreach (var m in meshes) _meshes.Add(m);
                }
                else
                {
                    var meshArgs = MeshingParameters.Default;
                    var created = Mesh.CreateFromBrep(geo as Brep, meshArgs);
                    if (created != null)
                    {
                        foreach (var m in created) _meshes.Add(m);
                    }
                }

                _objectIds.Add(obj.Id);
            }
            RhinoApp.WriteLine($"HLD: {_breps.Count} breps, {_meshes.Count} meshes collected.");
        }

        public bool Compute()
        {
            _results.Clear();

            List<Curve> edges = new List<Curve>();
            List<int> edgeOwner = new List<int>();
            ExtractEdges(edges, edgeOwner);

            RhinoApp.WriteLine($"HLD: {edges.Count} edges extracted.");

            ProjectTriangles();

            RhinoApp.WriteLine($"HLD: {_tris.Count} triangles projected.");

            for (int i = 0; i < edges.Count; i++)
            {
                Curve edge = edges[i];
                if (edge == null) continue;

                Guid owner = Guid.Empty;
                if (edgeOwner[i] >= 0 && edgeOwner[i] < _objectIds.Count)
                    owner = _objectIds[edgeOwner[i]];

                ClassifyEdge(edge, owner);
            }

            RhinoApp.WriteLine($"HLD: {_results.Count} segments classified.");
            return true;
        }

        private void ExtractEdges(List<Curve> edgesOut, List<int> ownerOut)
        {
            Transform invT;
            if (!_xform.TryGetInverse(out invT)) invT = _xform;
            invT = invT.Transpose();
            Vector3d viewDir = new Vector3d(0.0, 0.0, -1.0);

            for (int bi = 0; bi < _breps.Count; bi++)
            {
                Brep brep = _breps[bi];
                if (brep == null) continue;

                for (int ei = 0; ei < brep.Edges.Count; ei++)
                {
                    var edge = brep.Edges[ei];
                    if (edge.TrimIndices().Length < 2 || IsSharpEdge(brep, edge))
                    {
                        Curve dup = edge.DuplicateCurve();
                        if (dup != null)
                        {
                            dup.Transform(_xform);
                            edgesOut.Add(dup);
                            ownerOut.Add(bi);
                        }
                    }
                }
            }

            for (int mi = 0; mi < _meshes.Count; mi++)
            {
                Mesh mesh = _meshes[mi];
                if (mesh == null || mesh.Faces.Count == 0) continue;

                mesh.FaceNormals.ComputeFaceNormals();
                if (mesh.FaceNormals.Count != mesh.Faces.Count) continue;

                var topo = mesh.TopologyEdges;
                for (int ei = 0; ei < topo.Count; ei++)
                {
                    int[] faces = topo.GetConnectedFaces(ei);
                    bool isSil = false;

                    if (faces.Length < 2)
                    {
                        isSil = true;
                    }
                    else if (faces.Length >= 2)
                    {
                        Vector3d n0 = mesh.FaceNormals[faces[0]];
                        Vector3d n1 = mesh.FaceNormals[faces[1]];
                        n0.Transform(invT);
                        n1.Transform(invT);

                        double d0 = n0 * viewDir;
                        double d1 = n1 * viewDir;

                        if ((d0 > 0.0 && d1 < 0.0) || (d0 < 0.0 && d1 > 0.0))
                            isSil = true;
                    }

                    if (isSil)
                    {
                        IndexPair vIndices = topo.GetTopologyVertices(ei);
                        Point3d p0 = mesh.TopologyVertices[vIndices.I];
                        Point3d p1 = mesh.TopologyVertices[vIndices.J];

                        p0.Transform(_xform);
                        p1.Transform(_xform);

                        LineCurve lc = new LineCurve(p0, p1);
                        edgesOut.Add(lc);
                        ownerOut.Add(mi);
                    }
                }
            }
        }

        private bool IsSharpEdge(Brep brep, BrepEdge edge)
        {
            int[] ti = edge.TrimIndices();
            if (ti.Length < 2) return true;

            var trim0 = brep.Trims[ti[0]];
            var trim1 = brep.Trims[ti[1]];
            
            if (trim0 == null || trim1 == null) return true;
            
            var face0 = trim0.Face;
            var face1 = trim1.Face;

            if (face0 == null || face1 == null) return true;

            double tMid = edge.Domain.ParameterAt(0.5);
            Point3d ptMid = edge.PointAt(tMid);

            if (!face0.ClosestPoint(ptMid, out double u0, out double v0) ||
                !face1.ClosestPoint(ptMid, out double u1, out double v1))
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

        private void ProjectTriangles()
        {
            _tris.Clear();

            for (int mi = 0; mi < _meshes.Count; mi++)
            {
                Mesh mesh = _meshes[mi];
                if (mesh == null) continue;

                int vc = mesh.Vertices.Count;
                if (vc == 0) continue;

                Point3d[] xv = new Point3d[vc];
                for (int vi = 0; vi < vc; vi++)
                {
                    Point3d p = mesh.Vertices[vi];
                    p.Transform(_xform);
                    xv[vi] = p;
                }

                foreach (var f in mesh.Faces)
                {
                    AddTri(xv, f.A, f.B, f.C, mi);
                    if (f.IsQuad)
                        AddTri(xv, f.A, f.C, f.D, mi);
                }
            }
        }

        private void AddTri(Point3d[] xv, int i0, int i1, int i2, int src)
        {
            double area = Math.Abs(
                (xv[i1].X - xv[i0].X) * (xv[i2].Y - xv[i0].Y) -
                (xv[i2].X - xv[i0].X) * (xv[i1].Y - xv[i0].Y));
            if (area < 1e-12) return;

            ProjectedTriangle tri = new ProjectedTriangle
            {
                V2d = new Point2d[] { new Point2d(xv[i0].X, xv[i0].Y), new Point2d(xv[i1].X, xv[i1].Y), new Point2d(xv[i2].X, xv[i2].Y) },
                Depth = new double[] { xv[i0].Z, xv[i1].Z, xv[i2].Z },
                SourceIdx = src
            };
            _tris.Add(tri);
        }

        private void ClassifyEdge(Curve edge, Guid ownerId)
        {
            Interval dom = edge.Domain;
            List<double> paramsList = new List<double>();
            List<bool> visList = new List<bool>();

            for (int s = 0; s <= _sampleCount; s++)
            {
                double t = dom.ParameterAt((double)s / _sampleCount);
                Point3d pt = edge.PointAt(t);
                paramsList.Add(t);
                visList.Add(IsPointVisible(pt));
            }

            int segStart = 0;
            for (int s = 1; s <= _sampleCount; s++)
            {
                bool changed = (visList[s] != visList[segStart]);
                bool last = (s == _sampleCount);

                if (changed || last)
                {
                    double tEnd;
                    if (changed)
                        tEnd = RefineTransition(edge, paramsList[s - 1], paramsList[s], visList[segStart]);
                    else
                        tEnd = paramsList[s];

                    Interval sub = new Interval(paramsList[segStart], tEnd);
                    if (sub.Length > 1e-10)
                    {
                        Curve seg = edge.DuplicateCurve();
                        if (seg != null)
                        {
                            seg = seg.Trim(sub);
                            if (seg != null)
                            {
                                _results.Add(new ClassifiedSegment
                                {
                                    Curve = seg,
                                    Visibility = visList[segStart] ? HiddenLineVisibility.Visible : HiddenLineVisibility.Hidden,
                                    SourceObject = ownerId
                                });
                            }
                        }
                    }

                    if (changed)
                    {
                        paramsList[s] = tEnd;
                        segStart = s;
                    }
                }
            }
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

        private bool IsPointVisible(Point3d pt)
        {
            Point2d p2d = new Point2d(pt.X, pt.Y);
            for (int i = 0; i < _tris.Count; i++)
            {
                if (_tris[i].TestPoint(p2d, out double triZ))
                {
                    if (triZ > pt.Z + _depthTol)
                        return false;
                }
            }
            return true;
        }
    }
}
