using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Providers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public sealed class EfficiencyPropertyProbe : ScriptableObject
    {
        public int[] values;
        public string label;
    }

    public class McpEfficiencyTests
    {
        [Test]
        public void PropertyPageMatchesExistingValuesAndCountsOnLargeArray()
        {
            var probe = ScriptableObject.CreateInstance<EfficiencyPropertyProbe>();
            try
            {
                probe.values = Enumerable.Range(0, 10000).ToArray();
                probe.label = "selected";
                AssetsProvider.SerializeObjectProperties(probe);
                var baselineAlloc = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                var all = AssetsProvider.SerializeObjectProperties(probe);
                timer.Stop();
                var baselineMs = timer.Elapsed.TotalMilliseconds;
                baselineAlloc = GC.GetAllocatedBytesForCurrentThread() - baselineAlloc;
                var optimizedAlloc = GC.GetAllocatedBytesForCurrentThread();
                timer.Restart();
                var page = AssetsProvider.ReadPropertyPage(probe, new[] { "label" }, null, 0, 25, false, 2000, out var count, out var filtered);
                timer.Stop();
                optimizedAlloc = GC.GetAllocatedBytesForCurrentThread() - optimizedAlloc;
                Assert.That(count, Is.EqualTo(all.Count));
                Assert.That(filtered, Is.EqualTo(1));
                Assert.That((string)page["label"], Is.EqualTo((string)all["label"]));
                Assert.That(page.Count, Is.EqualTo(1));
                TestContext.WriteLine($"10,000 array elements, one property: baseline {baselineMs:F2} ms / {baselineAlloc} allocated bytes (zero means unavailable); selected page {timer.Elapsed.TotalMilliseconds:F2} ms / {optimizedAlloc} bytes.");
            }
            finally { UnityEngine.Object.DestroyImmediate(probe); }
        }

        [Test]
        public void JsonCharacterCounterMatchesEscapedUnicodeAndImageEnvelope()
        {
            var value = new JObject { ["text"] = "é\n\"quoted\" 🐈", ["result"] = new JObject { ["content"] = new JArray(
                new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(new byte[100000]), ["mimeType"] = "image/png" }) } };
            Assert.That(McpToolCallLogger.CountJsonCharacters(value), Is.EqualTo(value.ToString(Formatting.None).Length));
        }

        [Test]
        public void UsageMeasurementsSeparateImagesAndWriterWait()
        {
            var directory = Path.Combine(Path.GetTempPath(), "mcp-measures-" + Guid.NewGuid());
            try
            {
                using (var journal = new McpUsageJournal(directory, Assert.Fail, false))
                    journal.Record("probe.image", new JObject { ["x"] = 1 }, JObject.Parse("{'result':{'content':[{'type':'image','data':'AQI='}]}}"), 100, 75, 20);
                var entry = JObject.Parse(File.ReadAllText(Path.Combine(directory, "statistics.json")))["tools"]["probe.image"];
                Assert.That((long)entry["totalWriterWaitMs"], Is.EqualTo(75));
                Assert.That((long)entry["totalDispatchMs"], Is.EqualTo(20));
                Assert.That((long)entry["totalImageBytes"], Is.EqualTo(2));
                Assert.That((long)entry["measurementCalls"], Is.EqualTo(1));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test]
        public void CompactStatusPreservesReadinessAndOmitsConfiguration()
        {
            var result = EditorStatusProvider.ProjectStatus(JObject.Parse("{'isCompiling':true,'isUpdating':false,'playModeOwner':'user','projectPath':'large path','registryErrorCount':1}"), JObject.Parse("{'detail':'compact'}"));
            Assert.That((bool)result["isCompiling"], Is.True);
            Assert.That((string)result["playModeOwner"], Is.EqualTo("user"));
            Assert.That(result["projectPath"], Is.Null);
            Assert.That((int)result["registryErrorCount"], Is.EqualTo(1));
        }

        [Test]
        public void PreviewPoseAffectsOnlyTemporaryCameraTransform()
        {
            var preview = new UnityEditor.PreviewRenderUtility();
            try
            {
                var camera = preview.camera;
                ScenePreviewProvider.ApplyCameraPose(camera, new Bounds(Vector3.zero, Vector3.one), 256, 256, .1f,
                    JObject.Parse("{'cameraPosition':[2,3,-5],'cameraRotation':[10,20,0]}"));
                Assert.That(Vector3.Distance(camera.transform.position, new Vector3(2,3,-5)), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(camera.transform.rotation, Quaternion.Euler(10,20,0)), Is.LessThan(.01f));
            }
            finally { preview.Cleanup(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StudioPreviewExcludesOverlayAndCleansUpAfterFailure(bool ground)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Studio test mesh";
            var hidden = new GameObject("Hidden subtree", typeof(LineRenderer));
            hidden.transform.SetParent(cube.transform, false);
            var unrelated = GameObject.CreatePrimitive(PrimitiveType.Cube);
            unrelated.name = "Studio test unrelated world mesh";
            unrelated.transform.localScale = Vector3.one * 3;
            unrelated.SetActive(false);
            var overlay = new GameObject("Studio test unrelated overlay", typeof(Canvas));
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            panel.transform.SetParent(overlay.transform, false);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            panel.GetComponent<UnityEngine.UI.Image>().color = Color.magenta;
            var canvas = overlay.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            overlay.SetActive(false);
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"));
            var renderer = cube.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            var selection = UnityEditor.Selection.activeObject;
            var previewCount = UnityEditor.SceneManagement.EditorSceneManager.previewSceneCount;
            var dirty = scene.isDirty;
            var fog = RenderSettings.fog;
            var texture = new Texture2D(2, 2);
            var args = JObject.Parse("{'width':128,'height':128,'background':'#202020'}");
            args["ground"] = ground;
            args["excludePaths"] = new JArray("Hidden subtree");
            try
            {
                var baseline = ScenePreviewProvider.CaptureStudio(cube, args, CancellationToken.None);
                Assert.That((bool)baseline.StructuredContent["ground"], Is.EqualTo(ground));
                Assert.That(hidden.activeSelf, Is.True, "Cutaway previews must not deactivate authored objects.");
                overlay.SetActive(true);
                unrelated.SetActive(true);
                var withOverlay = ScenePreviewProvider.CaptureStudio(cube, args, CancellationToken.None);
                var first = baseline.Content.OfType<ImageContent>().Single().Data;
                var second = withOverlay.Content.OfType<ImageContent>().Single().Data;
                Assert.That(texture.LoadImage(first), Is.True);
                Assert.That(texture.GetPixel(64, 64).grayscale, Is.GreaterThan(.15f), "The target must actually render.");
                if (ground)
                    Assert.That(texture.GetPixel(64, 3).grayscale, Is.GreaterThan(.18f), "Studio ground must render below the target.");
                CollectionAssert.AreEqual(first, second, "A loaded screen-space overlay must not appear in the studio capture.");
                Assert.That(renderer.sharedMaterial, Is.SameAs(material));
                Assert.That(renderer.enabled, Is.True);
                Assert.That(UnityEditor.Selection.activeObject, Is.SameAs(selection));
                Assert.That(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), Is.EqualTo(scene));
                Assert.That(UnityEngine.SceneManagement.SceneManager.sceneCount, Is.EqualTo(sceneCount));
                Assert.That(scene.isDirty, Is.EqualTo(dirty));
                Assert.That(UnityEditor.SceneManagement.EditorSceneManager.previewSceneCount, Is.EqualTo(previewCount));

                var line = cube.AddComponent<LineRenderer>();
                line.positionCount = 2;
                try
                {
                    Assert.Throws<McpToolException>(() => ScenePreviewProvider.CaptureStudio(cube, args, CancellationToken.None));
                    Assert.That(UnityEditor.SceneManagement.EditorSceneManager.previewSceneCount, Is.EqualTo(previewCount),
                        "Unsupported-renderer failures must dispose their temporary preview scene.");
                    Assert.That(RenderSettings.fog, Is.EqualTo(fog),
                        "A failed static preview must restore the global lighting context before destroying its scene.");
                }
                finally { UnityEngine.Object.DestroyImmediate(line); }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(overlay);
                UnityEngine.Object.DestroyImmediate(unrelated);
                UnityEngine.Object.DestroyImmediate(cube);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
