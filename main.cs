using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Colors;

[assembly: CommandClass(typeof(BuildingGenerator.Commands))]

namespace BuildingGenerator
{
    public class Commands
    {
        [CommandMethod("GENERATE_FULL_PLAN")]
        public void GenerateFullPlan()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    
                    // יצירת שכבות עבודה
                    EnsureLayerExists(tr, db, "W_HATCH");
                    EnsureLayerExists(tr, db, "30");
                    EnsureLayerExists(tr, db, "TEMP_3");
                    EnsureLayerExists(tr, db, "TEMP_4");

                    TypedValue[] filter = {
                        new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                        new TypedValue((int)DxfCode.Operator, "<OR"),
                            new TypedValue((int)DxfCode.LayerName, "20"),
                            new TypedValue((int)DxfCode.LayerName, "30"),
                            new TypedValue((int)DxfCode.LayerName, "CORE"),
                        new TypedValue((int)DxfCode.Operator, "OR>")
                    };
                    
                    PromptSelectionResult selRes = ed.GetSelection(new SelectionFilter(filter));
                    if (selRes.Status != PromptStatus.OK) return;

                    List<Polyline> mainBuildings = new List<Polyline>();
                    List<Polyline> balconies = new List<Polyline>();
                    List<Polyline> cores = new List<Polyline>();

                    foreach (ObjectId id in selRes.Value.GetObjectIds())
                    {
                        Polyline pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (pl == null || !pl.Closed) continue;

                        if (pl.Layer == "30") mainBuildings.Add(pl);
                        else if (pl.Layer == "20") balconies.Add(pl);
                        else if (pl.Layer == "CORE") cores.Add(pl);
                    }

                    // עיבוד בניינים (ללא שינוי)
                    foreach (Polyline bld in mainBuildings)
                    {
                        var bldRegs = Region.CreateFromCurves(new DBObjectCollection { bld });
                        if (bldRegs.Count == 0) continue;
                        Region finalReg = bldRegs[0] as Region;

                        foreach (Polyline bal in balconies)
                        {
                            if (DoExtentsOverlap(bld.GeometricExtents, bal.GeometricExtents))
                            {
                                CreateBalconyHatch(tr, btr, bal);
                                var balRegs = Region.CreateFromCurves(new DBObjectCollection { bal });
                                if (balRegs.Count > 0)
                                    finalReg.BooleanOperation(BooleanOperationType.BoolSubtract, balRegs[0] as Region);
                            }
                        }

                        Polyline line1 = ProcessProcessedRegion(tr, btr, ed, finalReg, "30");
                        if (line1 != null) ApplyWallLogic(tr, btr, line1, false);

                        bld.UpgradeOpen();
                        bld.Erase();
                        finalReg.Dispose();
                    }

                    // --- עיבוד גרעינים עם מילוי לבן בשכבה 30 ---
                    foreach (Polyline line3 in cores)
                    {
                        line3.UpgradeOpen();
                        line3.Layer = "30"; // העברה ישירה לשכבה 30
                        
                        // יצירת אובייקט 4 - הקו הפנימי
                        DBObjectCollection offsets = line3.GetOffsetCurves(-0.2);
                        if (offsets.Count > 0)
                        {
                            Polyline line4 = offsets[0] as Polyline;
                            line4.Layer = "30"; // גם הקו הפנימי בשכבה 30
                            btr.AppendEntity(line4);
                            tr.AddNewlyCreatedDBObject(line4, true);

                            // מילוי לבן בתוך הקו הפנימי - בשכבה 30
                            CreateHatchOnLayer(tr, btr, line4.ObjectId, "30", 7);

                            // יצירת קיר אפור בין הקו החיצוני לפנימי
                            CreateWallHatch(tr, btr, line3.ObjectId, line4.ObjectId);
                        }
                    }

