#if HAS_ADDRESSABLES
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace LittleBrushGames.Mcp.Modules.Addressables.Tests
{
    public class AddressablesProviderTests
    {
        private const string TestRoot = "Assets/TempMcpAddressablesTests";
        private AddressableAssetSettings _settings;

        [SetUp]
        public void SetUp()
        {
            DeleteAssetIfExists(TestRoot);
            EnsureFolder("Assets", "TempMcpAddressablesTests");
            EnsureFolder(TestRoot, "Content");

            _settings = AddressableAssetSettings.Create(string.Empty, "McpTestSettings", true, false);
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings != null)
                Object.DestroyImmediate(_settings);
            _settings = null;
            DeleteAssetIfExists(TestRoot);
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void RegisterTools_ExposesAddressablesTools()
        {
            var provider = new AddressablesProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "addressables.list",
                    "addressables.find",
                    "addressables.add",
                    "addressables.remove",
                    "addressables.set_address",
                    "addressables.group_create",
                    "addressables.profiles",
                    "addressables.profile_set_active",
                    "addressables.build",
                },
                sink.Tools.Select(t => t.Name));
        }

        [Test]
        public async Task Profiles_DefaultsToCompactPagedNames()
        {
            var compact = await GetTool("addressables.profiles").Handler(
                CreateContext(new JObject()), CancellationToken.None);
            Assert.That((string)compact.StructuredContent["detail"], Is.EqualTo("names"));
            Assert.That((int)compact.StructuredContent["returned"], Is.LessThanOrEqualTo(25));
            if ((int)compact.StructuredContent["returned"] > 0)
                Assert.That(compact.StructuredContent["profiles"][0]["values"], Is.Null);

            var detailed = await GetTool("addressables.profiles").Handler(
                CreateContext(new JObject { ["detail"] = "values", ["limit"] = 1 }), CancellationToken.None);
            Assert.That(detailed.StructuredContent["variables"], Is.TypeOf<JArray>());
            if ((int)detailed.StructuredContent["returned"] > 0)
                Assert.That(detailed.StructuredContent["profiles"][0]["values"], Is.TypeOf<JObject>());
        }

        [Test]
        public async Task Add_UsesPerAssetAddressesAndLabels()
        {
            var firstPath = CreateTestAsset("alpha.mat");
            var secondPath = CreateTestAsset("beta.mat");
            var addTool = GetTool("addressables.add");

            var result = await addTool.Handler(CreateContext(JObject.Parse($@"{{
                ""groupName"": ""SharedGroup"",
                ""createGroup"": true,
                ""labels"": [""common""],
                ""assets"": [
                    {{ ""path"": ""{firstPath}"", ""address"": ""address/alpha"", ""labels"": [""first""] }},
                    {{ ""path"": ""{secondPath}"", ""address"": ""address/beta"", ""labels"": [""second""] }}
                ]
            }}")), CancellationToken.None);

            var entries = (JArray)result.StructuredContent["entries"];
            Assert.That(entries.Count, Is.EqualTo(2));
            Assert.That((string)entries[0]["address"], Is.EqualTo("address/alpha"));
            Assert.That((string)entries[1]["address"], Is.EqualTo("address/beta"));
            CollectionAssert.AreEquivalent(new[] { "common", "first" }, ((JArray)entries[0]["labels"]).Values<string>());
            CollectionAssert.AreEquivalent(new[] { "common", "second" }, ((JArray)entries[1]["labels"]).Values<string>());
        }

        [Test]
        public async Task List_AndFind_ReturnExpectedEntries()
        {
            var firstPath = CreateTestAsset("gamma.mat");
            var secondPath = CreateTestAsset("delta.mat");
            var addTool = GetTool("addressables.add");
            await addTool.Handler(CreateContext(JObject.Parse($@"{{
                ""createGroup"": true,
                ""assets"": [
                    {{ ""path"": ""{firstPath}"", ""address"": ""content/gamma"", ""labels"": [""creature""] }},
                    {{ ""path"": ""{secondPath}"", ""address"": ""content/delta"", ""groupName"": ""Secondary"", ""labels"": [""building""] }}
                ]
            }}")), CancellationToken.None);

            var listResult = await GetTool("addressables.list").Handler(
                CreateContext(new JObject { ["includeEntries"] = true }),
                CancellationToken.None);
            Assert.That((int)listResult.StructuredContent["entryCount"], Is.EqualTo(2));

            var compactList = await GetTool("addressables.list").Handler(CreateContext(new JObject()), CancellationToken.None);
            Assert.That((bool)compactList.StructuredContent["includeEntries"], Is.False);
            Assert.That(compactList.StructuredContent["groups"][0]["entries"], Is.Null);

            var findResult = await GetTool("addressables.find").Handler(
                CreateContext(new JObject { ["label"] = "building" }),
                CancellationToken.None);
            var found = (JArray)findResult.StructuredContent["entries"];
            Assert.That(found.Count, Is.EqualTo(1));
            Assert.That((string)found[0]["address"], Is.EqualTo("content/delta"));
            Assert.That((string)found[0]["groupName"], Is.EqualTo("Secondary"));

            var pagedList = await GetTool("addressables.list").Handler(
                CreateContext(new JObject { ["includeEntries"] = true, ["offset"] = 1, ["limit"] = 1 }),
                CancellationToken.None);
            Assert.That((int)pagedList.StructuredContent["offset"], Is.EqualTo(1));
            Assert.That((int)pagedList.StructuredContent["returnedEntryCount"], Is.EqualTo(1));
            Assert.That((bool)pagedList.StructuredContent["truncated"], Is.False);

            var pagedFind = await GetTool("addressables.find").Handler(
                CreateContext(new JObject { ["path"] = TestRoot, ["offset"] = 1, ["limit"] = 1 }),
                CancellationToken.None);
            Assert.That((int)pagedFind.StructuredContent["count"], Is.EqualTo(2));
            Assert.That((int)pagedFind.StructuredContent["returned"], Is.EqualTo(1));
            Assert.That((bool)pagedFind.StructuredContent["truncated"], Is.False);
        }

        [Test]
        public async Task Remove_ReturnsRemovedAndMissingTargets()
        {
            var keepPath = CreateTestAsset("keep.mat");
            var removePath = CreateTestAsset("remove.mat");
            await GetTool("addressables.add").Handler(CreateContext(JObject.Parse($@"{{
                ""assets"": [
                    {{ ""path"": ""{keepPath}"", ""address"": ""keep"" }},
                    {{ ""path"": ""{removePath}"", ""address"": ""remove"" }}
                ]
            }}")), CancellationToken.None);

            var result = await GetTool("addressables.remove").Handler(
                CreateContext(JObject.Parse($@"{{
                    ""paths"": [""{removePath}"", ""{TestRoot}/Content/missing.mat""]
                }}")),
                CancellationToken.None);

            Assert.That((int)result.StructuredContent["removedCount"], Is.EqualTo(1));
            Assert.That((int)result.StructuredContent["missingCount"], Is.EqualTo(1));
            Assert.That(_settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(removePath)), Is.Null);
            Assert.That(_settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(keepPath)), Is.Not.Null);
        }

        [Test]
        public async Task SetAddress_UpdatesExistingEntry()
        {
            var path = CreateTestAsset("rename.mat");
            await GetTool("addressables.add").Handler(CreateContext(JObject.Parse($@"{{
                ""assets"": [
                    {{ ""path"": ""{path}"", ""address"": ""before"" }}
                ]
            }}")), CancellationToken.None);

            var result = await GetTool("addressables.set_address").Handler(
                CreateContext(JObject.Parse($@"{{
                    ""path"": ""{path}"",
                    ""address"": ""after""
                }}")),
                CancellationToken.None);

            Assert.That((string)result.StructuredContent["previousAddress"], Is.EqualTo("before"));
            Assert.That((string)result.StructuredContent["address"], Is.EqualTo("after"));
        }

        [Test]
        public void List_WithoutSettings_ThrowsToolUnavailable()
        {
            _settings = null;

            var ex = Assert.ThrowsAsync<McpToolException>(async () =>
                await GetTool("addressables.list").Handler(CreateContext(new JObject()), CancellationToken.None));

            Assert.That(ex.Code, Is.EqualTo(McpErrorCodes.ToolUnavailable));
        }

        private static ToolContext CreateContext(JObject arguments)
        {
            return new ToolContext(
                arguments,
                new NoopProgressReporter(),
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                null,
                new FakeLogSink(),
                "req");
        }

        private static void EnsureFolder(string parent, string child)
        {
            var path = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }

        private static void DeleteAssetIfExists(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(path) != null || AssetDatabase.IsValidFolder(path))
                AssetDatabase.DeleteAsset(path);
        }

        private static string CreateTestAsset(string name)
        {
            var path = $"{TestRoot}/Content/{name}";
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null, "Expected a built-in shader for test material creation.");
            var asset = new Material(shader);
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            return path;
        }

        private ToolDescriptor GetTool(string toolName)
        {
            var provider = new AddressablesProvider(() => _settings);
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            return sink.Tools.Single(t => t.Name == toolName);
        }

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
#endif
