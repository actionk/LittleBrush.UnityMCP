using System.Collections.Generic;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Providers;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public sealed class BuildProviderTests
    {
        [Test]
        public void RegisterTools_ExposesBuildAndPlayerSettingsWorkflow()
        {
            var sink = new CollectingSink();
            new BuildProvider().RegisterTools(sink);
            var names = sink.Tools.Select(tool => tool.Name);
            Assert.That(names, Contains.Item("build.settings"));
            Assert.That(names, Contains.Item("build.player_settings.read"));
            Assert.That(names, Contains.Item("build.player_settings.write"));
            Assert.That(names, Contains.Item("build.start"));
            Assert.That(names, Contains.Item("build.result"));
            Assert.That(sink.Tools.Single(tool => tool.Name == "build.settings").Availability, Is.EqualTo(ToolAvailability.Either));
            Assert.That(sink.Tools.Single(tool => tool.Name == "build.player_settings.read").Availability, Is.EqualTo(ToolAvailability.Either));
        }

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