                    tr.Commit();
                    ed.WriteMessage("\nPlan processed successfully - core inner areas filled white in layer 30.");
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage("\nError: " + ex.Message);
                    tr.Abort();
                }
            }
        }

        private void CreateHatchOnLayer(Transaction tr, BlockTableRecord btr, ObjectId boundaryId, string layer, int colorIndex)
        {
            Hatch hat = new Hatch();
            hat.SetDatabaseDefaults();
            hat.Layer = layer; // ה-HATCH יהיה בשכבה שמועברת (30)
            hat.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hat.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);
            
            btr.AppendEntity(hat);
            tr.AddNewlyCreatedDBObject(hat, true);
            hat.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { boundaryId });
            hat.EvaluateHatch(true);
        }

        private void CreateWallHatch(Transaction tr, BlockTableRecord btr, ObjectId outerId, ObjectId innerId)
        {
            Hatch hat = new Hatch();
            hat.SetDatabaseDefaults();
            hat.Layer = "0";
            hat.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hat.Color = Color.FromColorIndex(ColorMethod.ByAci, 9);
            
            btr.AppendEntity(hat);
            tr.AddNewlyCreatedDBObject(hat, true);
            hat.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { outerId });
            hat.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection { innerId });
            hat.EvaluateHatch(true);
        }

        private void ApplyWallLogic(Transaction tr, BlockTableRecord btr, Polyline outer, bool isCore)
        {
            DBObjectCollection offsets = outer.GetOffsetCurves(-0.2);
            if (offsets.Count > 0)
            {
                Polyline inner = offsets[0] as Polyline;
                inner.Layer = outer.Layer;
                btr.AppendEntity(inner);
                tr.AddNewlyCreatedDBObject(inner, true);
                CreateWallHatch(tr, btr, outer.ObjectId, inner.ObjectId);
            }
        }

        private Polyline ProcessProcessedRegion(Transaction tr, BlockTableRecord btr, Editor ed, Region reg, string layer)
        {
            DBObjectCollection exploded = new DBObjectCollection();
            reg.Explode(exploded);
            ObjectIdCollection ids = new ObjectIdCollection();
            foreach (DBObject obj in exploded)
            {
                Entity ent = obj as Entity;
                ent.Layer = layer;
                ids.Add(btr.AppendEntity(ent));
                tr.AddNewlyCreatedDBObject(ent, true);
            }
            object oldPeditAccept = Application.GetSystemVariable("PEDITACCEPT");
            Application.SetSystemVariable("PEDITACCEPT", 1);
            try {
                SelectionSet ss = SelectionSet.FromObjectIds(ids.Cast<ObjectId>().ToArray());
                ed.Command("_.PEDIT", "_M", ss, "", "_J", "0.0", "");
                Application.SetSystemVariable("PEDITACCEPT", oldPeditAccept);
                PromptSelectionResult lastObj = ed.SelectLast();
                return (lastObj.Status == PromptStatus.OK) ? tr.GetObject(lastObj.Value.GetObjectIds()[0], OpenMode.ForWrite) as Polyline : null;
            } catch {
                Application.SetSystemVariable("PEDITACCEPT", oldPeditAccept);
                return null;
            }
        }

        private void CreateBalconyHatch(Transaction tr, BlockTableRecord btr, Polyline pl)
        {
            Hatch hat = new Hatch();
            hat.SetDatabaseDefaults();
            hat.Layer = "W_HATCH";
            hat.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31");
            hat.Color = Color.FromRgb(192, 163, 206);
            hat.BackgroundColor = Color.FromColorIndex(ColorMethod.ByAci, 7);
            btr.AppendEntity(hat);
            tr.AddNewlyCreatedDBObject(hat, true);
            hat.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { pl.ObjectId });
            hat.EvaluateHatch(true);
        }

        private bool DoExtentsOverlap(Extents3d e1, Extents3d e2)
        {
            return (e1.MinPoint.X <= e2.MaxPoint.X && e1.MaxPoint.X >= e2.MinPoint.X &&
                    e1.MinPoint.Y <= e2.MaxPoint.Y && e1.MaxPoint.Y >= e2.MinPoint.Y);
        }

        private void EnsureLayerExists(Transaction tr, Database db, string name)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(name))
            {
                lt.UpgradeOpen();
                LayerTableRecord ltr = new LayerTableRecord { Name = name };
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
        }
    }
}