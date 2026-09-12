using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class UiPreviewProvider : IToolProvider
    {
        public string Namespace => "ui";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "ui.preview_screenshot",
                Description = "Render a UI Toolkit UXML asset offscreen in Edit Mode and return an inline PNG. Uses Unity's VisualTreeAsset preview renderer first, then falls back to an offscreen PanelSettings target. Args: { uxmlPath: string, styleSheetPaths?: string[], panelSettingsPath?: string, width?: int, height?: int, waitFrames?: int }.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Async,
                RequiresMainThread = true,
                Timeout = TimeSpan.FromSeconds(10),
                ExclusiveGroup = "ui.preview",
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""uxmlPath""],
                    ""properties"": {
                        ""uxmlPath"": { ""type"": ""string"", ""description"": ""Project-relative .uxml path under Assets/ or Packages/."" },
                        ""styleSheetPaths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional extra USS paths used by the fallback offscreen panel. Unity's primary VisualTreeAsset preview uses styles referenced by the UXML asset."" },
                        ""panelSettingsPath"": { ""type"": ""string"", ""description"": ""Optional PanelSettings asset used by the fallback offscreen panel."" },
                        ""width"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""height"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""waitFrames"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 10 }
                    }
                }"),
                Handler = Capture,
            });
        }

        private static async ValueTask<ToolResult> Capture(ToolContext ctx, CancellationToken ct)
        {
            var width = ctx.Arguments["width"]?.Value<int>() ?? 1280;
            var height = ctx.Arguments["height"]?.Value<int>() ?? 720;
            var waitFrames = ctx.Arguments["waitFrames"]?.Value<int>() ?? 2;
            var (uxmlPath, tree, styleSheets, panelSettings) = LoadAssets(ctx);

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new McpToolException(McpErrorCodes.ToolUnavailable, "ui.preview_screenshot requires a graphics device.");

            GameObject host = null;
            PanelSettings settings = null;
            RenderTexture target = null;
            try
            {
                ToolResult previewResult;
                string previewError = null;
                if (styleSheets.Length == 0 && panelSettings == null &&
                    TryCaptureVisualTreeAssetPreview(tree, uxmlPath, width, height, 0, false, out previewResult, out previewError))
                    return previewResult;

                (host, settings, target) = CreatePreviewPanel(tree, styleSheets, panelSettings, width, height);
                await ctx.Frames.DelayFramesAsync(waitFrames, ct);
                return await ctx.MainThread.RunAsync(_ => new ValueTask<ToolResult>(CaptureTarget(target, uxmlPath, width, height, previewError)), ct);
            }
            finally
            {
                await ctx.MainThread.RunAsync(_ =>
                {
                    Cleanup(host, settings, target);
                    return default;
                }, CancellationToken.None);
            }
        }

        private static (string uxmlPath, VisualTreeAsset tree, StyleSheet[] styleSheets, PanelSettings panelSettings) LoadAssets(ToolContext ctx)
        {
            var uxmlPath = AssetProvider.NormalizeAssetPath(RequireString(ctx, "uxmlPath"));
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (tree == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"UXML asset not found: '{uxmlPath}'.");

            var styleSheets = Array.Empty<StyleSheet>();
            if (ctx.Arguments["styleSheetPaths"] is JArray arr && arr.Count > 0)
            {
                styleSheets = new StyleSheet[arr.Count];
                for (var i = 0; i < arr.Count; i++)
                {
                    var path = AssetProvider.NormalizeAssetPath((string)arr[i]);
                    var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                    if (sheet == null)
                        throw new McpToolException(McpErrorCodes.NotFound, $"StyleSheet asset not found: '{path}'.");
                    styleSheets[i] = sheet;
                }
            }

            PanelSettings panelSettings = null;
            var panelSettingsArg = (string)ctx.Arguments["panelSettingsPath"];
            if (!string.IsNullOrWhiteSpace(panelSettingsArg))
            {
                var path = AssetProvider.NormalizeAssetPath(panelSettingsArg);
                panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
                if (panelSettings == null)
                    throw new McpToolException(McpErrorCodes.NotFound, $"PanelSettings asset not found: '{path}'.");
            }

            return (uxmlPath, tree, styleSheets, panelSettings);
        }

        private static (GameObject host, PanelSettings settings, RenderTexture target) CreatePreviewPanel(
            VisualTreeAsset tree,
            StyleSheet[] styleSheets,
            PanelSettings sourceSettings,
            int width,
            int height)
        {
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "MCP UI Preview Target",
                hideFlags = HideFlags.HideAndDontSave,
            };
            if (!target.Create())
                throw new McpToolException(McpErrorCodes.ToolError, "Failed to create UI preview render texture.");

            var settings = sourceSettings != null
                ? UnityEngine.Object.Instantiate(sourceSettings)
                : ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = "MCP UI Preview PanelSettings";
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = target;
            settings.renderMode = PanelRenderMode.ScreenSpaceOverlay;
            if (sourceSettings == null)
            {
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                settings.scale = 1f;
            }
            settings.clearColor = true;
            settings.colorClearValue = Color.clear;
            settings.clearDepthStencil = true;
            if (sourceSettings == null)
                settings.referenceResolution = new Vector2Int(width, height);

            var host = EditorUtility.CreateGameObjectWithHideFlags("MCP UI Preview", HideFlags.HideAndDontSave, typeof(UIDocument));
            var doc = host.GetComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = tree;

            var root = doc.rootVisualElement;
            if (root == null)
                throw new McpToolException(McpErrorCodes.ToolError, "Failed to create UI preview root visual element.");

            root.style.width = width;
            root.style.height = height;
            root.style.flexGrow = 1;
            root.style.overflow = Overflow.Hidden;
            foreach (var sheet in styleSheets)
                root.styleSheets.Add(sheet);

            root.MarkDirtyRepaint();
            EditorApplication.QueuePlayerLoopUpdate();
            PumpRuntimePanels();
            MarkBackgroundTexturesDirty(root);
            root.MarkDirtyRepaint();
            PumpRuntimePanels();

            return (host, settings, target);
        }

        private static bool TryCaptureVisualTreeAssetPreview(
            VisualTreeAsset tree,
            string uxmlPath,
            int width,
            int height,
            int externalStyleSheetCount,
            bool hasPanelSettings,
            out ToolResult result,
            out string error)
        {
            result = null;
            error = null;

            UnityEditor.Editor editor = null;
            Texture2D tex = null;
            try
            {
                editor = UnityEditor.Editor.CreateEditor(tree);
                if (editor == null)
                {
                    error = "UnityEditor.Editor.CreateEditor returned null.";
                    return false;
                }

                var editorType = editor.GetType();
                // ponytail: Unity exposes arbitrary-size UXML previews through this editor internals path.
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                editorType.GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(editor, null);

                var updatePreview = editorType.GetMethod("UpdatePreviewTexture", instanceFlags);
                var previewTextureField = editorType.GetField("m_preview_texture", instanceFlags);
                if (updatePreview == null || previewTextureField == null)
                {
                    error = "Unity VisualTreeAsset preview internals were not found.";
                    return false;
                }

                var updated = updatePreview.Invoke(editor, new object[] { width, height }) is bool value && value;
                var target = previewTextureField.GetValue(editor) as RenderTexture;
                if (!updated || target == null)
                {
                    error = "Unity VisualTreeAsset preview renderer did not produce a render texture.";
                    return false;
                }

                var previous = RenderTexture.active;
                tex = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = target;
                    tex.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    tex.Apply();
                }
                finally
                {
                    RenderTexture.active = previous;
                }

                var png = tex.EncodeToPNG();
                var structured = new JObject
                {
                    ["source"] = "UnityVisualTreeAssetEditor",
                    ["uxmlPath"] = uxmlPath,
                    ["width"] = target.width,
                    ["height"] = target.height,
                    ["sizeBytes"] = png.Length,
                    ["visibleWindow"] = false,
                    ["playMode"] = false,
                };
                if (externalStyleSheetCount > 0)
                {
                    structured["externalStyleSheetPathsApplied"] = false;
                    structured["styleSource"] = "Unity's VisualTreeAsset preview renderer uses styles referenced by the UXML asset.";
                }
                if (hasPanelSettings)
                    structured["panelSettingsApplied"] = false;

                result = ToolResult.Ok(structured, new ImageContent { Data = png, MimeType = "image/png" });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (tex != null)
                    UnityEngine.Object.DestroyImmediate(tex);
                if (editor != null)
                    UnityEngine.Object.DestroyImmediate(editor);
            }
        }

        private static ToolResult CaptureTarget(RenderTexture target, string uxmlPath, int width, int height, string primaryError)
        {
            if (target == null)
                throw new McpToolException(McpErrorCodes.ToolError, "Preview render texture was not created.");

            var previous = RenderTexture.active;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                PumpRuntimePanels();
                RenderTexture.active = target;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                var structured = new JObject
                {
                    ["source"] = "UiPreviewRenderTexture",
                    ["uxmlPath"] = uxmlPath,
                    ["width"] = width,
                    ["height"] = height,
                    ["sizeBytes"] = png.Length,
                };
                if (!string.IsNullOrEmpty(primaryError))
                    structured["primaryPreviewError"] = primaryError;
                return ToolResult.Ok(structured, new ImageContent { Data = png, MimeType = "image/png" });
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static void PumpRuntimePanels()
        {
            var utility = typeof(PanelSettings).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
            // ponytail: Unity 6.4 has public offscreen panel targets but no public edit-mode render tick.
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            utility?.GetMethod("UpdatePanels", flags)?.Invoke(null, null);
            utility?.GetMethod("RepaintPanels", flags)?.Invoke(null, new object[] { true });
            utility?.GetMethod("RenderOffscreenPanels", flags)?.Invoke(null, null);
        }

        private static void MarkBackgroundTexturesDirty(VisualElement root)
        {
            var panel = root?.panel;
            if (panel == null)
                return;

            MarkBackgroundTexturesDirtyRecursive(root, panel);
        }

        private static void MarkBackgroundTexturesDirtyRecursive(VisualElement element, IPanel panel)
        {
            var background = element.resolvedStyle.backgroundImage;
            if (background.texture != null)
                SetTextureDirty(panel, background.texture);
            if (background.sprite != null && background.sprite.texture != null)
                SetTextureDirty(panel, background.sprite.texture);

            for (var i = 0; i < element.childCount; i++)
                MarkBackgroundTexturesDirtyRecursive(element[i], panel);
        }

        private static void SetTextureDirty(IPanel panel, Texture2D texture)
        {
            var utility = typeof(PanelSettings).Assembly.GetType("UnityEngine.UIElements.RuntimePanelUtils");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            utility?.GetMethod("SetTextureDirty", flags)?.Invoke(null, new object[] { panel, texture });
        }

        private static string RequireString(ToolContext ctx, string key)
        {
            var value = (string)ctx.Arguments[key];
            if (string.IsNullOrWhiteSpace(value))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"{key}: non-empty string required.");
            return value;
        }

        private static void Cleanup(GameObject host, PanelSettings settings, RenderTexture target)
        {
            if (host != null)
                UnityEngine.Object.DestroyImmediate(host);
            if (settings != null)
                UnityEngine.Object.DestroyImmediate(settings);
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
