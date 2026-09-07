using LittleBrushGames.Mcp.Editor.Host;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Integration
{
    public sealed class McpBridgeSettingsTests
    {
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
