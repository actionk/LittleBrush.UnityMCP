using System;
using System.Collections.Generic;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Providers;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public sealed class PackageManagerProviderTests
    {
        [Test]
        public void RegisterTools_ExposesSerializedAddAndRemoveOperations()
        {
            var sink = new CollectingSink();
            new PackageManagerProvider().RegisterTools(sink);

            Assert.That(sink.Tools.Select(tool => tool.Name), Is.EquivalentTo(new[]
            {
                "package_manager.add",
                "package_manager.remove",
            }));

            foreach (var tool in sink.Tools)
            {
                Assert.That(tool.Availability, Is.EqualTo(ToolAvailability.EditMode));
                Assert.That(tool.Execution, Is.EqualTo(ToolExecution.Async));
                Assert.That(tool.ReloadSafe, Is.True);
                Assert.That(tool.ExclusiveGroup, Is.EqualTo("package-manager"));
                Assert.That(tool.Timeout, Is.EqualTo(TimeSpan.FromMinutes(10)));
            }

            Assert.That(sink.Tools.Single(tool => tool.Name == "package_manager.add")
                .InputSchema["required"]!.Values<string>(), Contains.Item("packageId"));
            Assert.That(sink.Tools.Single(tool => tool.Name == "package_manager.remove")
                .InputSchema["required"]!.Values<string>(), Contains.Item("packageName"));
        }

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
