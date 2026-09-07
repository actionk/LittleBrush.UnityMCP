using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Host;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class ExecuteCodeProviderTests
    {
        private bool _hadOriginalOverride;
        private McpTrustDecision _originalOverride;
        private bool _hadOriginalSessionGrant;
        private bool _originalGettingStartedShown;

        [SetUp]
        public void SetUp()
        {
            _hadOriginalOverride = McpTrustPolicy.TryGetOverride(
                ToolTrustCategory.CodeExecution, out _originalOverride);
            _hadOriginalSessionGrant = McpTrustPolicy.IsAllowedForSession(ToolTrustCategory.CodeExecution);
            _originalGettingStartedShown = McpExecuteCodeConsent.HasShownGettingStarted;
            McpExecuteCodeConsent.SetState(McpExecuteCodeConsentState.Enabled);
        }

        [TearDown]
        public void TearDown()
        {
            if (_hadOriginalOverride)
                McpTrustPolicy.SetOverride(ToolTrustCategory.CodeExecution, _originalOverride);
            else
                McpTrustPolicy.ClearOverride(ToolTrustCategory.CodeExecution);
            if (_hadOriginalSessionGrant)
                McpTrustPolicy.AllowForSession(ToolTrustCategory.CodeExecution);
            McpExecuteCodeConsent.SetGettingStartedShown(_originalGettingStartedShown);
        }

        [Test]
        public void RegisterTools_DeclaresExecuteCode()
        {
            var sink = Collect(new ExecuteCodeProvider());
            Assert.That(sink.Tools.Select(t => t.Name), Contains.Item("editor.execute_code"));
        }

        [Test]
        public void RegisterTools_AlwaysExposesExecuteCodeForPolicyDiscovery()
        {
            foreach (var state in new[] { McpExecuteCodeConsentState.Disabled, McpExecuteCodeConsentState.Undecided })
            {
                McpExecuteCodeConsent.SetState(state);
                var sink = Collect(new ExecuteCodeProvider());
                Assert.That(sink.Tools.Select(t => t.Name), Contains.Item("editor.execute_code"), state.ToString());
            }
        }

        [Test]
        public void Schema_RequiresReason()
        {
            var tool = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code");
            var required = tool.InputSchema["required"] as JArray;
            Assert.That(required, Is.Not.Null);
            var names = required.Select(t => (string)t).ToList();
            Assert.That(names, Contains.Item("code"));
            Assert.That(names, Contains.Item("reason"));
        }

        [Test]
        public void Schema_BoundsCodeAndReason()
        {
            var schema = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code").InputSchema;
            Assert.That((int)schema["properties"]["code"]["maxLength"], Is.EqualTo(65_536));
            Assert.That((int)schema["properties"]["reason"]["maxLength"], Is.EqualTo(500));
        }

        [Test]
        public void RegisterTools_DeclaresUnsandboxedExecutionAsDestructive()
        {
            var tool = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code");
            Assert.That((bool)tool.Annotations["destructiveHint"], Is.True);
            Assert.That((bool)tool.Annotations["readOnlyHint"], Is.False);
        }

        [Test]
        public void RegisterTools_AssignsCodeExecutionCategory()
        {
            var tool = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code");
            Assert.That(tool.TrustCategory, Is.EqualTo(ToolTrustCategory.CodeExecution));
        }

        [Test]
        public void Execute_RejectsTooLongReason()
        {
            var tool = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code");
            var exception = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(Ctx(new JObject
            {
                ["code"] = "output.Add(1);",
                ["reason"] = new string('x', 501),
            }), CancellationToken.None));

            Assert.That(exception.Code, Is.EqualTo(McpErrorCodes.ValidationFailed));
        }

        [Test]
        public void ConsentState_IsLocalAndVersioned()
        {
            McpExecuteCodeConsent.SetState(McpExecuteCodeConsentState.Disabled);
            McpExecuteCodeConsent.SetGettingStartedShown(true);

            Assert.That(McpExecuteCodeConsent.State, Is.EqualTo(McpExecuteCodeConsentState.Disabled));
            Assert.That(McpExecuteCodeConsent.HasShownGettingStarted, Is.True);
        }

        [Test]
        public void Execute_RejectsMissingReason()
        {
            var tool = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code");
            var args = new JObject { ["code"] = "output.Add(1);" };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        [Test]
        public void Execute_RejectsShortReason()
        {
            var tool = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code");
            var args = new JObject { ["code"] = "output.Add(1);", ["reason"] = "x" };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        [Test]
        public async Task Execute_AllowsLeadingUsingDirectives()
        {
            var tool = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code");
            var args = new JObject
            {
                ["code"] = "using UnityEngine;\n\nvar mesh = new Mesh();\noutput.Add(mesh != null);",
                ["reason"] = "verify using directives"
            };

            var result = await tool.Handler(Ctx(args), CancellationToken.None);

            Assert.That(result.IsError, Is.False, ErrorText(result));
            Assert.That((bool)result.StructuredContent["output"][0], Is.True);
        }

        [Test]
        public async Task Execute_ReturnsStructuredCompilationErrors()
        {
            var result = await Execute("var value = ;");

            Assert.That(result.IsError, Is.True);
            Assert.That((string)result.StructuredContent["phase"], Is.EqualTo("compile"));
            Assert.That(result.StructuredContent["compilationErrors"], Is.Not.Empty);
            Assert.That((int)result.StructuredContent["compilationErrors"][0]["line"], Is.EqualTo(1));
        }

        [Test]
        public async Task Execute_MapsRuntimeErrorToSnippetLine()
        {
            var result = await Execute("var value = 1;\nthrow new InvalidOperationException(\"boom\");");

            Assert.That(result.IsError, Is.True);
            Assert.That((string)result.StructuredContent["phase"], Is.EqualTo("invoke"));
            Assert.That((int)result.StructuredContent["snippetLine"], Is.EqualTo(2));
            Assert.That((string)result.StructuredContent["snippetLineText"], Does.Contain("InvalidOperationException"));
        }

        [Test]
        public async Task Execute_CanReferencePluginAssembly()
        {
            var result = await Execute("output.Add(typeof(LittleBrushGames.Mcp.Editor.Providers.ExecuteCodeProvider).Name);");

            Assert.That(result.IsError, Is.False, ErrorText(result));
            Assert.That((string)result.StructuredContent["output"][0], Is.EqualTo(nameof(ExecuteCodeProvider)));
        }

        [Test]
        public async Task Execute_BoundsReturnedItems()
        {
            var result = await Execute("for (var i = 0; i < 101; i++) output.Add(i);");

            Assert.That(result.IsError, Is.False, ErrorText(result));
            Assert.That(result.StructuredContent["output"].Count(), Is.EqualTo(100));
            Assert.That((int)result.StructuredContent["outputItemCount"], Is.EqualTo(101));
            Assert.That((bool)result.StructuredContent["outputTruncated"], Is.True);
        }

        private static async Task<ToolResult> Execute(string code)
        {
            var tool = Collect(new ExecuteCodeProvider()).Tools.First(t => t.Name == "editor.execute_code");
            return await tool.Handler(Ctx(new JObject
            {
                ["code"] = code,
                ["reason"] = "verify direct Roslyn compiler"
            }), CancellationToken.None);
        }

        private static CollectingSink Collect(IToolProvider p) { var s = new CollectingSink(); p.RegisterTools(s); return s; }

        private static ToolContext Ctx(JObject args) => new(args, new NoopProgressReporter(), new FakeMainThreadPump(),
            new FakeFrameWaiter(), null, new FakeLogSink(), "req");

        private static string ErrorText(ToolResult result)
            => result.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? result.StructuredContent?.ToString();

        private sealed class CollectingSink : IToolRegistration
        {
            public List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
