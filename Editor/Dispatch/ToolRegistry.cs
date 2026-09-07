using System;
using System.Collections.Generic;
using System.Linq;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public sealed class ToolRegistry
    {
        private readonly object _sync = new();
        public string Revision { get; private set; } = Guid.NewGuid().ToString("N");
        private Dictionary<string, ToolDescriptor> _tools = new();
        private IReadOnlyList<IToolProvider> _editorProviders = Array.Empty<IToolProvider>();
        private IReadOnlyList<IToolProvider> _runtimeProviders = Array.Empty<IToolProvider>();
        private IReadOnlyList<string> _errors = Array.Empty<string>();

        public IReadOnlyList<string> Errors
        {
            get { lock (_sync) return _errors.ToArray(); }
        }

        public void SetEditorProviders(IReadOnlyList<IToolProvider> providers)
        {
            lock (_sync)
            {
                _editorProviders = providers ?? Array.Empty<IToolProvider>();
                Rebuild();
            }
        }

        public void SetRuntimeProviders(IReadOnlyList<IToolProvider> providers)
        {
            lock (_sync)
            {
                _runtimeProviders = providers ?? Array.Empty<IToolProvider>();
                Rebuild();
            }
        }

        public IEnumerable<ToolDescriptor> Enumerate()
        {
            lock (_sync) return _tools.Values.ToArray();
        }

        public IEnumerable<ToolDescriptor> EnumerateAvailable(bool isPlaying, bool isCompiling)
        {
            ToolDescriptor[] snapshot;
            lock (_sync) snapshot = _tools.Values.ToArray();
            foreach (var tool in snapshot)
            {
                if (isCompiling && (tool.Availability & ToolAvailability.Compiling) == 0) continue;
                if (isPlaying && (tool.Availability & ToolAvailability.PlayMode) == 0) continue;
                if (!isPlaying && (tool.Availability & ToolAvailability.EditMode) == 0) continue;
                yield return tool;
            }
        }

        public bool TryGet(string name, out ToolDescriptor descriptor)
        {
            lock (_sync) return _tools.TryGetValue(name, out descriptor);
        }

        private void Rebuild()
        {
            var next = new Dictionary<string, ToolDescriptor>();
            var errors = new List<string>();
            Collect(_editorProviders, next, errors);
            Collect(_runtimeProviders, next, errors);
            _tools = next;
            Revision = Guid.NewGuid().ToString("N");
            _errors = errors;
        }

        private static void Collect(
            IReadOnlyList<IToolProvider> providers,
            Dictionary<string, ToolDescriptor> target,
            List<string> errors)
        {
            foreach (var provider in providers)
            {
                if (provider == null)
                {
                    errors.Add("Provider is null.");
                    continue;
                }

                var providerType = provider.GetType();
                var staged = new Dictionary<string, ToolDescriptor>();
                try
                {
                    if (string.IsNullOrWhiteSpace(provider.Namespace))
                        throw new InvalidOperationException("Provider namespace is required.");
                    provider.RegisterTools(new Sink(staged, providerType));
                    foreach (var pair in staged)
                        if (target.ContainsKey(pair.Key))
                            throw new InvalidOperationException($"Duplicate MCP tool '{pair.Key}'.");
                    foreach (var pair in staged)
                        target.Add(pair.Key, pair.Value);
                }
                catch (Exception ex)
                {
                    errors.Add($"{providerType.FullName}: {ex.Message}");
                }
            }
        }

        private sealed class Sink : IToolRegistration
        {
            private readonly Dictionary<string, ToolDescriptor> _target;
            private readonly Type _providerType;

            public Sink(Dictionary<string, ToolDescriptor> target, Type providerType)
            {
                _target = target;
                _providerType = providerType;
            }

            public void Register(ToolDescriptor descriptor)
            {
                if (descriptor == null)
                    throw new InvalidOperationException("Tool descriptor is required.");
                if (!IsValidName(descriptor.Name))
                    throw new InvalidOperationException($"Invalid MCP tool name '{descriptor.Name ?? "<null>"}'.");
                if (descriptor.Handler == null)
                    throw new InvalidOperationException($"MCP tool '{descriptor.Name}' has no handler.");
                if (descriptor.InputSchema == null)
                    throw new InvalidOperationException($"MCP tool '{descriptor.Name}' has no input schema.");
                ValidateSchema(descriptor.Name, "input", descriptor.InputSchema);
                if (descriptor.OutputSchema != null)
                    ValidateSchema(descriptor.Name, "output", descriptor.OutputSchema);
                if (descriptor.Timeout <= TimeSpan.Zero)
                    throw new InvalidOperationException($"MCP tool '{descriptor.Name}' timeout must be positive.");
                if (_target.ContainsKey(descriptor.Name))
                    throw new InvalidOperationException($"Duplicate MCP tool '{descriptor.Name}'.");
                descriptor.ProviderTypeName ??= _providerType.FullName;
                descriptor.ProviderAssemblyName ??= _providerType.Assembly.GetName().Name;
                _target[descriptor.Name] = descriptor;
            }

            private static void ValidateSchema(string name, string label, Newtonsoft.Json.Linq.JObject schema)
            {
                var errors = SchemaValidator.ValidateSchema(schema).Take(5).ToArray();
                if (errors.Length == 0) return;
                throw new InvalidOperationException(
                    $"MCP tool '{name}' {label} schema is invalid: "
                    + string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}")));
            }

            private static bool IsValidName(string name)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !name.Contains('.')) return false;
                foreach (var character in name)
                    if (!(character is >= 'a' and <= 'z'
                          || character is >= '0' and <= '9'
                          || character is '.' or '_' or '-'))
                        return false;
                return true;
            }
        }
    }
}
