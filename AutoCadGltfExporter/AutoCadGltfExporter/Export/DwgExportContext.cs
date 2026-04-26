using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using GltfExporter.Shared;

namespace AutoCadGltfExporter.Export
{
    // Top-level orchestrator. Walks ModelSpace, dispatches each entity to
    // the appropriate handler (Solid/Curve/Text/Block), accumulates output
    // into per-layer buckets, then emits one glTF node per layer.
    internal class DwgExportContext
    {
        private const int MaxBlockDepth = 32;
        private static readonly RgbColor DefaultColor = new RgbColor(204, 204, 204);

        private readonly Database _db;
        private readonly ExportOptions _options;
        private readonly Editor _editor;
        private readonly GltfBuilder _builder = new GltfBuilder();
        private readonly MaterialBuilder _materials;
        private readonly LineBuilder _lines;
        private readonly SolidTessellator _tessellator;
        private readonly CurveSampler _sampler;
        private readonly BlockRefHandler _blockHandler = new BlockRefHandler();

        private readonly Dictionary<string, LayerBucket> _byLayer = new Dictionary<string, LayerBucket>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RgbColor> _layerColors = new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BlockAnalysis> _blockAnalysis = new Dictionary<string, BlockAnalysis>(StringComparer.OrdinalIgnoreCase);
        private readonly double _unitScale;

        public DwgExportContext(Database db, ExportOptions options, Editor editor)
        {
            _db = db;
            _options = options;
            _editor = editor;
            _materials = new MaterialBuilder(_builder);
            _lines = new LineBuilder(_builder);
            _unitScale = DwgUnitConverter.UnitFactorToMeters(_db.Insunits);
            _tessellator = new SolidTessellator(_unitScale);
            // Chord height in drawing units: convert from meters back through unit scale.
            _sampler = new CurveSampler(0.005 / Math.Max(_unitScale, 1e-9));

            _builder.Root.Extras = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" },
                { "source", "AutoCAD" },
                { "unit", "meter" },
                { "originalUnit", _db.Insunits.ToString() }
            };
        }

        public void Run()
        {
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                IndexLayers(tr);

                var bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var rootContext = CreateRootContext();

                int processed = 0;
                int skipped = 0;
                foreach (ObjectId id in ms)
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null) { skipped++; continue; }
                    if (!entity.Visible) { skipped++; continue; }

