using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Host;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    /// <summary>
    /// Compile and execute C# snippets at runtime using Microsoft Roslyn.
    /// Snippets are wrapped in a static method with a <c>List&lt;object&gt; output</c> parameter.
    /// Every failure mode returns a structured envelope — never drops the bridge connection.
    /// </summary>
    [McpToolProvider]
    public sealed class ExecuteCodeProvider : IToolProvider
    {
        private const string SnippetSourceName = "snippet.cs";
        private const int UserCodeStartLine = 1;
        private const int MaxCodeCharacters = 65_536;
        private const int MaxReasonCharacters = 500;
        private const int MaxOutputItems = 100;
        private const int MaxOutputCharacters = 65_536;

        private static IReadOnlyList<MetadataReference> s_references;

        public string Namespace => "editor.code";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "editor.execute_code",
                Description = "Unsandboxed last-resort Roslyn snippet runner governed by the local CodeExecution trust policy. Prefer typed tools whenever they cover the operation. Code runs with Unity's full user permissions; the request timeout cannot interrupt user code after invocation. Requires a reason documenting why no typed tool fits.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                RequiresMainThread = true,
                Timeout = TimeSpan.FromSeconds(30),
                ExclusiveGroup = "execute",
                TrustCategory = ToolTrustCategory.CodeExecution,
                Annotations = new JObject
                {
                    ["destructiveHint"] = true,
                    ["readOnlyHint"] = false,
                },
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""code"", ""reason""],
                    ""additionalProperties"": false,
                    ""properties"": {
                        ""code"": { ""type"": ""string"", ""maxLength"": 65536, ""description"": ""C# code to execute. Use output.Add(x) to return values."" },
                        ""reason"": { ""type"": ""string"", ""minLength"": 3, ""maxLength"": 500, ""description"": ""Why no typed tool fits. Audited to drive future MCP coverage."" }
                    }
                }"),
                Handler = Execute,
            });
        }

        private static ValueTask<ToolResult> Execute(ToolContext ctx, CancellationToken ct)
        {
            try
            {
                return new ValueTask<ToolResult>(ExecuteCore(ctx, ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (McpToolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var env = new JObject
                {
                    ["success"] = false,
                    ["runtimeError"] = ex.GetType().Name + ": " + ex.Message,
                    ["stackTrace"] = ex.StackTrace,
                    ["phase"] = "outer",
                };
                return new ValueTask<ToolResult>(ToolResult.ErrorWithData(
                    $"execute_code failed (outer): {ex.GetType().Name}: {ex.Message}",
                    env,
                    expectedFailure: false));
            }
        }

        private static ToolResult ExecuteCore(ToolContext ctx, CancellationToken ct)
        {
            var code = (string)ctx.Arguments["code"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "code required.");
            if (code.Length > MaxCodeCharacters)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"code exceeds the {MaxCodeCharacters}-character limit.");
            var reason = (string)ctx.Arguments["reason"];
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    "reason required (min 3 chars). Document why no typed tool (asset.*, prefab.*, model_importer.*, editor.ensure_compiled) fits.");
            if (reason.Length > MaxReasonCharacters)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"reason exceeds the {MaxReasonCharacters}-character limit.");

            // Pre-flight: refuse to run while Editor is transitioning. Eliminates the
            // "snippet ran, domain reloaded underneath us" disconnect window.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new McpToolException(McpErrorCodes.Compiling,
                    "Editor is compiling or updating. Call editor.ensure_compiled first, then retry.");

            ct.ThrowIfCancellationRequested();

            var source = WrapSnippet(code);
            Assembly assembly;
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(
                    source,
                    path: SnippetSourceName,
                    encoding: Encoding.UTF8,
                    cancellationToken: ct);
                var compilation = CSharpCompilation.Create(
                    "LittleBrushMcpSnippet_" + Guid.NewGuid().ToString("N"),
                    new[] { syntaxTree },
                    s_references ??= GetLoadedAssemblyReferences(),
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        optimizationLevel: OptimizationLevel.Debug,
                        allowUnsafe: false,
                        deterministic: true));

                using var pe = new MemoryStream();
                using var pdb = new MemoryStream();
                var emit = compilation.Emit(
                    pe,
                    pdbStream: pdb,
                    options: new EmitOptions(
                        debugInformationFormat: DebugInformationFormat.PortablePdb,
                        pdbFilePath: SnippetSourceName),
                    cancellationToken: ct);

                if (!emit.Success)
                {
                    var errors = new JArray();
                    string firstMsg = null;
                    foreach (var diagnostic in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        var position = diagnostic.Location.GetMappedLineSpan().StartLinePosition;
                        var line = position.Line + 1;
                        var column = position.Character + 1;
                        var message = diagnostic.GetMessage();
                        firstMsg ??= $"{diagnostic.Id} at line {line}: {message}";
                        errors.Add(new JObject
                        {
                            ["code"] = diagnostic.Id,
                            ["line"] = line,
                            ["column"] = column,
                            ["message"] = message,
                        });
                    }

                    return ToolResult.ErrorWithData(
                        $"execute_code compilation failed ({errors.Count} error(s)): {firstMsg ?? "unknown"}",
                        new JObject
                        {
                            ["success"] = false,
                            ["compilationErrors"] = errors,
                            ["phase"] = "compile",
                            ["source"] = NumberLines(code, UserCodeStartLine),
                        });
                }

                assembly = Assembly.Load(pe.ToArray(), pdb.ToArray());
            }
            catch (Exception ex)
            {
                return ToolResult.ErrorWithData(
                    $"execute_code compile threw: {ex.GetType().Name}: {ex.Message}",
                    new JObject
                    {
                        ["success"] = false,
                        ["runtimeError"] = ex.GetType().Name + ": " + ex.Message,
                        ["stackTrace"] = ex.StackTrace,
                        ["phase"] = "compile",
                    },
                    expectedFailure: false);
            }

            var snippetType = assembly.GetType("McpSnippet.__Snippet");
            if (snippetType == null)
                return ToolResult.ErrorWithData(
                    "execute_code: snippet type not found in compiled assembly.",
                    new JObject
                    {
                        ["success"] = false,
                        ["runtimeError"] = "Snippet type not found in compiled assembly.",
                        ["phase"] = "resolve",
                    },
                    expectedFailure: false);

            var runMethod = snippetType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (runMethod == null)
                return ToolResult.ErrorWithData(
                    "execute_code: Run method not found.",
                    new JObject
                    {
                        ["success"] = false,
                        ["runtimeError"] = "Run method not found.",
                        ["phase"] = "resolve",
                    },
                    expectedFailure: false);

            ct.ThrowIfCancellationRequested();

            var output = new List<object>();
            try
            {
                runMethod.Invoke(null, new object[] { output });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                var stack = inner.StackTrace ?? string.Empty;
                var err = new JObject
                {
                    ["success"] = false,
                    ["exceptionType"] = inner.GetType().FullName,
                    ["runtimeError"] = inner.GetType().Name + ": " + inner.Message,
                    ["stackTrace"] = stack,
                    ["phase"] = "invoke",
                };
                TryExtractSnippetLine(stack, out var line);
                if (line > 0)
                {
                    err["snippetLine"] = line;
                    err["snippetLineText"] = GetUserCodeLine(code, line);
                }
                err["source"] = NumberLines(code, UserCodeStartLine);
                var summary = line > 0
                    ? $"execute_code threw at line {line}: {inner.GetType().Name}: {inner.Message}"
                    : $"execute_code threw: {inner.GetType().Name}: {inner.Message}";
                return ToolResult.ErrorWithData(summary, err);
            }

            var outputArr = new JArray();
            var outputCharacters = 0;
            var outputTruncated = output.Count > MaxOutputItems;
            foreach (var item in output.Take(MaxOutputItems))
            {
                JToken token;
                try { token = UnitySerializer.ToJson(item); }
                catch { token = item?.ToString(); }

                var tokenCharacters = token?.ToString(Formatting.None).Length ?? 4;
                if (outputCharacters + tokenCharacters > MaxOutputCharacters)
                {
                    outputTruncated = true;
                    break;
                }

                outputArr.Add(token);
                outputCharacters += tokenCharacters;
            }

            var envelope = new JObject
            {
                ["success"] = true,
                ["output"] = outputArr,
                ["outputItemCount"] = output.Count,
                ["outputTruncated"] = outputTruncated,
            };

            // Post-flight: snippet may have dirtied assets in a way that queues a domain reload.
            // Report it so the client can resync via editor.ensure_compiled instead of discovering
            // the bridge reconnecting mid-workflow.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                envelope["domainReloadPending"] = true;
                envelope["hint"] = "Snippet queued a domain reload; call editor.ensure_compiled to resync.";
            }

            return ToolResult.Ok(envelope);
        }

        private static string WrapSnippet(string code)
        {
            var userUsings = ExtractLeadingUsingDirectives(code, out var body, out var bodyStartLine);
            var sb = new StringBuilder();
            foreach (var userUsing in userUsings)
            {
                sb.AppendLine($"#line {userUsing.line} \"{SnippetSourceName}\"");
                sb.AppendLine(userUsing.text);
            }
            if (userUsings.Count > 0)
                sb.AppendLine("#line default");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine("namespace McpSnippet {");
            sb.AppendLine("  public static class __Snippet {");
            sb.AppendLine("    public static void Run(List<object> output) {");
            // Re-base line numbers so PDB-backed stack traces point at the user's code directly.
            sb.AppendLine($"#line {bodyStartLine} \"{SnippetSourceName}\"");
            sb.AppendLine(body);
            sb.AppendLine("#line default");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static readonly Regex s_usingDirectiveRx = new Regex(
            @"^\s*(?:global\s+)?using\s+(?:(?:static\s+)?[A-Za-z_][\w]*(?:(?:\.|::)[A-Za-z_][\w]*)*|[A-Za-z_][\w]*\s*=\s*[^;]+)\s*;\s*(?://.*)?$",
            RegexOptions.Compiled);

        private static List<(int line, string text)> ExtractLeadingUsingDirectives(
            string code,
            out string body,
            out int bodyStartLine)
        {
            var directives = new List<(int line, string text)>();
            if (string.IsNullOrEmpty(code))
            {
                body = string.Empty;
                bodyStartLine = UserCodeStartLine;
                return directives;
            }

            var lines = code.Split('\n');
            var bodyIndex = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var trimmed = line.TrimStart();
                if (string.IsNullOrWhiteSpace(line) || trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    bodyIndex = i + 1;
                    continue;
                }

                if (s_usingDirectiveRx.IsMatch(line))
                {
                    directives.Add((UserCodeStartLine + i, line));
                    bodyIndex = i + 1;
                    continue;
                }

                bodyIndex = i;
                break;
            }

            if (bodyIndex >= lines.Length)
            {
                body = string.Empty;
                bodyStartLine = UserCodeStartLine + lines.Length;
                return directives;
            }

            body = string.Join("\n", lines.Skip(bodyIndex));
            bodyStartLine = UserCodeStartLine + bodyIndex;
            return directives;
        }

        private static readonly Regex s_snippetLineRx = new Regex(
            @"__Snippet\.Run[^\r\n]*?(?::line\s+|:)(\d+)",
            RegexOptions.Compiled);

        private static bool TryExtractSnippetLine(string stackTrace, out int line)
        {
            line = 0;
            if (string.IsNullOrEmpty(stackTrace)) return false;
            var m = s_snippetLineRx.Match(stackTrace);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups[1].Value, out line)) return false;
            return line > 0;
        }

        private static string GetUserCodeLine(string code, int oneBasedLine)
        {
            if (string.IsNullOrEmpty(code) || oneBasedLine < 1) return null;
            var lines = code.Split('\n');
            if (oneBasedLine > lines.Length) return null;
            return lines[oneBasedLine - 1].TrimEnd('\r');
        }

        private static string NumberLines(string code, int startLine)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            var lines = code.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append((startLine + i).ToString().PadLeft(4));
                sb.Append(": ");
                sb.AppendLine(lines[i].TrimEnd('\r'));
            }
            return sb.ToString();
        }

        private static IReadOnlyList<MetadataReference> GetLoadedAssemblyReferences()
        {
            // Reference every currently-loaded non-dynamic assembly with a resolvable file path.
            // Earlier versions whitelisted Unity+System+Newtonsoft only, which made game/plugin
            // types (Scellecs.Morpeh, DontSettle.*, user asmdefs) invisible to snippets.
            // Dedupe by short name — if two versions are loaded, first one wins.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var references = new List<MetadataReference>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;

                string shortName;
                try { shortName = asm.GetName().Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(shortName)) continue;
                if (!seen.Add(shortName)) continue;

                try
                {
                    var location = asm.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    if (!File.Exists(location)) continue;

                    references.Add(MetadataReference.CreateFromFile(location));
                }
                catch
                {
                    // Skip assemblies that can't be referenced (in-memory, unloadable, etc.).
                }
            }
            return references;
        }
    }
}
