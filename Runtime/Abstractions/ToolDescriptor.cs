using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp
{
    public sealed class ToolDescriptor
    {
        public string Name { get; init; }
        public string Description { get; init; }
        public JObject InputSchema { get; init; }
        public JObject OutputSchema { get; init; }
        public JObject Annotations { get; init; }
        public ToolTrustCategory TrustCategory { get; init; } = ToolTrustCategory.Auto;
        public Func<JObject, ToolTrustCategory> TrustCategoryResolver { get; init; }
        public string ProviderTypeName { get; set; }
        public string ProviderAssemblyName { get; set; }
        public ToolAvailability Availability { get; init; } = ToolAvailability.Either;
        public ToolExecution Execution { get; init; } = ToolExecution.Sync;
        public bool RequiresMainThread { get; init; } = true;
        public bool RequiresGraphics { get; init; }
        public bool RequiresInteractiveEditor { get; init; }
        /// <summary>Read-only main-thread tools may opt out of the writer lease when
        /// they only observe cached state. Writes always acquire the lease.</summary>
        public bool RequiresWriterLease { get; init; } = true;
        public TimeSpan? Timeout { get; init; }

        /// <summary>
        /// True if this tool is safe for the bridge to replay after a mid-request domain
        /// reload. The tool must be idempotent or hash-keyed so a replayed call either
        /// no-ops, returns cached results, or produces identical side-effects.
        /// </summary>
        public bool ReloadSafe { get; init; }

        /// <summary>
        /// Tools sharing the same non-null group are serialized — only one runs at a time.
        /// </summary>
        public string ExclusiveGroup { get; init; }

        /// <summary>
        /// Thread-safe probe for work continuing after the handler returns. While true,
        /// Editor operations outside this tool's ExclusiveGroup are rejected as busy.
        /// The provider must preserve this state across reload and through cleanup.
        /// </summary>
        public Func<bool> BackgroundOperationActive { get; init; }

        public Func<ToolContext, CancellationToken, ValueTask<ToolResult>> Handler { get; init; }
    }
}
