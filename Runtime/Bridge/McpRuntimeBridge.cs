using System;
using System.Collections.Generic;
using System.Linq;

namespace LittleBrushGames.Mcp
{
    public static class McpRuntimeBridge
    {
        private static readonly object s_sync = new();
        private static State s_state = new(Array.Empty<IToolProvider>(), null, 0);

        public static event Action Changed;

        public static IReadOnlyList<IToolProvider> Providers => s_state.Providers;
        public static IServiceProvider Services => s_state.Services;
        public static int Version => s_state.Version;

        public static IDisposable Install(IEnumerable<IToolProvider> providers, IServiceProvider services)
        {
            var snapshot = providers?.ToArray() ?? Array.Empty<IToolProvider>();
            State installed;
            lock (s_sync)
            {
                installed = new State(snapshot, services, s_state.Version + 1);
                s_state = installed;
            }
            NotifyChanged();
            return new Scope(installed);
        }

        private static void NotifyChanged()
        {
            foreach (Action subscriber in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { subscriber(); }
                catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
            }
        }

        private sealed class State
        {
            public State(IToolProvider[] providers, IServiceProvider services, int version)
            {
                Providers = providers;
                Services = services;
                Version = version;
            }
            public IToolProvider[] Providers { get; }
            public IServiceProvider Services { get; }
            public int Version { get; }
        }

        private sealed class Scope : IDisposable
        {
            private readonly State _installed;
            public Scope(State installed) { _installed = installed; }

            public void Dispose()
            {
                var changed = false;
                lock (s_sync)
                {
                    if (ReferenceEquals(s_state, _installed))
                    {
                        s_state = new State(Array.Empty<IToolProvider>(), null, s_state.Version + 1);
                        changed = true;
                    }
                }
                if (changed) NotifyChanged();
            }
        }
    }
}
