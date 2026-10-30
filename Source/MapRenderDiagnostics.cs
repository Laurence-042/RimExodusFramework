using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using Verse;
#if RIMWORLD_1_7
using Waylith.Rendering;
#endif

namespace RimExodus.Maps
{
    /// <summary>Bounded CPU trace with an opt-in source isolation experiment. No GPU result is inferred.</summary>
    public sealed class MapRenderDiagnostics
    {
#if RIMWORLD_1_7
        // Routing pointer only; capture state belongs to the runtime instance and Clear closes it on game change.
        [ThreadStatic] internal static MapRenderDiagnostics Capturing;
        private StreamWriter writer;
        private FileStream file;
        private readonly StringBuilder buffer = new StringBuilder();
        private readonly Dictionary<int, int> sourceCharacters = new Dictionary<int, int>();
        private readonly Dictionary<int, int> sourceOmissions = new Dictionary<int, int>();
        private readonly Dictionary<string, int> propertySets = new Dictionary<string, int>();
        private readonly HashSet<string> meshes = new HashSet<string>();
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        private DateTime deadline, nextSample;
        private Map previousFocus;
        private int sampleFrame = -1, samples, burst, commands, omitted;
        private long bytes;
        private readonly List<Map> sources = new List<Map>();
        private Map requestedSource, frameSource;
        public IReadOnlyList<Map> Sources => sources.AsReadOnly();
        public Map IsolatedSource => requestedSource;

        /// <summary>Null restores all sources. Applied at the next frame boundary; never changes callbacks or focus.</summary>
        public bool TrySetIsolatedSource(Map source)
        {
            if (!Active || (source != null && (source.Disposed || !sources.Contains(source)))) return false;
            requestedSource = source;
            return true;
        }

        internal void UpdateIsolation(IEnumerable<MapRenderPlan> plans)
        {
            sources.Clear();
            foreach (var plan in plans)
                if (plan?.Source != null && !plan.Source.Disposed && !sources.Contains(plan.Source)) sources.Add(plan.Source);
            if (requestedSource != null && !sources.Contains(requestedSource)) requestedSource = null;
            if (frameSource == requestedSource) return;
            frameSource = requestedSource;
            burst = 3;
            writer.WriteLine("ISOLATION utc=" + DateTime.UtcNow.ToString("O") + " source=" + MapId(frameSource)
                + " (-1=all); unscoped=preserved");
            Log.Message("[REF:RenderTrace] isolation source=" + MapId(frameSource) + " (-1=all); unscoped=preserved");
        }

        // Only commands with known scope ownership can be removed. Global/post-process draws must survive.
        internal bool ShouldSubmit(MapRenderPlan plan) => !Active || frameSource == null || plan == null
            || ReferenceEquals(plan.Source, frameSource);

        private void ResetIsolation() { requestedSource = frameSource = null; sources.Clear(); }
        // A busy first map must never consume the following map's diagnostic budget.
        private const int SourceLimit = 4 * 1024 * 1024;
        private const int SourceCountLimit = 16;
        private const long FileLimit = 32L * 1024 * 1024;
        private const long RawLimit = 512L * 1024 * 1024;
        public bool Active => writer != null;
        public string LastPath { get; private set; }

