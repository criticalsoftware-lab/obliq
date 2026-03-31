using System;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Geometry;

namespace Obliq
{
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
            if (doc == null) return Result.Failure;

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

            double angleDeg = 90.0;
            double scale = 1.0;

            GetOption getOpt = new GetOption();
            getOpt.SetCommandPrompt("Oblique projection options. Press Enter to accept");
            getOpt.AcceptNothing(true);

            OptionDouble optAngle = new OptionDouble(angleDeg);
            OptionDouble optScale = new OptionDouble(scale);

            while (true)
            {
                getOpt.ClearCommandOptions();
                
                int ixAngle = getOpt.AddOptionDouble("Angle", ref optAngle);
                int ixScale = getOpt.AddOptionDouble("Scale", ref optScale);

                GetResult res = getOpt.Get();

                if (res == GetResult.Nothing) break;
                if (res == GetResult.Cancel) return Result.Cancel;

                if (res == GetResult.Option)
                {
                    if (getOpt.Option().Index == ixAngle)
                    {
                        GetNumber gn = new GetNumber();
                        gn.SetCommandPrompt("Shear angle in degrees");
                        gn.SetDefaultNumber(angleDeg);
                        gn.SetLowerLimit(0.0, false);
                        gn.SetUpperLimit(360.0, false);
                        if (gn.Get() == GetResult.Number)
                        {
                            angleDeg = gn.Number();
                            optAngle.CurrentValue = angleDeg;
                        }
                    }
                    else if (getOpt.Option().Index == ixScale)
                    {
                        GetNumber gn = new GetNumber();
                        gn.SetCommandPrompt("Shear scale factor");
                        gn.SetDefaultNumber(scale);
                        gn.SetLowerLimit(0.001, false);
                        gn.SetUpperLimit(100.0, false);
                        if (gn.Get() == GetResult.Number)
                        {
                            scale = gn.Number();
                            optScale.CurrentValue = scale;
                        }
                    }
                    continue;
                }
                break;
            }

            double alpha = angleDeg * Math.PI / 180.0;
            double shx = scale * Math.Cos(alpha);
            double shy = scale * Math.Sin(alpha);

            Transform shear = Transform.Identity;
            shear[0, 2] = shx;
            shear[1, 2] = shy;

            Transform flatten = Transform.Identity;
            flatten[2, 0] = 0.0;
            flatten[2, 1] = 0.0;
            flatten[2, 2] = 0.0;
            flatten[2, 3] = 0.0;

            Transform combined = flatten * shear;

            string layerName = $"ObliqueCurveMake2D{angleDeg:0}_{scale:0.00}";
            
            int layerIndex = doc.Layers.FindByFullPath(layerName, -1);
            if (layerIndex < 0)
            {
                Layer layer = new Layer
                {
                    Name = layerName,
                    Color = Color.Black
                };
                layerIndex = doc.Layers.Add(layer);
            }

            int added = 0;
            for (int i = 0; i < count; i++)
            {
                var refObj = go.Object(i);
                var crv = refObj.Curve();
                if (crv == null) continue;

                var projected = crv.DuplicateCurve();
                if (projected != null)
                {
                    if (projected.Transform(combined))
                    {
                        ObjectAttributes attrs = new ObjectAttributes { LayerIndex = layerIndex };
                        if (doc.Objects.AddCurve(projected, attrs) != Guid.Empty)
                            added++;
                    }
                }
            }

            doc.Views.Redraw();
            RhinoApp.WriteLine($"ObliqueCurveMake2D: Projected {added} curve(s) to layer \"{layerName}\"");

            return added > 0 ? Result.Success : Result.Nothing;
        }
    }
}
