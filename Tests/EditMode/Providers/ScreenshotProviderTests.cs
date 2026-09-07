using System;
using System.Reflection;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Providers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public sealed class ScreenshotProviderTests
    {
        [TestCase(false, ToolTrustCategory.Read)]
        [TestCase(true, ToolTrustCategory.EditorState)]
        public void WindowScreenshot_ActivationUsesEditorStateTrust(bool activate, ToolTrustCategory expected)
        {
            Assert.That(
                ScreenshotProvider.ResolveWindowScreenshotTrust(new JObject { ["activate"] = activate }),
                Is.EqualTo(expected));
        }

        [Test]
        public void FloatingCapture_ReusesMatchingOpenWindow()
        {
            TestWindow existing = ScriptableObject.CreateInstance<TestWindow>();
            existing.ShowUtility();

            try
            {
                MethodInfo method = typeof(ScreenshotProvider).GetMethod(
                    "CreateFloatingWindow",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null);

                object target = method.Invoke(
                    null,
                    new object[] { typeof(TestWindow).FullName, null, 320, 240 });
                PropertyInfo windowProperty = target.GetType().GetProperty("Window");
                PropertyInfo closeProperty = target.GetType().GetProperty("CloseAfterCapture");

                Assert.That(windowProperty?.GetValue(target), Is.SameAs(existing));
                Assert.That(closeProperty?.GetValue(target), Is.False);
                Assert.That(Resources.FindObjectsOfTypeAll<TestWindow>(), Has.Length.EqualTo(1));
            }
            finally
            {
                existing.Close();
            }
        }

        [Test]
        public void BackgroundPreparation_ReusesMatchingOpenWindowWithoutCreatingOne()
        {
            TestWindow existing = ScriptableObject.CreateInstance<TestWindow>();
            existing.ShowUtility();

            try
            {
                MethodInfo method = typeof(ScreenshotProvider).GetMethod(
                    "PrepareWindow",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null);

                object target = method.Invoke(
                    null,
                    new object[] { typeof(TestWindow).FullName, null, true, false, false, 320, 240 });
                PropertyInfo windowProperty = target.GetType().GetProperty("Window");
                PropertyInfo activateProperty = target.GetType().GetProperty("Activate");

                Assert.That(windowProperty?.GetValue(target), Is.SameAs(existing));
                Assert.That(activateProperty?.GetValue(target), Is.False);
                Assert.That(Resources.FindObjectsOfTypeAll<TestWindow>(), Has.Length.EqualTo(1));
            }
            finally
            {
                existing.Close();
            }
        }

        [Test]
        public void FloatingTarget_DoesNotCreateAContainerBeforePreparation()
        {
            Type containerType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
            int containerCount = Resources.FindObjectsOfTypeAll(containerType).Length;
            object target = CreateFloatingTarget();
            var window = (EditorWindow)target.GetType().GetProperty("Window")?.GetValue(target);

            try
            {
                Assert.That(Resources.FindObjectsOfTypeAll(containerType), Has.Length.EqualTo(containerCount));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void EditorRectToWindowsDesktopRect_ScalesNegativeMultiMonitorCoordinates()
        {
            MethodInfo method = typeof(ScreenshotProvider).GetMethod(
                "EditorRectToWindowsDesktopRect",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            object result = method.Invoke(null, new object[] { new Rect(-2118f, 79f, 1693f, 905f), 1.5f });
            Type type = result.GetType();

            Assert.That(type.GetProperty("X")?.GetValue(result), Is.EqualTo(-3177));
            Assert.That(type.GetProperty("Y")?.GetValue(result), Is.EqualTo(118));
            Assert.That(type.GetProperty("Width")?.GetValue(result), Is.EqualTo(2540));
            Assert.That(type.GetProperty("Height")?.GetValue(result), Is.EqualTo(1358));
        }

        [Test]
        public void ResolveLocalCaptureRect_ConvertsWindowLocalLogicalRegionToPixels()
        {
            MethodInfo boundsMethod = typeof(ScreenshotProvider).GetMethod(
                "EditorRectToWindowsDesktopRect",
                BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo regionMethod = typeof(ScreenshotProvider).GetMethod(
                "ResolveLocalCaptureRect",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(boundsMethod, Is.Not.Null);
            Assert.That(regionMethod, Is.Not.Null);

            object windowRect = boundsMethod.Invoke(null, new object[] { new Rect(-2118f, 79f, 1693f, 905f), 1.5f });
            var region = JObject.Parse(@"{ 'x': 100, 'y': 50, 'width': 400, 'height': 300 }");
            object result = regionMethod.Invoke(null, new[] { windowRect, region, (object)1.5f });
            Type type = result.GetType();

            Assert.That(type.GetProperty("X")?.GetValue(result), Is.EqualTo(150));
            Assert.That(type.GetProperty("Y")?.GetValue(result), Is.EqualTo(75));
            Assert.That(type.GetProperty("Width")?.GetValue(result), Is.EqualTo(600));
            Assert.That(type.GetProperty("Height")?.GetValue(result), Is.EqualTo(450));
        }

        private static object CreateFloatingTarget()
        {
            MethodInfo method = typeof(ScreenshotProvider).GetMethod(
                "CreateFloatingWindow",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(
                null,
                new object[] { typeof(TestWindow).FullName, null, 320, 240 });
        }

        private sealed class TestWindow : EditorWindow
        {
        }
    }
}