        /// <summary>60 seconds, one sample per second plus three consecutive frames after focus changes.
        /// Stops at 120 sampled frames or 32 MiB. Returns null on failure.</summary>
        public string StartCapture()
        {
            Capturing?.StopCapture();
            StopCapture();
            try
            {
                string directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "REF-RenderDiagnostics");
                Directory.CreateDirectory(directory);
                LastPath = Path.Combine(directory, "render-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log.gz");
                file = new FileStream(LastPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                writer = new StreamWriter(new GZipStream(file, CompressionLevel.Fastest), new UTF8Encoding(false));
                writer.WriteLine("REF CPU submission trace v3; UTC=" + DateTime.UtcNow.ToString("O")
                    + "; assembly=" + typeof(MapRenderDiagnostics).Assembly.Location
                    + "; mvid=" + typeof(MapRenderDiagnostics).Module.ModuleVersionId);
                writer.WriteLine("Observation: this REF Enqueue prefix before/after its own clip/freeze; not final GPU order/depth."
                    + " Unscoped commands have unknown source ownership. Texture IDs identify objects, not pixel contents."
                    + " Matrices use column-vector layout; bounds are mesh culling bounds, not exact referenced geometry.");
                writer.WriteLine("DRAW: seq | source(-1=unscoped) | role | shader | material ID/name | input mesh/sub | output mesh/sub | pass | layer | input/output queue | instancing ID | input matrix | output matrix | properties ID | disposition(submitted/clipped/isolated)");
                writer.Flush();
                samples = 0; bytes = 0; previousFocus = null; burst = 3;
                deadline = DateTime.UtcNow.AddSeconds(60); nextSample = DateTime.MinValue;
                Capturing = this;
                Log.Message("[REF:RenderTrace] started: " + LastPath);
                return LastPath;
            }
            catch (Exception error) { Fail(error); return null; }
        }

        public void StopCapture()
        {
            ResetIsolation();
            if (!Active) return;
            try
            {
                FlushFrame();
                writer.WriteLine("END samples=" + samples + " rawBytes=" + bytes + " compressedBytesBeforeEnd=" + (file?.Position ?? 0));
                writer.Dispose(); writer = null; file = null;
                Log.Message("[REF:RenderTrace] stopped: " + LastPath);
            }
            catch (Exception error) { Fail(error); }
            finally { ResetFrame(); previousFocus = null; if (Capturing == this) Capturing = null; }
        }

        internal void BeginFrame(Map focus, IEnumerable<MapRenderPlan> plans)
        {
            if (!Active) return;
            try
            {
                FlushFrame();
                var now = DateTime.UtcNow;
                if (now >= deadline || samples >= 120 || bytes >= RawLimit || file?.Position >= FileLimit) { StopCapture(); return; }
                UpdateIsolation(plans);
                if (previousFocus != focus) { previousFocus = focus; burst = 3; }
                if (burst <= 0 && now < nextSample) return;
                if (burst > 0) burst--;
                nextSample = now.AddSeconds(1);
                sampleFrame = Time.frameCount; samples++;
                buffer.AppendLine("FRAME " + sampleFrame + " utc=" + now.ToString("O") + " focus=" + MapId(focus)
                    + " isolatedSource=" + MapId(frameSource));
                var camera = Find.Camera;
                if (camera != null)
                    buffer.AppendLine("CAMERA view=" + Matrix4x4.ToNumerics(camera.worldToCameraMatrix).ToString()
                        + " projection=" + Matrix4x4.ToNumerics(camera.projectionMatrix).ToString()
                        + " pixels=" + camera.pixelWidth + "x" + camera.pixelHeight);
                foreach (var plan in plans)
                {
                    buffer.AppendLine("PLAN source=" + MapId(plan.Source) + " size=" + plan.Source.Size
                        + " view=" + plan.LocalView + " matrix=" + Matrix4x4.ToNumerics(plan.Transform.Matrix));
                    if (plan.OverlayFootprint != null)
                        foreach (var point in plan.OverlayFootprint) buffer.AppendLine("FOOTPRINT " + MapId(plan.Source) + " " + point);
                }
            }
            catch (Exception error) { Fail(error); }
        }

        internal bool Sampling => Active && sampleFrame == Time.frameCount;

