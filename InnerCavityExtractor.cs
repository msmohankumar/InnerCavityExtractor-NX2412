// Siemens NX 2412 - Inner Cavity Extractor
// C# NXOpen conversion of the supplied Python journal.
// Build as a Class Library and load from NX using File > Execute > NX Open.
// NOTE: NXOpen managed API signatures can vary by NX maintenance release.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

public class InnerCavityExtractor
{
    private static Session theSession;
    private static UI theUI;
    private static UFSession theUf;

    public static void Main(string[] args)
    {
        theSession = Session.GetSession();
        theUI = UI.GetUI();
        theUf = UFSession.GetUFSession();
        Part workPart = theSession.Parts.Work;

        if (workPart == null)
        {
            theUI.NXMessageBox.Show("Inner Cavity Extractor", NXMessageBox.DialogType.Error,
                "No active Work Part found.");
            return;
        }

        Session.UndoMarkId mark = theSession.SetUndoMark(Session.MarkVisibility.Visible,
            "Extract Internal Cavity");
        Body originalBody = null;
        try
        {
            originalBody = SelectBody("Select the outer solid body", "1. Select Model Body");
            if (originalBody == null)
            {
                theSession.DeleteUndoMark(mark, null);
                return;
            }

            Body extractedBody = ExtractDuplicateBody(workPart, originalBody);
            theSession.DisplayManager.BlankObjects(new DisplayableObject[] { originalBody });
            workPart.ModelingViews.WorkView.UpdateDisplay();

            Face[] cavityFaces = SelectFaces(
                "Select seed face(s) of the cavity (Boss/Pocket rule active)",
                "2. Select Cavity Faces");
            if (cavityFaces == null || cavityFaces.Length == 0)
            {
                theSession.DisplayManager.ShowObjects(new DisplayableObject[] { originalBody },
                    DisplayManager.LayerSetting.ChangeLayerToSelectable);
                theSession.UndoToMark(mark, "Extract Internal Cavity");
                theSession.DeleteUndoMark(mark, null);
                return;
            }

            Feature deleteFeature = DeleteCavityFaces(workPart, cavityFaces);
            Body[] healedBodies = deleteFeature.GetBodies();
            Body healedBody = healedBodies.Length > 0 ? healedBodies[0] : extractedBody;

            Feature subtractFeature = BooleanSubtract(workPart, healedBody, originalBody);
            Body[] cavityBodies = subtractFeature.GetBodies();
            if (cavityBodies == null || cavityBodies.Length == 0)
                throw new InvalidOperationException("Boolean subtract did not create a cavity body.");

            Body cavityBody = UniteBodiesIfRequired(workPart, cavityBodies);
            IsolateCavityView(workPart, cavityBody, originalBody);

            Dictionary<string, string> metrics = ExtractDimensionsAndMass(workPart, cavityBody);
            string imagePath = CaptureSnapshot(workPart);
            WriteListingWindow(metrics);
            LaunchHtaReport(metrics, imagePath);
        }
        catch (Exception ex)
        {
            if (originalBody != null)
            {
                try
                {
                    theSession.DisplayManager.ShowObjects(new DisplayableObject[] { originalBody },
                        DisplayManager.LayerSetting.ChangeLayerToSelectable);
                }
                catch { }
            }
            try
            {
                theSession.UndoToMark(mark, "Extract Internal Cavity");
                theSession.DeleteUndoMark(mark, null);
            }
            catch { }
            theUI.NXMessageBox.Show("Execution Error", NXMessageBox.DialogType.Error, ex.ToString());
        }
    }

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Immediately;
    }

    private static Body SelectBody(string message, string title)
    {
        TaggedObject selected;
        Point3d cursor;
        Selection.MaskTriple[] mask = {
            new Selection.MaskTriple(UFConstants.UF_solid_type, 0,
                UFConstants.UF_UI_SEL_FEATURE_SOLID_BODY)
        };
        Selection.Response response = theUI.SelectionManager.SelectTaggedObject(
            message, title, Selection.SelectionScope.WorkPart,
            Selection.SelectionAction.ClearAndEnableSpecific,
            false, false, mask, out selected, out cursor);
        if (response == Selection.Response.ObjectSelected || response == Selection.Response.Ok)
            return selected as Body;
        return null;
    }

    private static Face[] SelectFaces(string message, string title)
    {
        TaggedObject[] selected;
        Selection.MaskTriple[] mask = {
            new Selection.MaskTriple(UFConstants.UF_solid_type, 0,
                UFConstants.UF_UI_SEL_FEATURE_ANY_FACE)
        };
        Selection.Response response = theUI.SelectionManager.SelectTaggedObjects(
            message, title, Selection.SelectionScope.WorkPart,
            Selection.SelectionAction.ClearAndEnableSpecific,
            false, false, mask, out selected);
        if (response != Selection.Response.ObjectSelected && response != Selection.Response.Ok)
            return new Face[0];
        return selected == null ? new Face[0] : selected.OfType<Face>().ToArray();
    }

    private static Body ExtractDuplicateBody(Part workPart, Body sourceBody)
    {
        ExtractFaceBuilder builder = workPart.Features.CreateExtractFaceBuilder(null);
        try
        {
            builder.Type = ExtractFaceBuilder.ExtractType.Body;
            builder.ParentPart = ExtractFaceBuilder.ParentPartType.WorkPart;
            builder.Associative = false;
            builder.FixAtCurrentTimestamp = true;
            builder.HideOriginal = false;
            builder.InheritDisplayProperties = false;
            SelectionIntentRule rule = workPart.ScRuleFactory.CreateRuleBodyDumb(new Body[] { sourceBody });
            builder.ExtractBodyCollector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
            Feature feature = builder.CommitFeature();
            Body[] bodies = feature.GetBodies();
            if (bodies == null || bodies.Length == 0)
                throw new InvalidOperationException("Extract Body did not return a body.");
            return bodies[0];
        }
        finally { builder.Destroy(); }
    }

    private static Feature DeleteCavityFaces(Part workPart, Face[] faces)
    {
        DeleteFaceBuilder builder = workPart.Features.CreateDeleteFaceBuilder(null);
        try
        {
            builder.Type = DeleteFaceBuilder.SelectTypes.Face;
            List<SelectionIntentRule> rules = new List<SelectionIntentRule>();
            foreach (Face face in faces)
            {
                ScRuleOptions options = workPart.ScRuleFactory.CreateRuleOptions();
                try
                {
                    options.SetSelectedFromInactive(false);
                    rules.Add(workPart.ScRuleFactory.CreateRuleFaceBossPocket(face, true, options));
                }
                finally { options.Dispose(); }
            }
            builder.FaceCollector.ReplaceRules(rules.ToArray(), false);
            return builder.CommitFeature();
        }
        finally { builder.Destroy(); }
    }

    private static Feature BooleanSubtract(Part workPart, Body target, Body tool)
    {
        // Use the collector-based builder, matching the Python journal's preferred path.
        BooleanBuilder builder = workPart.Features.CreateBooleanBuilder(null);
        try
        {
            builder.Operation = Feature.BooleanType.Subtract;
            builder.Targets.Add(target);
            builder.Tools.Add(tool);
            builder.RetainTarget = false;
            builder.RetainTool = true;
            return builder.CommitFeature();
        }
        finally { builder.Destroy(); }
    }

    private static Body UniteBodiesIfRequired(Part workPart, Body[] bodies)
    {
        if (bodies.Length == 1) return bodies[0];
        BooleanBuilder builder = workPart.Features.CreateBooleanBuilder(null);
        try
        {
            builder.Operation = Feature.BooleanType.Unite;
            builder.Targets.Add(bodies[0]);
            for (int i = 1; i < bodies.Length; i++) builder.Tools.Add(bodies[i]);
            builder.RetainTarget = false;
            builder.RetainTool = false;
            Feature uniteFeature = builder.CommitFeature();
            Body[] merged = uniteFeature.GetBodies();
            if (merged != null && merged.Length > 0) return merged[0];
            throw new InvalidOperationException("Unite operation returned no body.");
        }
        finally { builder.Destroy(); }
    }

    private static void IsolateCavityView(Part workPart, Body cavityBody, Body originalBody)
    {
        cavityBody.SetName("INTERNAL_CAVITY_VOLUME");
        theSession.DisplayManager.BlankObjects(new DisplayableObject[] { originalBody });
        theSession.DisplayManager.ShowObjects(new DisplayableObject[] { cavityBody },
            DisplayManager.LayerSetting.ChangeLayerToSelectable);
        DisplayModification display = theSession.DisplayManager.NewDisplayModification();
        try
        {
            display.ApplyToAllFaces = true;
            display.NewColor = 211;
            display.NewTranslucency = 0;
            List<DisplayableObject> objects = new List<DisplayableObject> { cavityBody };
            objects.AddRange(cavityBody.GetFaces().Cast<DisplayableObject>());
            display.Apply(objects.ToArray());
        }
        finally { display.Dispose(); }
        workPart.Views.WorkView.Fit();
        workPart.Views.WorkView.Update();
        workPart.ModelingViews.WorkView.UpdateDisplay();
    }

    private static Dictionary<string, string> ExtractDimensionsAndMass(Part workPart, Body body)
    {
        Face[] faces = body.GetFaces();
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (Edge edge in body.GetEdges())
        {
            Point3d p1, p2;
            edge.GetVertices(out p1, out p2);
            foreach (Point3d p in new[] { p1, p2 })
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
            }
        }

        double[] dims = { Math.Abs(maxX - minX), Math.Abs(maxY - minY), Math.Abs(maxZ - minZ) };
        Array.Sort(dims);
        Array.Reverse(dims);
        double length = RoundOrZero(dims[0]);
        double width = RoundOrZero(dims[1]);
        double height = RoundOrZero(dims[2]);
        double volumeMm3 = 0, surfaceArea = 0, cx = 0, cy = 0, cz = 0;

        // NX MeasureManager returns properties in the units supplied below.
        UnitCollection uc = workPart.UnitCollection;
        Unit[] units = { uc.GetBase("Area"), uc.GetBase("Volume"), uc.GetBase("Mass"), uc.GetBase("Length") };
        MeasureBodies measure = workPart.MeasureManager.NewMassProperties(units, 0.99, new Body[] { body });
        if (measure != null)
        {
            volumeMm3 = measure.Volume;
            surfaceArea = measure.Area;
            Point3d centroid = measure.Centroid;
            cx = Math.Round(centroid.X, 3); cy = Math.Round(centroid.Y, 3); cz = Math.Round(centroid.Z, 3);
        }

        double liters = volumeMm3 / 1000000.0;
        double ml = volumeMm3 / 1000.0;
        double massKg = liters; // Fluid-equivalent density = 1.0 g/cm3, matching source journal.
        return new Dictionary<string, string>
        {
            ["partName"] = workPart.Name,
            ["volume_mm3"] = Math.Round(volumeMm3, 2).ToString(CultureInfo.InvariantCulture),
            ["vol_liters"] = Math.Round(liters, 4).ToString(CultureInfo.InvariantCulture),
            ["vol_ml"] = Math.Round(ml, 2).ToString(CultureInfo.InvariantCulture),
            ["length"] = length.ToString(CultureInfo.InvariantCulture),
            ["width"] = width.ToString(CultureInfo.InvariantCulture),
            ["height"] = height.ToString(CultureInfo.InvariantCulture),
            ["projArea"] = Math.Round(length * width, 2).ToString(CultureInfo.InvariantCulture),
            ["surfaceArea"] = Math.Round(surfaceArea, 2).ToString(CultureInfo.InvariantCulture),
            ["mass_kg"] = Math.Round(massKg, 4).ToString(CultureInfo.InvariantCulture),
            ["mass_g"] = Math.Round(ml, 2).ToString(CultureInfo.InvariantCulture),
            ["faces"] = faces.Length.ToString(CultureInfo.InvariantCulture),
            ["cog"] = string.Format(CultureInfo.InvariantCulture, "{0}, {1}, {2}", cx, cy, cz)
        };
    }

    private static double RoundOrZero(double value)
    {
        return double.IsInfinity(value) || double.IsNaN(value) ? 0.0 : Math.Round(value, 2);
    }

    private static string CaptureSnapshot(Part workPart)
    {
        string path = Path.Combine(Path.GetTempPath(), workPart.Name + "_cavity_only.jpg");
        try
        {
            workPart.Views.WorkView.Fit();
            workPart.Views.WorkView.Update();
            theUf.Disp.Refresh();
            theUf.Disp.CreateImage(path, UFDisp.ImageFormat.Jpeg, UFDisp.BackgroundColor.White);
        }
        catch
        {
            // The report still opens if snapshot export is unavailable in this NX setup.
        }
        return path;
    }

    private static void WriteListingWindow(Dictionary<string, string> m)
    {
        ListingWindow lw = theSession.ListingWindow;
        lw.Open();
        lw.WriteLine("===============================================================");
        lw.WriteLine("=== ISOLATED SINGLE CAVITY BODY REPORT ===");
        lw.WriteLine("Cavity Body Name: INTERNAL_CAVITY_VOLUME");
        lw.WriteLine("Volume (L):        " + m["vol_liters"] + " L");
        lw.WriteLine("Volume (mL):       " + m["vol_ml"] + " mL");
        lw.WriteLine("Volume (mm3):      " + m["volume_mm3"] + " mm3");
        lw.WriteLine("Envelope Dims:     " + m["length"] + " x " + m["width"] + " x " + m["height"] + " mm");
        lw.WriteLine("Projected Area:    " + m["projArea"] + " mm2");
        lw.WriteLine("Surface Area:      " + m["surfaceArea"] + " mm2");
        lw.WriteLine("Total Faces:       " + m["faces"]);
        lw.WriteLine("Fluid Mass:        " + m["mass_kg"] + " kg (" + m["mass_g"] + " g)");
        lw.WriteLine("Centroid (WCS):    " + m["cog"] + " mm");
        lw.WriteLine("===============================================================");
    }

    private static void LaunchHtaReport(Dictionary<string, string> m, string imagePath)
    {
        string htaPath = Path.Combine(Path.GetTempPath(), "nx_cavity_single_body_gui.hta");
        string safeImage = imagePath.Replace("\\", "/").Replace("\"", "&quot;");
        string html = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>NX Single Cavity Inspector</title>
<HTA:APPLICATION ID='oHTA' APPLICATIONNAME='NX Single Cavity Inspector' BORDER='dialog' CAPTION='yes' MAXIMIZEBUTTON='no' MINIMIZEBUTTON='yes' SHOWINTASKBAR='yes' SINGLEINSTANCE='yes' SYSMENU='yes' WINDOWSTATE='normal' SCROLL='yes'/>
<style>*{box-sizing:border-box;font-family:Segoe UI,Arial;font-size:12px}body{background:#f0f0f0;margin:0;padding:14px;color:#111}fieldset{border:1px solid #bbb;border-radius:4px;padding:10px 12px 12px;margin-bottom:10px;background:#fff}legend{font-weight:bold;color:#004085}.row{display:flex;align-items:center;margin-bottom:6px}.row label{width:195px;flex-shrink:0}.row input{width:220px;height:23px;padding:2px 6px;border:1px solid #aaa;font-weight:bold;color:#004085;background:#fafafa}.btn{height:32px;width:100%;margin-top:6px;cursor:pointer;font-weight:bold}.green{background:#28a745;color:white}.blue{background:#0056b3;color:white}#pic{width:100%;height:200px;object-fit:contain;background:#f8f8f8;border:1px solid #ccc}</style></head><body>
<fieldset><legend>CAD Snapshot (Single Cavity Body Only)</legend><img id='pic' src='" + safeImage + @"' onerror=""this.style.display='none'"" /></fieldset>
<fieldset><legend>Isolated Cavity Geometry &amp; Mass</legend>
<div class='row'><label>Cavity Volume (Liters):</label><input id='liters' readonly value='" + m["vol_liters"] + @"'></div>
<div class='row'><label>Cavity Volume (mL / cm3):</label><input id='ml' readonly value='" + m["vol_ml"] + @"'></div>
<div class='row'><label>Cavity Volume (mm3):</label><input id='vol' readonly value='" + m["volume_mm3"] + @"'></div><hr>
<div class='row'><label>Envelope Length (mm):</label><input id='len' readonly value='" + m["length"] + @"'></div>
<div class='row'><label>Envelope Width (mm):</label><input id='wid' readonly value='" + m["width"] + @"'></div>
<div class='row'><label>Envelope Height (mm):</label><input id='hei' readonly value='" + m["height"] + @"'></div>
<div class='row'><label>Projected Area (mm2):</label><input id='proj' readonly value='" + m["projArea"] + @"'></div>
<div class='row'><label>Wetted Surface Area (mm2):</label><input id='area' readonly value='" + m["surfaceArea"] + @"'></div><hr>
<div class='row'><label>Number of Faces (Auto):</label><input id='faces' readonly value='" + m["faces"] + @"'></div>
<div class='row'><label>Fluid Mass (kg @ 1.0 g/cm3):</label><input id='mass' readonly value='" + m["mass_kg"] + @"'></div>
<div class='row'><label>Centroid X, Y, Z (mm):</label><input id='cog' readonly value='" + m["cog"] + @"'></div></fieldset>
<button class='btn green' onclick='copyMetrics()'>Copy All Metrics to Clipboard</button><button class='btn blue' onclick='window.close()'>Close Window</button>
<script language='javascript'>function copyMetrics(){var s='=== ISOLATED SINGLE CAVITY BODY REPORT ===\r\nVolume: '+liters.value+' L ('+ml.value+' mL)\r\nVolume mm3: '+vol.value+' mm3\r\nBounding Box: '+len.value+' x '+wid.value+' x '+hei.value+' mm\r\nProjected Area: '+proj.value+' mm2\r\nSurface Area: '+area.value+' mm2\r\nTotal Faces: '+faces.value+'\r\nFluid Mass: '+mass.value+' kg\r\nCentroid (X,Y,Z): '+cog.value+' mm';window.clipboardData.setData('Text',s);alert('Metrics copied to clipboard!')}window.onload=function(){window.resizeTo(475,780)};</script></body></html>";
        File.WriteAllText(htaPath, html, Encoding.UTF8);
        Process.Start("mshta.exe", "\"" + htaPath + "\"");
    }
}
