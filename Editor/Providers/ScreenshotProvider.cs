using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class ScreenshotProvider : IToolProvider
    {
        public string Namespace => "editor.screenshot";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "editor.screenshot",
                Description = "Capture a screenshot of the SceneView (edit/play) or GameView (play mode). Returns inline PNG via ImageContent. Args: { source?: 'SceneView'|'GameView', width?: int, height?: int }.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                RequiresMainThread = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""source"": { ""enum"": [""SceneView"", ""GameView""] },
                        ""width"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""height"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 }
                    }
                }"),
                Handler = CaptureScreenshot,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "editor.window_screenshot",
                Description = "Capture a visible Unity EditorWindow by type name or title. Captures the full window by default; region optionally crops in window-local logical coordinates from the top-left. Background capture reuses an already open window. Creating a temporary floating window requires activate=true and can steal focus. Returns inline PNG and logical/physical bounds. Args: { windowType?: string, title?: string, openIfNeeded?: bool, waitFrames?: int, floating?: bool, activate?: bool, width?: int, height?: int, region?: { x, y, width, height } }.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Async,
                RequiresMainThread = false,
                Timeout = TimeSpan.FromSeconds(10),
                ExclusiveGroup = "screenshot",
                TrustCategory = ToolTrustCategory.Read,
                TrustCategoryResolver = ResolveWindowScreenshotTrust,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""windowType"": { ""type"": ""string"", ""description"": ""Full or short EditorWindow type name, e.g. LittleBrushGames.Mcp.Editor.Diagnostics.LbgPluginsWindow."" },
                        ""title"": { ""type"": ""string"", ""description"": ""Fallback title text to find among open Editor windows."" },
                        ""openIfNeeded"": { ""type"": ""boolean"" },
                        ""waitFrames"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 10 },
                        ""floating"": { ""type"": ""boolean"", ""description"": ""Create a temporary floating window only when no matching window is already open."" },
                        ""activate"": { ""type"": ""boolean"", ""description"": ""Activate and focus the target before capture. This steals user focus and is governed as EditorState; omit it unless foreground interaction is explicitly required. Default false."" },
                        ""width"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""height"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""region"": {
                            ""type"": ""object"",
                            ""description"": ""Optional crop in window-local logical coordinates, with (0,0) at the window's top-left."",
                            ""required"": [""x"", ""y"", ""width"", ""height""],
                            ""additionalProperties"": false,
                            ""properties"": {
                                ""x"": { ""type"": ""number"", ""minimum"": 0 },
                                ""y"": { ""type"": ""number"", ""minimum"": 0 },
                                ""width"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
                                ""height"": { ""type"": ""number"", ""exclusiveMinimum"": 0 }
                            }
                        }
                    }
                }"),
                Handler = CaptureWindowScreenshot,
            });
        }

        internal static ToolTrustCategory ResolveWindowScreenshotTrust(JObject arguments) =>
            arguments?["activate"]?.Value<bool>() == true
                ? ToolTrustCategory.EditorState
                : ToolTrustCategory.Read;

        private static ValueTask<ToolResult> CaptureScreenshot(ToolContext ctx, CancellationToken ct)
        {
            var source = (string)ctx.Arguments["source"] ?? "SceneView";
            var requestedWidth = ctx.Arguments["width"]?.Value<int>() ?? 0;
            var requestedHeight = ctx.Arguments["height"]?.Value<int>() ?? 0;

            byte[] pngBytes;
            int width, height;

            switch (source)
            {
                case "SceneView":
                    (pngBytes, width, height) = CaptureSceneView(requestedWidth, requestedHeight);
                    break;
                case "GameView":
                    (pngBytes, width, height) = CaptureGameView(requestedWidth, requestedHeight);
                    break;
                default:
                    throw new McpToolException(McpErrorCodes.InvalidParams, $"Unknown source '{source}'. Use 'SceneView' or 'GameView'.");
            }

            var structured = new JObject
            {
                ["source"] = source,
                ["width"] = width,
                ["height"] = height,
                ["sizeBytes"] = pngBytes.Length,
            };
            var image = new ImageContent { Data = pngBytes, MimeType = "image/png" };
            return new ValueTask<ToolResult>(ToolResult.Ok(structured, image));
        }

        private static async ValueTask<ToolResult> CaptureWindowScreenshot(ToolContext ctx, CancellationToken ct)
        {
            var windowTypeName = (string)ctx.Arguments["windowType"];
            var title = (string)ctx.Arguments["title"];
            var openIfNeeded = ctx.Arguments["openIfNeeded"]?.Value<bool>() ?? true;
            var waitFrames = ctx.Arguments["waitFrames"]?.Value<int>() ?? 2;
            var floating = ctx.Arguments["floating"]?.Value<bool>() ?? false;
            var activate = ctx.Arguments["activate"]?.Value<bool>() ?? false;
            var requestedWidth = ctx.Arguments["width"]?.Value<int>() ?? 0;
            var requestedHeight = ctx.Arguments["height"]?.Value<int>() ?? 0;

            var target = await ctx.MainThread.RunAsync(_ =>
            {
                var prepared = PrepareWindow(windowTypeName, title, openIfNeeded, floating, activate, requestedWidth, requestedHeight);
                return new ValueTask<WindowCaptureTarget>(prepared);
            }, ct);

            try
            {
                if (waitFrames > 0)
                    await ctx.Frames.DelayFramesAsync(waitFrames, ct);

                return await ctx.MainThread.RunAsync(
                    _ => new ValueTask<ToolResult>(CapturePreparedWindow(target, ctx.Arguments["region"] as JObject)),
                    ct);
            }
            finally
            {
                if (target.CloseAfterCapture && target.Window != null)
                {
                    await ctx.MainThread.RunAsync(_ =>
                    {
                        target.Window.Close();
                        return new ValueTask();
                    }, CancellationToken.None);
                }
            }
        }

        private static WindowCaptureTarget PrepareWindow(
            string windowTypeName,
            string title,
            bool openIfNeeded,
            bool floating,
            bool activate,
            int requestedWidth,
            int requestedHeight)
        {
            var target = !activate && floating
                ? new WindowCaptureTarget(FindEditorWindow(windowTypeName, title, false), false, false)
                : floating
                ? CreateFloatingWindow(windowTypeName, title, requestedWidth, requestedHeight)
                : new WindowCaptureTarget(FindEditorWindow(windowTypeName, title, activate && openIfNeeded), false, activate);
            var window = target.Window;

            if (target.CloseAfterCapture)
                window.ShowUtility();
            else if (target.Activate)
                window.Show();
            if (target.Activate)
            {
                window.Focus();
                BringUnityToFront();
            }
            window.Repaint();
            InternalEditorUtility.RepaintAllViews();
            EditorApplication.QueuePlayerLoopUpdate();
            return target;
        }

        private static ToolResult CapturePreparedWindow(WindowCaptureTarget target, JObject region)
        {
            var window = target.Window;
            var logicalWindowRect = window.position;
            var pixelsPerPoint = Application.platform == RuntimePlatform.WindowsEditor
                ? Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint)
                : 1f;
            var boundsSource = "EditorWindow.position";
            var windowTitle = window.titleContent?.text;
            var handle = IntPtr.Zero;
            if (Application.platform == RuntimePlatform.WindowsEditor &&
                TryGetWindowsWindowRect(windowTitle, out handle, out var hwndRect))
            {
                if (target.Activate)
                    SetForegroundWindow(handle);
                boundsSource = "Win32.GetWindowRect";
            }
            else
            {
                // ponytail: docked windows fall back to screen pixels; use an offscreen UI render API if Unity exposes one.
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    hwndRect = EditorRectToWindowsDesktopRect(logicalWindowRect, pixelsPerPoint);
                    boundsSource = "EditorWindow.position * pixelsPerPoint";
                }
                else
                {
                    var editorRect = window.position;
                    hwndRect = new DesktopRect(
                        Mathf.RoundToInt(editorRect.x),
                        Mathf.RoundToInt(editorRect.y),
                        Mathf.RoundToInt(editorRect.width),
                        Mathf.RoundToInt(editorRect.height));
                }
            }

            var localCaptureRect = ResolveLocalCaptureRect(hwndRect, region, pixelsPerPoint);
            var captureRect = new DesktopRect(
                hwndRect.X + localCaptureRect.X,
                hwndRect.Y + localCaptureRect.Y,
                localCaptureRect.Width,
                localCaptureRect.Height);
            var x = captureRect.X;
            var y = captureRect.Y;
            var w = Mathf.Max(1, captureRect.Width);
            var h = Mathf.Max(1, captureRect.Height);

            if (w <= 1 || h <= 1)
                throw new McpToolException(McpErrorCodes.ToolError, $"Invalid window size: {w}x{h}.");

            var pngBytes = Application.platform == RuntimePlatform.WindowsEditor && handle != IntPtr.Zero
                ? CropPngIfNeeded(
                    CaptureWindowsWindow(handle, hwndRect.Width, hwndRect.Height),
                    hwndRect.Width,
                    hwndRect.Height,
                    localCaptureRect)
                : Application.platform == RuntimePlatform.WindowsEditor
                ? CaptureWindowsDesktopRect(x, y, w, h)
                : CaptureScreenRect(x, y, w, h);
            var structured = new JObject
            {
                ["source"] = "EditorWindow",
                ["windowType"] = window.GetType().FullName,
                ["title"] = windowTitle,
                ["x"] = x,
                ["y"] = y,
                ["width"] = w,
                ["height"] = h,
                ["sizeBytes"] = pngBytes.Length,
                ["floating"] = target.CloseAfterCapture,
                ["boundsSource"] = boundsSource,
                ["pixelsPerPoint"] = pixelsPerPoint,
                ["windowBoundsLogical"] = BoundsJson(
                    logicalWindowRect.x,
                    logicalWindowRect.y,
                    logicalWindowRect.width,
                    logicalWindowRect.height),
                ["windowBoundsPhysical"] = BoundsJson(
                    hwndRect.X,
                    hwndRect.Y,
                    hwndRect.Width,
                    hwndRect.Height),
                ["captureBoundsPhysical"] = BoundsJson(x, y, w, h),
                ["regionLocalLogical"] = region == null
                    ? BoundsJson(0f, 0f, logicalWindowRect.width, logicalWindowRect.height)
                    : BoundsJson(
                        region["x"].Value<float>(),
                        region["y"].Value<float>(),
                        region["width"].Value<float>(),
                        region["height"].Value<float>()),
            };
            return ToolResult.Ok(structured, new ImageContent { Data = pngBytes, MimeType = "image/png" });
        }

        private static DesktopRect EditorRectToWindowsDesktopRect(Rect rect, float pixelsPerPoint)
        {
            var scale = Mathf.Max(1f, pixelsPerPoint);
            return new DesktopRect(
                Mathf.RoundToInt(rect.x * scale),
                Mathf.RoundToInt(rect.y * scale),
                Mathf.RoundToInt(rect.width * scale),
                Mathf.RoundToInt(rect.height * scale));
        }

        private static DesktopRect ResolveLocalCaptureRect(
            DesktopRect windowRect,
            JObject region,
            float pixelsPerPoint)
        {
            if (region == null)
                return new DesktopRect(0, 0, windowRect.Width, windowRect.Height);

            var scale = Mathf.Max(1f, pixelsPerPoint);
            var local = new DesktopRect(
                Mathf.RoundToInt(region["x"].Value<float>() * scale),
                Mathf.RoundToInt(region["y"].Value<float>() * scale),
                Mathf.RoundToInt(region["width"].Value<float>() * scale),
                Mathf.RoundToInt(region["height"].Value<float>() * scale));
            if (local.X < 0 || local.Y < 0 || local.Width <= 0 || local.Height <= 0 ||
                local.X + local.Width > windowRect.Width || local.Y + local.Height > windowRect.Height)
            {
                throw new McpToolException(
                    McpErrorCodes.InvalidParams,
                    $"region resolves to {local.X},{local.Y} {local.Width}x{local.Height} physical pixels, " +
                    $"outside the {windowRect.Width}x{windowRect.Height} window.");
            }
            return local;
        }

        private static JObject BoundsJson(float x, float y, float width, float height)
        {
            return new JObject
            {
                ["x"] = x,
                ["y"] = y,
                ["width"] = width,
                ["height"] = height,
            };
        }

        private static (byte[] png, int w, int h) CaptureSceneView(int reqW, int reqH)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null)
                throw new McpToolException(McpErrorCodes.NotFound, "No active SceneView found.");

            var camera = sv.camera;
            if (camera == null)
                throw new McpToolException(McpErrorCodes.ToolError, "SceneView camera is null.");

            var w = reqW > 0 ? reqW : (int)sv.position.width;
            var h = reqH > 0 ? reqH : (int)sv.position.height;
            if (w <= 0 || h <= 0)
                throw new McpToolException(McpErrorCodes.ToolError, $"Invalid SceneView size: {w}x{h}.");

            return RenderCamera(camera, w, h);
        }

        private static (byte[] png, int w, int h) CaptureGameView(int reqW, int reqH)
        {
            if (!EditorApplication.isPlaying)
                throw new McpToolException(McpErrorCodes.ToolUnavailable, "GameView capture requires Play Mode.");

            var camera = Camera.main;
            if (camera == null)
                throw new McpToolException(McpErrorCodes.NotFound, "No Camera.main found in Play Mode.");

            var w = reqW > 0 ? reqW : camera.pixelWidth;
            var h = reqH > 0 ? reqH : camera.pixelHeight;
            if (w <= 0 || h <= 0)
                throw new McpToolException(McpErrorCodes.ToolError, $"Invalid camera size: {w}x{h}.");

            return RenderCamera(camera, w, h);
        }

        private static (byte[] png, int w, int h) RenderCamera(Camera camera, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            var prevRT = camera.targetTexture;
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                var png = tex.EncodeToPNG();
                return (png, w, h);
            }
            finally
            {
                RenderTexture.active = prevActive;
                camera.targetTexture = prevRT;
                if (tex != null)
                    UnityEngine.Object.DestroyImmediate(tex);
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static EditorWindow FindEditorWindow(string windowTypeName, string title, bool openIfNeeded)
        {
            if (!string.IsNullOrWhiteSpace(windowTypeName))
            {
                var type = FindType(windowTypeName);
                if (type == null || !typeof(EditorWindow).IsAssignableFrom(type))
                    throw new McpToolException(McpErrorCodes.NotFound, $"EditorWindow type not found: '{windowTypeName}'.");

                var existing = Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault();
                if (existing != null)
                    return existing;

                if (openIfNeeded)
                    return EditorWindow.GetWindow(type);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                var existing = Resources.FindObjectsOfTypeAll<EditorWindow>()
                    .FirstOrDefault(window => window.titleContent != null &&
                                              window.titleContent.text.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0);
                if (existing != null)
                    return existing;
            }

            throw new McpToolException(McpErrorCodes.NotFound, "No matching EditorWindow found.");
        }

        private static WindowCaptureTarget CreateFloatingWindow(
            string windowTypeName,
            string title,
            int requestedWidth,
            int requestedHeight)
        {
            if (string.IsNullOrWhiteSpace(windowTypeName))
                throw new McpToolException(McpErrorCodes.InvalidParams, "windowType is required when floating is true.");

            var type = FindType(windowTypeName);
            if (type == null || !typeof(EditorWindow).IsAssignableFrom(type))
                throw new McpToolException(McpErrorCodes.NotFound, $"EditorWindow type not found: '{windowTypeName}'.");

            var existing = Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault();
            if (existing != null)
                return new WindowCaptureTarget(existing, false);

            var window = ScriptableObject.CreateInstance(type) as EditorWindow;
            if (window == null)
                throw new McpToolException(McpErrorCodes.ToolError, $"Failed to create EditorWindow type '{windowTypeName}'.");

            if (!string.IsNullOrWhiteSpace(title))
                window.titleContent = new GUIContent(title);

            var width = requestedWidth > 0 ? requestedWidth : 1280;
            var height = requestedHeight > 0 ? requestedHeight : 720;
            window.position = new Rect(80, 80, width, height);
            return new WindowCaptureTarget(window, true);
        }

        private static Type FindType(string typeName)
        {
            var direct = Type.GetType(typeName);
            if (direct != null)
                return direct;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName);
                if (type != null)
                    return type;

                type = assembly.GetTypes().FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static byte[] CaptureScreenRect(int x, int y, int w, int h)
        {
            // ponytail: Unity exposes editor-window pixels only through this editor-internal screen read.
            var colors = InternalEditorUtility.ReadScreenPixel(new Vector2(x, y), w, h);
            var texture = new Texture2D(w, h, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels(colors);
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void BringUnityToFront()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return;

            var handle = Process.GetCurrentProcess().MainWindowHandle;
            if (handle != IntPtr.Zero)
                SetForegroundWindow(handle);
        }

        private static bool TryGetWindowsWindowRect(string title, out IntPtr handle, out DesktopRect rect)
        {
            handle = IntPtr.Zero;
            rect = default;
            if (string.IsNullOrWhiteSpace(title))
                return false;

            var foundHandle = IntPtr.Zero;
            var foundRect = default(DesktopRect);
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                    return true;

                var length = GetWindowTextLength(hwnd);
                if (length <= 0)
                    return true;

                var builder = new StringBuilder(length + 1);
                GetWindowText(hwnd, builder, builder.Capacity);
                var windowTitle = builder.ToString();
                if (windowTitle.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                if (!GetWindowRect(hwnd, out var win32Rect))
                    return true;

                foundHandle = hwnd;
                foundRect = new DesktopRect(
                    win32Rect.Left,
                    win32Rect.Top,
                    win32Rect.Right - win32Rect.Left,
                    win32Rect.Bottom - win32Rect.Top);
                return false;
            }, IntPtr.Zero);

            handle = foundHandle;
            rect = foundRect;
            return handle != IntPtr.Zero && rect.Width > 0 && rect.Height > 0;
        }

        private static byte[] CaptureWindowsDesktopRect(int x, int y, int w, int h)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memoryDc = CreateCompatibleDC(screenDc);
            var bitmap = CreateCompatibleBitmap(screenDc, w, h);
            var previous = SelectObject(memoryDc, bitmap);
            Texture2D texture = null;

            try
            {
                if (!BitBlt(memoryDc, 0, 0, w, h, screenDc, x, y, Srccopy))
                    throw new McpToolException(McpErrorCodes.ToolError, "BitBlt failed while capturing editor window.");

                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = w,
                        Height = -h,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0,
                    }
                };
                var pixels = new byte[w * h * 4];
                if (GetDIBits(memoryDc, bitmap, 0, (uint)h, pixels, ref info, 0) == 0)
                    throw new McpToolException(McpErrorCodes.ToolError, "GetDIBits failed while capturing editor window.");

                return EncodeBgraTopDownToPng(pixels, w, h);
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
                SelectObject(memoryDc, previous);
                DeleteObject(bitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static byte[] CaptureWindowsWindow(IntPtr hwnd, int w, int h)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memoryDc = CreateCompatibleDC(screenDc);
            var bitmap = CreateCompatibleBitmap(screenDc, w, h);
            var previous = SelectObject(memoryDc, bitmap);

            try
            {
                if (!PrintWindow(hwnd, memoryDc, PrintWindowFullContent) &&
                    !PrintWindow(hwnd, memoryDc, 0))
                {
                    throw new McpToolException(McpErrorCodes.ToolError, "PrintWindow failed while capturing editor window.");
                }

                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = w,
                        Height = -h,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0,
                    }
                };
                var pixels = new byte[w * h * 4];
                if (GetDIBits(memoryDc, bitmap, 0, (uint)h, pixels, ref info, 0) == 0)
                    throw new McpToolException(McpErrorCodes.ToolError, "GetDIBits failed while capturing editor window.");

                return EncodeBgraTopDownToPng(pixels, w, h);
            }
            finally
            {
                SelectObject(memoryDc, previous);
                DeleteObject(bitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private static byte[] EncodeBgraTopDownToPng(byte[] pixels, int w, int h)
        {
            var colors = new Color32[w * h];
            for (var row = 0; row < h; row++)
            {
                var targetRow = h - 1 - row;
                for (var col = 0; col < w; col++)
                {
                    var sourceIndex = (row * w + col) * 4;
                    colors[targetRow * w + col] = new Color32(
                        pixels[sourceIndex + 2],
                        pixels[sourceIndex + 1],
                        pixels[sourceIndex],
                        pixels[sourceIndex + 3]);
                }
            }

            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(colors);
                texture.Apply();
                return texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static byte[] CropPngIfNeeded(
            byte[] png,
            int sourceWidth,
            int sourceHeight,
            DesktopRect localRect)
        {
            if (localRect.X == 0 && localRect.Y == 0 &&
                localRect.Width == sourceWidth && localRect.Height == sourceHeight)
            {
                return png;
            }

            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var cropped = new Texture2D(localRect.Width, localRect.Height, TextureFormat.RGBA32, false);
            try
            {
                source.LoadImage(png);
                cropped.SetPixels(source.GetPixels(
                    localRect.X,
                    sourceHeight - localRect.Y - localRect.Height,
                    localRect.Width,
                    localRect.Height));
                cropped.Apply();
                return cropped.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(cropped);
            }
        }

        private readonly struct WindowCaptureTarget
        {
            public EditorWindow Window { get; }
            public bool CloseAfterCapture { get; }
            public bool Activate { get; }

            public WindowCaptureTarget(EditorWindow window, bool closeAfterCapture)
                : this(window, closeAfterCapture, true)
            {
            }

            public WindowCaptureTarget(EditorWindow window, bool closeAfterCapture, bool activate)
            {
                Window = window;
                CloseAfterCapture = closeAfterCapture;
                Activate = activate;
            }
        }

        private const int Srccopy = 0x00CC0020;
        private const uint PrintWindowFullContent = 0x00000002;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr destDc, int destX, int destY, int width, int height, IntPtr sourceDc, int sourceX, int sourceY, int rasterOp);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint startScan, uint scanLines, byte[] bits, ref BitmapInfo info, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private readonly struct DesktopRect
        {
            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }

            public DesktopRect(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }
    }
}