        internal void Observe(MapRenderPlan plan, in MeshDrawCommand input, in MeshDrawCommand output, bool isolationSuppressed = false)
        {
            if (!Sampling) return;
            try
            {
                int source = MapId(plan?.Source);
                string shader = input.Material?.Shader?.Name ?? "null";
                string group = source + "|" + shader + "|q=" + Queue(input) + "|pass=" + input.PassId
                    + "|clip=" + (output.Mesh == null ? "dropped" : ReferenceEquals(input.Mesh, output.Mesh) ? "unchanged" : "replaced")
                    + "|isolation=" + (isolationSuppressed ? "suppressed" : "retained");
                counts.TryGetValue(group, out int count); counts[group] = count + 1;
                commands++;
                sourceCharacters.TryGetValue(source, out int used);
                if (used >= SourceLimit || (!sourceCharacters.ContainsKey(source) && sourceCharacters.Count >= SourceCountLimit))
                {
                    omitted++;
                    sourceOmissions.TryGetValue(source, out int skipped); sourceOmissions[source] = skipped + 1;
                    return;
                }
                int before = buffer.Length;
                DescribeMesh(input); DescribeMesh(output);
                string properties = Properties(output);
                if (!propertySets.TryGetValue(properties, out int propertiesId))
                {
                    propertiesId = propertySets.Count;
                    propertySets.Add(properties, propertiesId);
                    buffer.AppendLine("PROPERTIES " + propertiesId + " " + properties);
                }
                buffer.Append("DRAW ").Append(commands).Append('|').Append(source).Append('|')
                    .Append(plan == null ? "unscoped" : plan.Source == previousFocus ? "focus" : "neighbor")
                    .Append('|').Append(shader).Append('|').Append(Id(input.Material)).Append(':').Append(input.Material?.DebugName)
                    .Append('|').Append(MeshKey(input)).Append('|').Append(MeshKey(output))
                    .Append('|').Append(output.PassId).Append('|').Append(output.Layer)
                    .Append('|').Append(Queue(input)).Append('/').Append(Queue(output)).Append('|').Append(Id(output.Instancing))
                    .Append('|').Append(input.Matrix).Append('|').Append(output.Matrix).Append('|').Append(propertiesId)
                    .Append('|').Append(isolationSuppressed ? "isolated" : output.Mesh == null ? "clipped" : "submitted").AppendLine();
                sourceCharacters[source] = used + buffer.Length - before;
            }
            catch (Exception error) { Fail(error); }
        }

        private void DescribeMesh(in MeshDrawCommand command)
        {
            var mesh = command.Mesh;
            string key = MeshKey(command);
            if (mesh == null || !meshes.Add(key)) return;
            buffer.Append("MESH ").Append(key).Append(" name=").Append(mesh.DebugName)
                .Append(" boundsCenter=").Append(mesh.Bounds.Center).Append(" boundsSize=").Append(mesh.Bounds.Size);
            if (mesh.IsReadable)
            {
                var data = mesh.CpuStorage;
                buffer.Append(" vertices=").Append(data.VertexCount);
                if (command.SubmeshIndex >= 0 && command.SubmeshIndex < data.SubMeshes.Length)
                {
                    var sub = data.SubMeshes[command.SubmeshIndex];
                    buffer.Append(" indexStart=").Append(sub.IndexStart).Append(" indexCount=").Append(sub.IndexCount)
                        .Append(" baseVertex=").Append(sub.BaseVertex).Append(" topology=").Append(sub.Topology);
                }
            }
            buffer.AppendLine();
        }

        // Include declared globals, authored-default flags, material values and final overrides separately.
        // This is observation only: do not call Freeze again, mutate material state, or keep arena ranges across frames.
        private static string Properties(in MeshDrawCommand command)
        {
            var text = new StringBuilder();
            var layout = command.Material?.Shader?.MaterialLayout;
            if (layout != null)
            {
                foreach (var slot in layout.UniformSlots)
                {
                    text.Append("G[").Append(slot.PropertyId).Append(':').Append(PropertyName(slot.PropertyId)).Append(':').Append(slot.Type)
                        .Append(slot.HasAuthoredDefault ? ":authored]=" : "]=");
                    if (GlobalShaderState.TryGetFloat(slot.PropertyId, out var f)) text.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    else if (GlobalShaderState.TryGetVector(slot.PropertyId, out var v)) text.Append(v);
                    else if (GlobalShaderState.TryGetMatrix(slot.PropertyId, out var m)) text.Append(m);
                    else text.Append("unset");
                    text.Append(';');
                }
                foreach (var slot in layout.TextureSlots)
                {
                    GlobalShaderState.TryGetTexture(slot.PropertyId, out var texture);
                    text.Append("GT[").Append(slot.PropertyId).Append(':').Append(PropertyName(slot.PropertyId)).Append("]=").Append(Id(texture)).Append(';');
                }
            }
            if (command.Material != null)
                foreach (var value in command.Material.PropertiesForReading) Property(text, "M", value);
            foreach (var value in MeshDrawSubmission.ActiveArena.GetProperties(command.PropertyRange)) Property(text, "P", value);
            return text.ToString();
        }

