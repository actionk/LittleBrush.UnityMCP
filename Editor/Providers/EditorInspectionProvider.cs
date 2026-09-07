using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class EditorInspectionProvider : IToolProvider
    {
        public string Namespace => "editor";
        private static JObject PageSchema() => JObject.Parse(@"{'type':'object','additionalProperties':false,'properties':{'offset':{'type':'integer','minimum':0},'limit':{'type':'integer','minimum':1,'maximum':100}}}");

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(Read("editor.selection.read", "Read selected object identities without changing selection or focus. Paged; entity IDs are valid only in this Editor session.", SelectionRead));
            reg.Register(Read("editor.windows.list", "List open Editor windows with type, title, bounds and entity ID. Does not open or focus windows. A listed window may be hidden behind another tab.", WindowsRead));
            reg.Register(Read("editor.scene_view.read", "Read existing Scene view camera framing: pivot, rotation, zoom, projection and 2D mode. Does not move or focus views.", ViewsRead));
            reg.Register(new ToolDescriptor
            {
                Name = "editor.selection.set",
                Description = "Select existing objects by session-local entity IDs without focusing or pinging a window. Empty array clears selection. Validates all IDs before changing selection.",
                TrustCategory = ToolTrustCategory.EditorState,
                InputSchema = JObject.Parse(@"{'type':'object','required':['entityIds'],'additionalProperties':false,'properties':{'entityIds':{'type':'array','maxItems':100,'uniqueItems':true,'items':{'type':'string'}}}}"),
                Handler = SetSelection,
            });
        }

        private static ToolDescriptor Read(string name, string description, System.Func<ToolContext, CancellationToken, ValueTask<ToolResult>> handler) => new()
        {
            Name = name, Description = description, InputSchema = PageSchema(), Handler = handler,
            RequiresWriterLease = false, ReloadSafe = true, TrustCategory = ToolTrustCategory.Read,
            Annotations = new JObject { ["readOnlyHint"] = true },
        };

        private static JObject Identity(Object value) => new()
        {
            ["entityId"] = UnitySerializer.ToEntityIdString(value), ["name"] = value.name,
            ["type"] = value.GetType().FullName,
        };

        private static ToolResult Page<T>(T[] values, JObject args, System.Func<T, JObject> convert)
        {
            var offset = args.Value<int?>("offset") ?? 0;
            var limit = args.Value<int?>("limit") ?? 25;
            var items = new JArray(values.Skip(offset).Take(limit).Select(convert));
            return ToolResult.Ok(new JObject { ["total"] = values.Length, ["offset"] = offset,
                ["items"] = items, ["truncated"] = offset + items.Count < values.Length });
        }

        private static ValueTask<ToolResult> SelectionRead(ToolContext ctx, CancellationToken ct)
            => new(Page(Selection.objects, ctx.Arguments, Identity));

        private static ValueTask<ToolResult> WindowsRead(ToolContext ctx, CancellationToken ct)
            => new(Page(Resources.FindObjectsOfTypeAll<EditorWindow>(), ctx.Arguments, window =>
            {
                var item = Identity(window); var bounds = window.position;
                item["title"] = window.titleContent.text;
                item["bounds"] = new JArray(bounds.x, bounds.y, bounds.width, bounds.height);
                return item;
            }));

        private static ValueTask<ToolResult> ViewsRead(ToolContext ctx, CancellationToken ct)
            => new(Page(SceneView.sceneViews.Cast<SceneView>().ToArray(), ctx.Arguments, view =>
            {
                var item = Identity(view); var p = view.pivot; var r = view.rotation;
                item["pivot"] = new JArray(p.x, p.y, p.z);
                item["rotation"] = new JArray(r.x, r.y, r.z, r.w);
                item["size"] = view.size; item["orthographic"] = view.orthographic;
                item["in2DMode"] = view.in2DMode; return item;
            }));

        private static ValueTask<ToolResult> SetSelection(ToolContext ctx, CancellationToken ct)
        {
            var objects = ((JArray)ctx.Arguments["entityIds"]).Select(token =>
            {
                if (!UnitySerializer.TryParseEntityId(token, out var id))
                    throw new McpToolException(McpErrorCodes.InvalidParams, "Invalid entity ID.");
                return EditorUtility.EntityIdToObject(id) ?? throw new McpToolException(McpErrorCodes.NotFound, "Selected object no longer exists; read its current identity.");
            }).ToArray();
            ct.ThrowIfCancellationRequested();
            Selection.objects = objects;
            return new(ToolResult.Ok(new JObject { ["selectedCount"] = objects.Length }));
        }
    }
}