                    try
                    {
                        ProcessEntityToScene(entity, tr, rootContext);
                        processed++;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        _editor?.WriteMessage("\n[skip {0}] {1}: {2}\n",
                            id.Handle.Value.ToString("X"),
                            entity.GetType().Name,
                            ex.Message);
                    }
                }

                EmitLayerNodes();
                tr.Commit();

                _editor?.WriteMessage("\nProcessed {0} entities ({1} skipped) across {2} layers.\n",
                    processed, skipped, _byLayer.Count);
            }
        }

        public void WriteGlb(string path) => _builder.WriteGlb(path);

        // ---------- scene dispatch ----------

        private void ProcessEntityToScene(Entity entity, Transaction tr, EntityContext context)
        {
            switch (entity)
            {
                case BlockReference br:
                    ProcessBlockReferenceToScene(br, tr, context);
                    break;

                case Solid3d _:
                case Autodesk.AutoCAD.DatabaseServices.Surface _:
                case SubDMesh _:
                case PolyFaceMesh _:
                case PolygonMesh _:
                case Region _:
                case Body _:
                    ProcessSolidToScene(entity, tr, context);
                    break;

                case Hatch hatch:
                    ProcessHatchToScene(hatch, tr, context);
                    break;

                case DBText _:
                case MText _:
                case Dimension _:
                case Leader _:
                case MLeader _:
                    ProcessAnnotationToScene(entity, tr, context);
                    break;

                case Curve curve:
                    ProcessCurveToScene(curve, tr, context);
                    break;

                default:
                    // Unknown / proxy: try a best-effort explode + recurse.
                    ProcessAnnotationToScene(entity, tr, context);
                    break;
            }
        }

        private void ProcessSolidToScene(Entity entity, Transaction tr, EntityContext context)
        {
            var result = _tessellator.Tessellate(entity);
            if (result.IsEmpty) return;

            var state = ResolveEntityState(entity, context);
            var bucket = GetOrCreateBucket(state.LayerName);
            var matIdx = ResolveMaterial(state.Color);

            var primBucket = GetOrCreateMeshBucket(bucket.Triangles, matIdx);
            AppendMesh(primBucket, result, context.Transform);
            AddEntityExtras(bucket, entity, tr);
        }

        private void ProcessCurveToScene(Curve curve, Transaction tr, EntityContext context)
        {
            var pts = _sampler.Sample(curve);
            if (pts == null || pts.Count < 2) return;

            var state = ResolveEntityState(curve, context);
            var bucket = GetOrCreateBucket(state.LayerName);
            var matIdx = ResolveMaterial(state.Color);

            var primBucket = GetOrCreateMeshBucket(bucket.Lines, matIdx);
            AppendPolyline(primBucket, pts, context.Transform);
            AddEntityExtras(bucket, curve, tr);
        }

        private void ProcessAnnotationToScene(Entity entity, Transaction tr, EntityContext context)
        {
            DBObjectCollection bits = null;
            try
            {
                bits = TextExploder.ExplodeRecursive(entity, maxDepth: 4);
            }
            catch
            {
                return;
            }

            var originState = ResolveEntityState(entity, context);
            var explodedContext = CreateDerivedContext(context, originState);

            try
            {
                foreach (DBObject obj in bits)
                {
                    var child = obj as Entity;
                    if (child == null) continue;
                    if (child.Layer == null) child.Layer = entity.Layer;

                    if (child is BlockReference childBlock)
                    {
                        ProcessBlockReferenceToScene(childBlock, tr, explodedContext);
                    }
                    else if (child is Curve childCurve)
                    {
                        ProcessCurveToScene(childCurve, tr, explodedContext);
                    }
                    else if (child is Solid solid2d)
                    {
                        ProcessFilledSolidToScene(solid2d, entity, explodedContext, originState, tr);
                    }
                    else if (child is Region)
                    {
                        ProcessSolidToScene(child, tr, explodedContext);
                    }
                }
            }
            finally
            {
                foreach (DBObject obj in bits) obj.Dispose();
            }
        }

        private void ProcessHatchToScene(Hatch hatch, Transaction tr, EntityContext context)
        {
            var solidFill = hatch.PatternType == HatchPatternType.PreDefined &&
                            string.Equals(hatch.PatternName, "SOLID", StringComparison.OrdinalIgnoreCase);
            if (solidFill)
            {
                ProcessSolidToScene(hatch, tr, context);
            }
            else
            {
                ProcessAnnotationToScene(hatch, tr, context);
            }
        }

        private void ProcessFilledSolidToScene(Solid solid, Entity origin, EntityContext context, ResolvedEntityState originState, Transaction tr)
        {
            var bucket = GetOrCreateBucket(originState.LayerName);
            var matIdx = ResolveMaterial(originState.Color);
            var pb = GetOrCreateMeshBucket(bucket.Triangles, matIdx);
            AppendFilledSolid(pb, solid, context.Transform);
            AddEntityExtras(bucket, origin, tr);
        }

        private void ProcessBlockReferenceToScene(BlockReference br, Transaction tr, EntityContext context)
        {
            var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return;

            var state = ResolveEntityState(br, context);
            if (!CanInstanceBlock(btr, tr))
            {
                var childContext = CreateBlockContext(br, btr, context, state);
                if (childContext == null) return;

                ProcessBlockContentsToScene(btr, tr, childContext);
                return;
            }

            var meshIdx = GetOrBuildTemplateMesh(btr, tr);
            if (!meshIdx.HasValue) return;

            var bucket = GetOrCreateBucket(state.LayerName);
            var node = new GltfNode
            {
                Name = SafeName(br),
                Mesh = meshIdx.Value,
                Matrix = DwgUnitConverter.ToGltfMatrix(context.Transform * br.BlockTransform, _unitScale),
                Extras = DwgPropertyCollector.Collect(br, tr)
            };
            bucket.InstanceNodes.Add(node);
        }

        private void ProcessBlockContentsToScene(BlockTableRecord btr, Transaction tr, EntityContext context)
        {
            foreach (ObjectId id in btr)
            {
                var child = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (child == null || !child.Visible) continue;
                ProcessEntityToScene(child, tr, context);
            }
        }

        // ---------- template dispatch ----------

        private int? GetOrBuildTemplateMesh(BlockTableRecord btr, Transaction tr)
        {
            if (!_blockHandler.HasSeen(btr))
            {
                var meshIdx = BuildBlockTemplate(btr, tr);
                _blockHandler.RegisterMesh(btr, meshIdx);
                return meshIdx;
            }

            _blockHandler.TryGetMesh(btr, out var cached);
            return cached >= 0 ? (int?)cached : null;
        }

        private int? BuildBlockTemplate(BlockTableRecord btr, Transaction tr)
        {
            var inner = new MeshBuckets();
            var rootContext = CreateTemplateRootContext(btr);

            ProcessBlockContentsToTemplate(btr, tr, rootContext, inner);
            if (inner.Triangles.Count == 0 && inner.Lines.Count == 0) return null;

            var mesh = new GltfMesh { Name = "block_" + btr.Name };
            EmitTriangleBuckets(mesh, inner.Triangles);
            EmitLineBuckets(mesh, inner.Lines);
            if (mesh.Primitives.Count == 0) return null;

            return _builder.AddMesh(mesh);
        }

        private void ProcessBlockContentsToTemplate(BlockTableRecord btr, Transaction tr, EntityContext context, MeshBuckets buckets)
        {
            foreach (ObjectId id in btr)
            {
                var child = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (child == null || !child.Visible) continue;
                ProcessEntityToTemplate(child, tr, context, buckets);
            }
        }

        private void ProcessEntityToTemplate(Entity entity, Transaction tr, EntityContext context, MeshBuckets buckets)
        {
            switch (entity)
            {
                case BlockReference br:
                    ProcessBlockReferenceToTemplate(br, tr, context, buckets);
                    break;

                case Solid3d _:
                case Autodesk.AutoCAD.DatabaseServices.Surface _:
                case SubDMesh _:
                case PolyFaceMesh _:
                case PolygonMesh _:
                case Region _:
                case Body _:
                    ProcessSolidToTemplate(entity, context, buckets);
                    break;

                case Hatch hatch:
                    ProcessHatchToTemplate(hatch, tr, context, buckets);
                    break;

                case DBText _:
                case MText _:
                case Dimension _:
                case Leader _:
                case MLeader _:
                    ProcessAnnotationToTemplate(entity, tr, context, buckets);
                    break;

                case Curve curve:
                    ProcessCurveToTemplate(curve, context, buckets);
                    break;

                default:
                    ProcessAnnotationToTemplate(entity, tr, context, buckets);
                    break;
            }
        }

        private void ProcessSolidToTemplate(Entity entity, EntityContext context, MeshBuckets buckets)
        {
            var result = _tessellator.Tessellate(entity);
            if (result.IsEmpty) return;

            var state = ResolveEntityState(entity, context);
            var matIdx = ResolveMaterial(state.Color);
            var primBucket = GetOrCreateMeshBucket(buckets.Triangles, matIdx);
            AppendMesh(primBucket, result, context.Transform);
        }

        private void ProcessCurveToTemplate(Curve curve, EntityContext context, MeshBuckets buckets)
        {
            var pts = _sampler.Sample(curve);
            if (pts == null || pts.Count < 2) return;

            var state = ResolveEntityState(curve, context);
            var matIdx = ResolveMaterial(state.Color);
            var primBucket = GetOrCreateMeshBucket(buckets.Lines, matIdx);
            AppendPolyline(primBucket, pts, context.Transform);
        }

        private void ProcessAnnotationToTemplate(Entity entity, Transaction tr, EntityContext context, MeshBuckets buckets)
        {
            DBObjectCollection bits = null;
            try
            {
                bits = TextExploder.ExplodeRecursive(entity, maxDepth: 4);
            }
            catch
            {
                return;
            }

            var originState = ResolveEntityState(entity, context);
            var explodedContext = CreateDerivedContext(context, originState);

            try
            {
                foreach (DBObject obj in bits)
                {
                    var child = obj as Entity;
                    if (child == null) continue;
                    if (child.Layer == null) child.Layer = entity.Layer;

                    if (child is BlockReference childBlock)
                    {
                        ProcessBlockReferenceToTemplate(childBlock, tr, explodedContext, buckets);
                    }
                    else if (child is Curve childCurve)
                    {
                        ProcessCurveToTemplate(childCurve, explodedContext, buckets);
                    }
                    else if (child is Solid solid2d)
                    {
                        var matIdx = ResolveMaterial(originState.Color);
                        var primBucket = GetOrCreateMeshBucket(buckets.Triangles, matIdx);
                        AppendFilledSolid(primBucket, solid2d, context.Transform);
                    }
                    else if (child is Region)
                    {
                        ProcessSolidToTemplate(child, explodedContext, buckets);
                    }
                }
            }
            finally
            {
                foreach (DBObject obj in bits) obj.Dispose();
            }
        }

        private void ProcessHatchToTemplate(Hatch hatch, Transaction tr, EntityContext context, MeshBuckets buckets)
        {
            var solidFill = hatch.PatternType == HatchPatternType.PreDefined &&
                            string.Equals(hatch.PatternName, "SOLID", StringComparison.OrdinalIgnoreCase);
            if (solidFill)
            {
                ProcessSolidToTemplate(hatch, context, buckets);
            }
            else
            {
                ProcessAnnotationToTemplate(hatch, tr, context, buckets);
            }
        }

        private void ProcessBlockReferenceToTemplate(BlockReference br, Transaction tr, EntityContext context, MeshBuckets buckets)
        {
            var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return;

            var state = ResolveEntityState(br, context);
            var childContext = CreateBlockContext(br, btr, context, state);
            if (childContext == null) return;

            ProcessBlockContentsToTemplate(btr, tr, childContext, buckets);
        }

        // ---------- colors ----------

        private EntityContext CreateRootContext()
        {
            var zeroLayerColor = ResolveLayerColor("0", DefaultColor);
            return new EntityContext(
                "0",
                zeroLayerColor,
                DefaultColor,
                Matrix3d.Identity,
                0,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private EntityContext CreateTemplateRootContext(BlockTableRecord btr)
        {
            var zeroLayerColor = ResolveLayerColor("0", DefaultColor);
            var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                GetBlockKey(btr)
            };
            return new EntityContext("0", zeroLayerColor, DefaultColor, Matrix3d.Identity, 1, stack);
        }

        private EntityContext CreateDerivedContext(EntityContext context, ResolvedEntityState state)
        {
            return new EntityContext(
                state.LayerName,
                state.LayerColor,
                state.Color,
                context.Transform,
                context.BlockDepth,
                context.BlockStack);
        }

        private EntityContext CreateBlockContext(BlockReference br, BlockTableRecord btr, EntityContext parentContext, ResolvedEntityState brState)
        {
            var key = GetBlockKey(btr);
            if (parentContext.BlockDepth >= MaxBlockDepth)
            {
                _editor?.WriteMessage("\n[skip block] {0}: max recursion depth reached.\n", btr.Name);
                return null;
            }
            if (parentContext.BlockStack.Contains(key))
            {
                _editor?.WriteMessage("\n[skip block] {0}: recursive block reference detected.\n", btr.Name);
                return null;
            }

            var stack = new HashSet<string>(parentContext.BlockStack, StringComparer.OrdinalIgnoreCase)
            {
                key
            };

            return new EntityContext(
                brState.LayerName,
                brState.LayerColor,
                brState.Color,
                parentContext.Transform * br.BlockTransform,
                parentContext.BlockDepth + 1,
                stack);
        }

        private ResolvedEntityState ResolveEntityState(Entity entity, EntityContext context)
        {
            var layerName = ResolveEffectiveLayerName(entity.Layer, context.EffectiveLayerName);
            var layerColor = ResolveLayerColor(layerName, context.EffectiveLayerColor);
            var color = ResolveColorValue(entity.Color, layerColor, context.InheritedColor);
            return new ResolvedEntityState(layerName, layerColor, color);
        }

        private int ResolveMaterial(RgbColor color)
        {
            var key = "rgb:" + color.R + "," + color.G + "," + color.B;
            return _materials.GetOrCreate(
                key, "color_" + color.R + "_" + color.G + "_" + color.B,
                color.R / 255f, color.G / 255f, color.B / 255f, 1f,
                metallic: 0f, roughness: 0.8f);
        }

        private RgbColor ResolveColorValue(Color color, RgbColor layerColor, RgbColor inheritedColor)
        {
            if (color == null) return DefaultColor;

            if (IsByLayer(color)) return layerColor;
            if (IsByBlock(color)) return inheritedColor;
            if (IsByColor(color))
            {
                try { return new RgbColor(color.Red, color.Green, color.Blue); }
                catch { return DefaultColor; }
            }
            if (color.IsByAci && TryLookupAciColor(color.ColorIndex, out var aciColor))
            {
                return aciColor;
            }
            return DefaultColor;
        }

        private void IndexLayers(Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                var lr = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                if (lr == null) continue;
                _layerColors[lr.Name] = ResolveColorValue(lr.Color, DefaultColor, DefaultColor);
            }
        }

        private RgbColor ResolveLayerColor(string layerName, RgbColor fallback)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return fallback;
            if (_layerColors.TryGetValue(layerName, out var color)) return color;
            return fallback;
        }

        private static string ResolveEffectiveLayerName(string rawLayerName, string parentLayerName)
        {
            return UsesParentLayer(rawLayerName)
                ? NormalizeLayerName(parentLayerName)
                : NormalizeLayerName(rawLayerName);
        }

        private static bool UsesParentLayer(string layerName)
        {
            return string.IsNullOrWhiteSpace(layerName) ||
                   string.Equals(layerName, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLayerName(string layerName)
        {
            return string.IsNullOrWhiteSpace(layerName) ? "0" : layerName;
        }

        private static bool IsByLayer(Color color)
        {
            return color != null && (color.IsByLayer || color.ColorMethod == ColorMethod.ByLayer);
        }

        private static bool IsByBlock(Color color)
        {
            return color != null && (color.IsByBlock || color.ColorMethod == ColorMethod.ByBlock);
        }

        private static bool IsByColor(Color color)
        {
            return color != null && (color.IsByColor || color.ColorMethod == ColorMethod.ByColor);
        }

        private static bool TryLookupAciColor(short colorIndex, out RgbColor color)
        {
            color = DefaultColor;
            if (colorIndex < 1 || colorIndex > 255) return false;

            var trueColor = EntityColor.LookUpRgb((byte)colorIndex);
            color = new RgbColor(
                (trueColor >> 16) & 0xFF,
                (trueColor >> 8) & 0xFF,
                trueColor & 0xFF);
            return true;
        }

        // ---------- block analysis ----------

        private bool CanInstanceBlock(BlockTableRecord btr, Transaction tr)
        {
            var analysis = GetBlockAnalysis(btr, tr);
            return !analysis.DependsOnInsertionColor && !analysis.DependsOnInsertionLayer;
        }

        private BlockAnalysis GetBlockAnalysis(BlockTableRecord btr, Transaction tr)
        {
            var key = GetBlockKey(btr);
            if (_blockAnalysis.TryGetValue(key, out var cached))
            {
                return cached;
            }
            return AnalyzeBlock(btr, tr, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private BlockAnalysis AnalyzeBlock(BlockTableRecord btr, Transaction tr, HashSet<string> visiting)
        {
            var key = GetBlockKey(btr);
            if (_blockAnalysis.TryGetValue(key, out var cached))
            {
                return cached;
            }
            if (!visiting.Add(key))
            {
                return BlockAnalysis.Dynamic;
            }

            var analysis = new BlockAnalysis();
            foreach (ObjectId id in btr)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || !entity.Visible) continue;

                if (!(entity is BlockReference br))
                {
                    var directDep = GetColorDependency(entity.Color, UsesParentLayer(entity.Layer));
                    analysis.DependsOnInsertionColor |= directDep.FromParentColor;
                    analysis.DependsOnInsertionLayer |= directDep.FromParentLayer;
                }
                else
                {
                    var nestedBtr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    if (nestedBtr == null)
                    {
                        continue;
                    }

                    var nested = AnalyzeBlock(nestedBtr, tr, visiting);
                    var colorDep = GetColorDependency(br.Color, UsesParentLayer(br.Layer));

                    if (nested.DependsOnInsertionColor)
                    {
                        analysis.DependsOnInsertionColor |= colorDep.FromParentColor;
                        analysis.DependsOnInsertionLayer |= colorDep.FromParentLayer;
                    }
                    if (nested.DependsOnInsertionLayer && UsesParentLayer(br.Layer))
                    {
                        analysis.DependsOnInsertionLayer = true;
                    }
                }

                if (analysis.DependsOnInsertionColor && analysis.DependsOnInsertionLayer)
                {
                    break;
                }
            }

            visiting.Remove(key);
            _blockAnalysis[key] = analysis;
            return analysis;
        }

        private static ColorDependency GetColorDependency(Color color, bool inheritsParentLayer)
        {
            var dep = new ColorDependency();
            if (color == null) return dep;

            if (IsByBlock(color))
            {
                dep.FromParentColor = true;
            }
            else if (IsByLayer(color) && inheritsParentLayer)
            {
                dep.FromParentLayer = true;
            }
            return dep;
        }

        private static string GetBlockKey(BlockTableRecord btr)
        {
            return btr.ObjectId.Handle.Value.ToString("X");
        }

        // ---------- bucket emit ----------

        private void EmitLayerNodes()
        {
            var rootNodes = new List<int>(_byLayer.Count);
            foreach (var kv in _byLayer)
            {
                var layerName = kv.Key;
                var bucket = kv.Value;

                int? meshIndex = null;
                if (bucket.Triangles.Count > 0 || bucket.Lines.Count > 0)
                {
                    var mesh = new GltfMesh { Name = "layer_" + layerName };
                    EmitTriangleBuckets(mesh, bucket.Triangles);
                    EmitLineBuckets(mesh, bucket.Lines);
                    if (mesh.Primitives.Count > 0) meshIndex = _builder.AddMesh(mesh);
                }

                var extras = new Dictionary<string, object>
                {
                    ["layer"] = layerName,
                };
                if (_layerColors.TryGetValue(layerName, out var lc))
                {
                    extras["layerColor"] = new int[] { lc.R, lc.G, lc.B };
                }
                if (_options.IncludeProperties && bucket.PerEntityExtras.Count > 0)
                {
                    var arr = new List<object>(bucket.PerEntityExtras.Count);
                    foreach (var e in bucket.PerEntityExtras)
                    {
                        e.Extras["entityType"] = e.EntityType;
                        arr.Add(e.Extras);
                    }
                    extras["entities"] = arr;
                }

                var node = new GltfNode
                {
                    Name = "layer_" + layerName,
                    Mesh = meshIndex,
                    Extras = extras
                };

                if (bucket.InstanceNodes.Count > 0)
                {
                    var children = new List<int>();
                    foreach (var inst in bucket.InstanceNodes)
                    {
                        if (inst.Extras is Dictionary<string, object> instExtras)
                        {
                            instExtras["layer"] = layerName;
                        }
                        children.Add(_builder.AddNode(inst));
                    }
                    node.Children = children;
                }

                rootNodes.Add(_builder.AddNode(node));
            }

            _builder.Root.Scenes.Clear();
            _builder.Root.Scenes.Add(new GltfScene { Nodes = rootNodes });
            _builder.Root.Scene = 0;
        }

        private void EmitTriangleBuckets(GltfMesh mesh, List<MeshPrimitiveBucket> buckets)
        {
            foreach (var pb in buckets)
            {
                if (pb.Indices.Count == 0) continue;
                var minMax = ComputeMinMax(pb.Positions);

                GltfPrimitive prim;
                if (_options.EnableDraco)
                {
                    prim = _builder.AddDracoPrimitive(
                        pb.Positions.ToArray(), minMax.min, minMax.max,
                        pb.Normals.Count > 0 ? pb.Normals.ToArray() : null,
                        null,
                        pb.Indices.ToArray(),
                        pb.Material,
                        _options.DracoCompressionLevel);
                }
                else
                {
                    var posAcc = _builder.AddFloat3Accessor(pb.Positions, minMax.min, minMax.max, GltfTarget.ArrayBuffer);
                    var nrmAcc = _builder.AddFloat3Accessor(pb.Normals, null, null, GltfTarget.ArrayBuffer);
                    var idxAcc = _builder.AddIndexAccessor(pb.Indices);
                    prim = new GltfPrimitive { Material = pb.Material, Indices = idxAcc };
                    prim.Attributes["POSITION"] = posAcc;
                    prim.Attributes["NORMAL"] = nrmAcc;
                }
                mesh.Primitives.Add(prim);
            }
        }

        private void EmitLineBuckets(GltfMesh mesh, List<MeshPrimitiveBucket> buckets)
        {
            foreach (var pb in buckets)
            {
                if (pb.Indices.Count == 0) continue;
                var prim = _lines.AddLinePrimitive(pb.Positions, pb.Indices, pb.Material);
                mesh.Primitives.Add(prim);
            }
        }

        // ---------- bucket construction ----------

        private LayerBucket GetOrCreateBucket(string layer)
        {
            var key = NormalizeLayerName(layer);
            if (!_byLayer.TryGetValue(key, out var b))
            {
                b = new LayerBucket();
                _byLayer[key] = b;
            }
            return b;
        }

        private static MeshPrimitiveBucket GetOrCreateMeshBucket(List<MeshPrimitiveBucket> list, int materialIdx)
        {
            foreach (var b in list)
            {
                if (b.Material == materialIdx) return b;
            }
            var nb = new MeshPrimitiveBucket { Material = materialIdx };
            list.Add(nb);
            return nb;
        }

        private void AddEntityExtras(LayerBucket bucket, Entity entity, Transaction tr)
        {
            bucket.PerEntityExtras.Add(new EntityExtras
            {
                Extras = DwgPropertyCollector.Collect(entity, tr),
                EntityType = entity.GetType().Name
            });
        }

        // ---------- geometry ----------

        private void AppendMesh(MeshPrimitiveBucket pb, SolidTessellator.TessellationResult t, Matrix3d transform)
        {
            var baseIdx = pb.Positions.Count / 3;
            var transformed = new List<float>(t.RawPositions.Count * 3);
            foreach (var p in t.RawPositions)
            {
                float x, y, z;
                DwgUnitConverter.ToGltfPoint(p.TransformBy(transform), _unitScale, out x, out y, out z);
                transformed.Add(x);
                transformed.Add(y);
                transformed.Add(z);

                pb.Positions.Add(x);
                pb.Positions.Add(y);
                pb.Positions.Add(z);
            }

            AppendNormals(pb.Normals, transformed, t.Indices);
            for (int i = 0; i < t.Indices.Count; i++) pb.Indices.Add(baseIdx + t.Indices[i]);
        }

        private void AppendPolyline(MeshPrimitiveBucket pb, List<Point3d> pts, Matrix3d transform)
        {
            var baseIdx = pb.Positions.Count / 3;
            for (int i = 0; i < pts.Count; i++)
            {
                float x, y, z;
                DwgUnitConverter.ToGltfPoint(pts[i].TransformBy(transform), _unitScale, out x, out y, out z);
                pb.Positions.Add(x);
                pb.Positions.Add(y);
                pb.Positions.Add(z);
            }
            for (int i = 0; i < pts.Count - 1; i++)
            {
                pb.Indices.Add(baseIdx + i);
                pb.Indices.Add(baseIdx + i + 1);
            }
        }

        private void AppendFilledSolid(MeshPrimitiveBucket pb, Solid solid, Matrix3d transform)
        {
            var p0 = solid.GetPointAt((short)0).TransformBy(transform);
            var p1 = solid.GetPointAt((short)1).TransformBy(transform);
            var p2 = solid.GetPointAt((short)2).TransformBy(transform);
            var p3 = solid.GetPointAt((short)3).TransformBy(transform);

            var faceNormal = (p1 - p0).CrossProduct(p2 - p0);
            if (faceNormal.Length <= 1e-12) faceNormal = Vector3d.ZAxis;
            else faceNormal = faceNormal.GetNormal();

            float nx, ny, nz;
            DwgUnitConverter.ToGltfNormal(faceNormal, out nx, out ny, out nz);

            var baseIdx = pb.Positions.Count / 3;
            PushVertex(pb, p0, nx, ny, nz);
            PushVertex(pb, p1, nx, ny, nz);
            PushVertex(pb, p2, nx, ny, nz);
            PushVertex(pb, p3, nx, ny, nz);

            pb.Indices.Add(baseIdx + 0); pb.Indices.Add(baseIdx + 1); pb.Indices.Add(baseIdx + 2);
            pb.Indices.Add(baseIdx + 1); pb.Indices.Add(baseIdx + 3); pb.Indices.Add(baseIdx + 2);
        }

        private void PushVertex(MeshPrimitiveBucket pb, Point3d point, float nx, float ny, float nz)
        {
            float x, y, z;
            DwgUnitConverter.ToGltfPoint(point, _unitScale, out x, out y, out z);
            pb.Positions.Add(x);
            pb.Positions.Add(y);
            pb.Positions.Add(z);
            pb.Normals.Add(nx);
            pb.Normals.Add(ny);
            pb.Normals.Add(nz);
        }

        private static void AppendNormals(List<float> target, List<float> positions, List<int> indices)
        {
            var sums = new double[(positions.Count / 3) * 3];
            for (int i = 0; i < indices.Count; i += 3)
            {
                var ia = indices[i];
                var ib = indices[i + 1];
                var ic = indices[i + 2];

                var a = ia * 3;
                var b = ib * 3;
                var c = ic * 3;

                var abx = positions[b + 0] - positions[a + 0];
                var aby = positions[b + 1] - positions[a + 1];
                var abz = positions[b + 2] - positions[a + 2];
                var acx = positions[c + 0] - positions[a + 0];
                var acy = positions[c + 1] - positions[a + 1];
                var acz = positions[c + 2] - positions[a + 2];

                var nx = aby * acz - abz * acy;
                var ny = abz * acx - abx * acz;
                var nz = abx * acy - aby * acx;

                AddNormal(sums, ia, nx, ny, nz);
                AddNormal(sums, ib, nx, ny, nz);
                AddNormal(sums, ic, nx, ny, nz);
            }

            for (int i = 0; i < sums.Length; i += 3)
            {
                var nx = sums[i + 0];
                var ny = sums[i + 1];
                var nz = sums[i + 2];
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len <= 1e-12)
                {
                    target.Add(0f);
                    target.Add(1f);
                    target.Add(0f);
                    continue;
                }
                target.Add((float)(nx / len));
                target.Add((float)(ny / len));
                target.Add((float)(nz / len));
            }
        }

        private static void AddNormal(double[] sums, int vertexIndex, double nx, double ny, double nz)
        {
            var baseIdx = vertexIndex * 3;
            sums[baseIdx + 0] += nx;
            sums[baseIdx + 1] += ny;
            sums[baseIdx + 2] += nz;
        }

        // ---------- helpers ----------

        private static (float[] min, float[] max) ComputeMinMax(List<float> positions)
        {
            if (positions.Count == 0)
            {
                return (new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 0f });
            }
            var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < positions.Count; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    var v = positions[i + k];
                    if (v < min[k]) min[k] = v;
                    if (v > max[k]) max[k] = v;
                }
            }
            return (min, max);
        }

        private static string SafeName(Entity e)
        {
            if (e == null) return "Entity";
            if (e is BlockReference br && br.Name != null) return "block_" + br.Name;
            return e.GetType().Name + "_" + e.Handle.Value.ToString("X");
        }

        // ---------- internal state ----------

        private struct RgbColor
        {
            public int R;
            public int G;
            public int B;

            public RgbColor(int r, int g, int b)
            {
                R = r;
                G = g;
                B = b;
            }
        }

        private struct ResolvedEntityState
        {
            public string LayerName;
            public RgbColor LayerColor;
            public RgbColor Color;

            public ResolvedEntityState(string layerName, RgbColor layerColor, RgbColor color)
            {
                LayerName = layerName;
                LayerColor = layerColor;
                Color = color;
            }
        }

        private sealed class EntityContext
        {
            public readonly string EffectiveLayerName;
            public readonly RgbColor EffectiveLayerColor;
            public readonly RgbColor InheritedColor;
            public readonly Matrix3d Transform;
            public readonly int BlockDepth;
            public readonly HashSet<string> BlockStack;

            public EntityContext(
                string effectiveLayerName,
                RgbColor effectiveLayerColor,
                RgbColor inheritedColor,
                Matrix3d transform,
                int blockDepth,
                HashSet<string> blockStack)
            {
                EffectiveLayerName = effectiveLayerName;
                EffectiveLayerColor = effectiveLayerColor;
                InheritedColor = inheritedColor;
                Transform = transform;
                BlockDepth = blockDepth;
                BlockStack = blockStack;
            }
        }

        private struct BlockAnalysis
        {
            public bool DependsOnInsertionColor;
            public bool DependsOnInsertionLayer;

            public static BlockAnalysis Dynamic => new BlockAnalysis
            {
                DependsOnInsertionColor = true,
                DependsOnInsertionLayer = true
            };
        }

        private struct ColorDependency
        {
            public bool FromParentColor;
            public bool FromParentLayer;
        }

        private class LayerBucket
        {
            public readonly List<MeshPrimitiveBucket> Triangles = new List<MeshPrimitiveBucket>();
            public readonly List<MeshPrimitiveBucket> Lines = new List<MeshPrimitiveBucket>();
            public readonly List<GltfNode> InstanceNodes = new List<GltfNode>();
            public readonly List<EntityExtras> PerEntityExtras = new List<EntityExtras>();
        }

        private class MeshBuckets
        {
            public readonly List<MeshPrimitiveBucket> Triangles = new List<MeshPrimitiveBucket>();
            public readonly List<MeshPrimitiveBucket> Lines = new List<MeshPrimitiveBucket>();
        }

        private class MeshPrimitiveBucket
        {
            public int Material;
            public readonly List<float> Positions = new List<float>();
            public readonly List<float> Normals = new List<float>();
            public readonly List<int> Indices = new List<int>();
        }

        private class EntityExtras
        {
            public Dictionary<string, object> Extras;
            public string EntityType;
        }
    }
}