        private static void Property(StringBuilder text, string origin, DrawProperty value)
        {
            text.Append(origin).Append('[').Append(value.PropertyId).Append(':').Append(value.Type).Append("]=");
            switch (value.Type)
            {
                case DrawPropertyType.Float: text.Append(value.FloatValue.ToString("R", CultureInfo.InvariantCulture)); break;
                case DrawPropertyType.Int: text.Append(value.IntValue); break;
                case DrawPropertyType.Vector:
                case DrawPropertyType.Color: text.Append(value.VectorValue); break;
                case DrawPropertyType.Matrix: text.Append(value.MatrixValue); break;
                default: text.Append("object#").Append(Id(value.ReferenceValue)); break;
            }
            text.Append(';');
        }
        private static int Id(object value) => value == null ? 0 : RuntimeHelpers.GetHashCode(value);
        private static string PropertyName(int id) => ShaderPropertyId.TryGetName(id, out var name) ? name : "?";
        private static int MapId(Map map) => map?.uniqueID ?? -1;
        private static int Queue(in MeshDrawCommand command) => command.RenderQueueOverride > 0
            ? command.RenderQueueOverride : command.Material?.RenderQueue ?? 0;
        private static string MeshKey(in MeshDrawCommand command) => Id(command.Mesh) + ":v" + command.Mesh?.Version + ":s" + command.SubmeshIndex;

        private void FlushFrame()
        {
            if (sampleFrame < 0) return;
            buffer.AppendLine("COUNTS commands=" + commands + " detailOmitted=" + omitted);
            foreach (var pair in sourceCharacters)
            {
                sourceOmissions.TryGetValue(pair.Key, out int skipped);
                buffer.AppendLine("SOURCE_BUDGET source=" + pair.Key + " characters=" + pair.Value + " detailOmitted=" + skipped);
            }
            foreach (var pair in sourceOmissions)
                if (!sourceCharacters.ContainsKey(pair.Key)) buffer.AppendLine("SOURCE_LIMIT source=" + pair.Key + " detailOmitted=" + pair.Value);
            foreach (var pair in counts) buffer.AppendLine("GROUP " + pair.Key + "=" + pair.Value);
            string content = buffer.ToString();
            int length = Encoding.UTF8.GetByteCount(content);
            if (bytes + length <= RawLimit) { writer.Write(content); bytes += length; }
            else { writer.WriteLine("RAW_LIMIT: last sample omitted"); bytes = RawLimit; }
            writer.Flush();
            ResetFrame();
        }
        private void ResetFrame()
        {
            sampleFrame = -1; commands = omitted = 0; buffer.Clear();
            propertySets.Clear(); meshes.Clear(); counts.Clear();
            sourceCharacters.Clear(); sourceOmissions.Clear();
        }
        private void Fail(Exception error)
        {
            try { writer?.Dispose(); } catch { }
            try { file?.Dispose(); } catch { }
            file = null;
            writer = null; ResetFrame(); previousFocus = null;
            ResetIsolation();
            if (Capturing == this) Capturing = null;
            Log.Warning("[REF:RenderTrace] capture stopped after diagnostic failure; normal submission restored: " + error);
        }
#else
        public bool Active => false;
        public string LastPath => null;
        public string StartCapture() => null;
        public void StopCapture() { }
#endif
    }
}
