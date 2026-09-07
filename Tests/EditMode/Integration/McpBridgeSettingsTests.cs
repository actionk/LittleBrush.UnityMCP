using LittleBrushGames.Mcp.Editor.Host;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Integration
{
    public sealed class McpBridgeSettingsTests
    {
        [Test]
        public void EnvironmentPorts_OverrideSavedSettings()
        {
            var http = System.Environment.GetEnvironmentVariable("LITTLEBRUSH_MCP_PORT");
            var unity = System.Environment.GetEnvironmentVariable("LITTLEBRUSH_MCP_UNITY_PORT");
            try
            {
                System.Environment.SetEnvironmentVariable("LITTLEBRUSH_MCP_PORT", "48769");
                System.Environment.SetEnvironmentVariable("LITTLEBRUSH_MCP_UNITY_PORT", "48770");
                Assert.That(PortResolver.Resolve(), Is.EqualTo(48769));
                Assert.That(PortResolver.ResolveUnityPort(), Is.EqualTo(48770));
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("LITTLEBRUSH_MCP_PORT", http);
                System.Environment.SetEnvironmentVariable("LITTLEBRUSH_MCP_UNITY_PORT", unity);
            }
        }

        [Test]
        public void Defaults_AreSerializableAndHideSceneToolbar()
        {
            var settings = ScriptableObject.CreateInstance<McpBridgeSettings>();
            try
            {
                var serialized = new SerializedObject(settings);
                Assert.That(serialized.FindProperty("m_ServerEnabled"), Is.Not.Null);
                Assert.That(settings.ShowSceneToolbar, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }
    }
}
