#if HAS_ADDRESSABLES
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp;
using LittleBrushGames.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace LittleBrushGames.Mcp.Modules.Addressables
{
    [McpToolProvider]
    public sealed class AddressablesProvider : IToolProvider
    {
        private readonly Func<AddressableAssetSettings> _settingsProvider;

        public AddressablesProvider()
            : this(() => AddressableAssetSettingsDefaultObject.Settings)
        {
        }

        public AddressablesProvider(Func<AddressableAssetSettings> settingsProvider)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        }

        public string Namespace => "addressables";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "addressables.list",
                Description = "List Addressables groups; explicit entries are opt-in and bounded.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""groupName"": { ""type"": ""string"", ""minLength"": 1 },
                        ""createGroup"": { ""type"": ""boolean"" },
                        ""includeEntries"": { ""type"": ""boolean"" },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 5000, ""description"": ""Entry page size. Default 25."" }
                    }
                }"),
                Handler = List,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "addressables.find",
                Description = "Find explicit Addressables entries by address text, label, path text, guid, or group name. At least one filter is required.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""address"": { ""type"": ""string"", ""minLength"": 1 },
                        ""label"": { ""type"": ""string"", ""minLength"": 1 },
                        ""path"": { ""type"": ""string"", ""minLength"": 1 },
                        ""guid"": { ""type"": ""string"", ""minLength"": 1 },
                        ""groupName"": { ""type"": ""string"", ""minLength"": 1 },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 5000, ""description"": ""Page size. Default 25."" }
                    }
                }"),
                Handler = Find,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "addressables.add",
                Description = "Add or move assets into Addressables. Supports per-asset address, group, and labels. Args: { groupName?: string, labels?: string[], assets: [{ path, address?, groupName?, labels? }] }.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""assets""],
                    ""properties"": {
                        ""groupName"": { ""type"": ""string"", ""minLength"": 1 },
                        ""labels"": { ""type"": ""array"", ""maxItems"": 64, ""items"": { ""type"": ""string"", ""minLength"": 1 } },
                        ""assets"": {
                            ""type"": ""array"",
                            ""minItems"": 1,
                            ""maxItems"": 256,
                            ""items"": {
                                ""type"": ""object"",
                                ""required"": [""path""],
                                ""properties"": {
                                    ""path"": { ""type"": ""string"", ""minLength"": 1 },
                                    ""address"": { ""type"": ""string"", ""minLength"": 1 },
                                    ""groupName"": { ""type"": ""string"", ""minLength"": 1 },
                                    ""labels"": { ""type"": ""array"", ""maxItems"": 64, ""items"": { ""type"": ""string"", ""minLength"": 1 } }
                                }
                            }
                        }
                    }
                }"),
                Handler = Add,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "addressables.remove",
                Description = "Remove explicit Addressables entries by path or guid. Returns removed and missing targets.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""paths"": { ""type"": ""array"", ""maxItems"": 256, ""items"": { ""type"": ""string"", ""minLength"": 1 } },
                        ""guids"": { ""type"": ""array"", ""maxItems"": 256, ""items"": { ""type"": ""string"", ""minLength"": 1 } }
                    }
                }"),
                Handler = Remove,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "addressables.set_address",
                Description = "Set the address for an explicit Addressables entry. Args: { address: string, path?: string, guid?: string }.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""address""],
                    ""properties"": {
                        ""address"": { ""type"": ""string"", ""minLength"": 1 },
                        ""path"": { ""type"": ""string"", ""minLength"": 1 },
                        ""guid"": { ""type"": ""string"", ""minLength"": 1 }
                    }
                }"),
                Handler = SetAddress,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "addressables.group_create",
                Description = "Create an Addressables group using the default group's schemas. Idempotent.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""required"": [""name""], ""properties"": { ""name"": { ""type"": ""string"", ""minLength"": 1 } } }"),
                Handler = CreateGroup,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "addressables.profiles",
                Description = "Page Addressables profiles. Defaults to compact names; request detail=values for profile variables and values.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""additionalProperties"": false,
                    ""properties"": {
                        ""detail"": { ""type"": ""string"", ""enum"": [""names"", ""values""] },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 }
                    }
                }"),
                Handler = ListProfiles,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });

            reg.Register(new ToolDescriptor
            {
                Name = "addressables.profile_set_active",
                Description = "Set the active Addressables profile by name.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""required"": [""name""], ""properties"": { ""name"": { ""type"": ""string"", ""minLength"": 1 } } }"),
                Handler = SetActiveProfile,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "addressables.build",
                Description = "Build Addressables player content. Governed by the local Builds trust policy.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.LongRunning,
                Timeout = TimeSpan.FromMinutes(30),
                ExclusiveGroup = "build",
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""properties"": {} }"),
                Handler = Build,
            });
        }

        private ValueTask<ToolResult> List(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var includeEntries = ctx.Arguments["includeEntries"]?.Value<bool>() ?? false;
            var groupName = (string)ctx.Arguments["groupName"];
            var offset = ctx.Arguments["offset"]?.Value<int>() ?? 0;
            var limit = ctx.Arguments["limit"]?.Value<int>() ?? 25;

            var groups = EnumerateGroups(settings, groupName).ToList();
            var totalEntryCount = groups.Sum(g => g.entries.Count);
            var returnedEntryCount = 0;
            var seenEntryCount = 0;
            var groupResults = new JArray();

            foreach (var group in groups)
            {
                var groupJson = new JObject
                {
                    ["name"] = group.Name,
                    ["guid"] = group.Guid,
                    ["readOnly"] = group.ReadOnly,
                    ["isDefault"] = group.Default,
                    ["entryCount"] = group.entries.Count,
                };

                if (includeEntries)
                {
                    var entries = new JArray();
                    foreach (var entry in group.entries.OrderBy(e => e.address, StringComparer.Ordinal).ThenBy(e => e.AssetPath, StringComparer.Ordinal))
                    {
                        if (seenEntryCount++ < offset)
                            continue;
                        if (returnedEntryCount >= limit)
                            continue;

                        entries.Add(SerializeEntry(entry));
                        returnedEntryCount++;
                    }

                    groupJson["entries"] = entries;
                }

                groupResults.Add(groupJson);
            }

            var truncated = includeEntries && offset + returnedEntryCount < totalEntryCount;
            var result = new JObject
            {
                ["settingsPath"] = AssetDatabase.GetAssetPath(settings),
                ["groupCount"] = groups.Count,
                ["entryCount"] = totalEntryCount,
                ["offset"] = includeEntries ? offset : 0,
                ["returnedEntryCount"] = includeEntries ? returnedEntryCount : 0,
                ["includeEntries"] = includeEntries,
                ["truncated"] = truncated,
                ["groups"] = groupResults,
            };
            if (truncated)
                result["nextOffset"] = offset + returnedEntryCount;
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private ValueTask<ToolResult> Find(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var filter = new FindFilter(
                (string)ctx.Arguments["address"],
                (string)ctx.Arguments["label"],
                (string)ctx.Arguments["path"],
                (string)ctx.Arguments["guid"],
                (string)ctx.Arguments["groupName"]);

            if (!filter.HasAnyFilter)
                throw new McpToolException(McpErrorCodes.InvalidParams, "addressables.find requires at least one filter: address, label, path, guid, or groupName.");

            var offset = ctx.Arguments["offset"]?.Value<int>() ?? 0;
            var limit = ctx.Arguments["limit"]?.Value<int>() ?? 25;
            var matches = new JArray();
            var totalCount = 0;

            foreach (var group in EnumerateGroups(settings, filter.GroupName))
            {
                foreach (var entry in group.entries.OrderBy(e => e.address, StringComparer.Ordinal).ThenBy(e => e.AssetPath, StringComparer.Ordinal))
                {
                    if (!MatchesFilter(entry, group, filter))
                        continue;

                    totalCount++;
                    if (totalCount > offset && matches.Count < limit)
                        matches.Add(SerializeEntry(entry));
                }
            }

            var result = new JObject
            {
                ["settingsPath"] = AssetDatabase.GetAssetPath(settings),
                ["count"] = totalCount,
                ["offset"] = offset,
                ["returned"] = matches.Count,
                ["truncated"] = offset + matches.Count < totalCount,
                ["entries"] = matches,
            };
            if (offset + matches.Count < totalCount)
                result["nextOffset"] = offset + matches.Count;
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private ValueTask<ToolResult> Add(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var requests = ParseAddRequests(ctx.Arguments);
            if (requests.Count == 0)
                throw new McpToolException(McpErrorCodes.InvalidParams, "addressables.add requires at least one asset.");

            var defaultLabels = ReadStringSet(ctx.Arguments["labels"]);
            var groupCache = new Dictionary<string, AddressableAssetGroup>(StringComparer.Ordinal);
            var seenGuids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var request in requests)
            {
                request.Guid = AssetDatabase.AssetPathToGUID(request.Path);
                if (string.IsNullOrEmpty(request.Guid))
                    throw new McpToolException(McpErrorCodes.NotFound, $"Asset not found: {request.Path}");
                if (!seenGuids.Add(request.Guid))
                    throw new McpToolException(McpErrorCodes.Conflict, $"Duplicate asset in addressables.add batch: {request.Path}");
            }

            var results = new JArray();
            foreach (var request in requests)
            {
                var existing = settings.FindAssetEntry(request.Guid);
                var previousGroupName = existing?.parentGroup?.Name;
                var previousAddress = existing?.address;
                var targetGroupName = request.GroupName ?? (string)ctx.Arguments["groupName"];
                var targetGroup = ResolveGroup(settings, targetGroupName, groupCache, ctx.Arguments["createGroup"]?.Value<bool>() == true);
                var entry = settings.CreateOrMoveEntry(request.Guid, targetGroup, readOnly: false, postEvent: true);
                if (entry == null)
                    throw new McpToolException(McpErrorCodes.Conflict, $"Unable to create or move Addressables entry for asset '{request.Path}'.");

                if (!string.IsNullOrWhiteSpace(request.Address))
                    entry.SetAddress(request.Address);

                foreach (var label in MergeLabels(defaultLabels, request.Labels))
                    entry.SetLabel(label, true, force: true, postEvent: true);

                results.Add(new JObject
                {
                    ["guid"] = entry.guid,
                    ["path"] = entry.AssetPath,
                    ["address"] = entry.address,
                    ["previousAddress"] = previousAddress,
                    ["groupName"] = entry.parentGroup?.Name,
                    ["previousGroupName"] = previousGroupName,
                    ["wasAddressable"] = existing != null,
                    ["moved"] = existing != null && !string.Equals(previousGroupName, entry.parentGroup?.Name, StringComparison.Ordinal),
                    ["labels"] = new JArray(GetSortedLabels(entry)),
                });
            }

            AssetDatabase.SaveAssets();

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["settingsPath"] = AssetDatabase.GetAssetPath(settings),
                ["count"] = results.Count,
                ["entries"] = results,
            }));
        }

        private ValueTask<ToolResult> Remove(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var targets = new Dictionary<string, RemovalTarget>(StringComparer.Ordinal);
            AddRemovalTargets(targets, ctx.Arguments["guids"] as JArray, isPath: false);
            AddRemovalTargets(targets, ctx.Arguments["paths"] as JArray, isPath: true);

            if (targets.Count == 0)
                throw new McpToolException(McpErrorCodes.InvalidParams, "addressables.remove requires at least one guid or path.");

            var removed = new JArray();
            var missing = new JArray();

            foreach (var target in targets.Values.OrderBy(t => t.SortKey, StringComparer.Ordinal))
            {
                if (target.IsPath)
                {
                    target.Guid = AssetDatabase.AssetPathToGUID(target.RawValue);
                    if (string.IsNullOrEmpty(target.Guid))
                    {
                        missing.Add(new JObject
                        {
                            ["selector"] = "path",
                            ["value"] = target.RawValue,
                            ["reason"] = "asset_not_found",
                        });
                        continue;
                    }
                }

                var entry = settings.FindAssetEntry(target.Guid);
                if (entry == null)
                {
                    missing.Add(new JObject
                    {
                        ["selector"] = target.IsPath ? "path" : "guid",
                        ["value"] = target.RawValue,
                        ["guid"] = target.Guid,
                        ["reason"] = "entry_not_found",
                    });
                    continue;
                }

                var serialized = SerializeEntry(entry);
                settings.RemoveAssetEntry(entry.guid, postEvent: true);
                removed.Add(serialized);
            }

            if (removed.Count > 0)
                AssetDatabase.SaveAssets();

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["settingsPath"] = AssetDatabase.GetAssetPath(settings),
                ["removedCount"] = removed.Count,
                ["missingCount"] = missing.Count,
                ["removed"] = removed,
                ["missing"] = missing,
            }));
        }

        private ValueTask<ToolResult> SetAddress(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var address = (string)ctx.Arguments["address"];
            var guid = (string)ctx.Arguments["guid"];
            var path = (string)ctx.Arguments["path"];

            if (string.IsNullOrWhiteSpace(guid))
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new McpToolException(McpErrorCodes.InvalidParams, "addressables.set_address requires either guid or path.");

                guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                    throw new McpToolException(McpErrorCodes.NotFound, $"Asset not found: {path}");
            }

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Addressables entry not found for guid '{guid}'.");

            var previousAddress = entry.address;
            entry.SetAddress(address);
            AssetDatabase.SaveAssets();

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["guid"] = entry.guid,
                ["path"] = entry.AssetPath,
                ["groupName"] = entry.parentGroup?.Name,
                ["previousAddress"] = previousAddress,
                ["address"] = entry.address,
            }));
        }

        private ValueTask<ToolResult> CreateGroup(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var name = ((string)ctx.Arguments["name"])?.Trim();
            var existing = settings.FindGroup(name);
            var group = existing ?? ResolveGroup(settings, name, new Dictionary<string, AddressableAssetGroup>(), true);
            if (existing == null) AssetDatabase.SaveAssets();
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["name"] = group.Name, ["guid"] = group.Guid, ["created"] = existing == null }));
        }

        private ValueTask<ToolResult> ListProfiles(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var variables = settings.profileSettings.GetVariableNames();
            var names = settings.profileSettings.GetAllProfileNames().ToList();
            var detail = (string)ctx.Arguments["detail"] ?? "names";
            var offset = ctx.Arguments["offset"]?.Value<int>() ?? 0;
            var limit = ctx.Arguments["limit"]?.Value<int>() ?? 25;
            var profiles = new JArray();
            foreach (var name in names.Skip(offset).Take(limit))
            {
                var id = settings.profileSettings.GetProfileId(name);
                var profile = new JObject { ["id"] = id, ["name"] = name, ["active"] = id == settings.activeProfileId };
                if (detail == "values")
                {
                    var values = new JObject();
                    foreach (var variable in variables)
                        values[variable] = settings.profileSettings.GetValueByName(id, variable);
                    profile["values"] = values;
                }
                profiles.Add(profile);
            }
            var result = new JObject
            {
                ["activeProfileId"] = settings.activeProfileId,
                ["detail"] = detail,
                ["variableCount"] = variables.Count,
                ["total"] = names.Count,
                ["offset"] = offset,
                ["returned"] = profiles.Count,
                ["truncated"] = offset + profiles.Count < names.Count,
                ["profiles"] = profiles,
            };
            if (detail == "values") result["variables"] = new JArray(variables);
            if (offset + profiles.Count < names.Count) result["nextOffset"] = offset + profiles.Count;
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private ValueTask<ToolResult> SetActiveProfile(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var name = (string)ctx.Arguments["name"];
            var id = settings.profileSettings.GetProfileId(name);
            if (string.IsNullOrEmpty(id))
                throw new McpToolException(McpErrorCodes.NotFound, $"Addressables profile not found: '{name}'.");
            settings.activeProfileId = id;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["name"] = name, ["id"] = id, ["active"] = true }));
        }

        private ValueTask<ToolResult> Build(ToolContext ctx, CancellationToken _)
        {
            var settings = GetSettingsOrThrow();
            var started = DateTimeOffset.UtcNow;
            AddressableAssetSettings.BuildPlayerContent(out var result);
            var error = result?.Error;
            var payload = new JObject
            {
                ["success"] = string.IsNullOrEmpty(error),
                ["error"] = error,
                ["profile"] = settings.profileSettings.GetProfileName(settings.activeProfileId),
                ["durationMs"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            };
            if (!string.IsNullOrEmpty(error))
                throw new McpToolException(McpErrorCodes.ToolError, error, payload);
            return new ValueTask<ToolResult>(ToolResult.Ok(payload));
        }

        private AddressableAssetSettings GetSettingsOrThrow()
        {
            var settings = _settingsProvider();
            if (settings != null)
                return settings;

            throw new McpToolException(
                McpErrorCodes.ToolUnavailable,
                "Addressables settings are currently unavailable.",
                new JObject { ["expectedPath"] = AddressableAssetSettingsDefaultObject.DefaultAssetPath });
        }

        private static IEnumerable<AddressableAssetGroup> EnumerateGroups(AddressableAssetSettings settings, string groupName)
        {
            var groups = settings.groups.Where(g => g != null);
            if (!string.IsNullOrWhiteSpace(groupName))
                groups = groups.Where(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));

            return groups.OrderBy(g => g.Name, StringComparer.Ordinal);
        }

        private static JObject SerializeEntry(AddressableAssetEntry entry)
        {
            return new JObject
            {
                ["guid"] = entry.guid,
                ["address"] = entry.address,
                ["path"] = entry.AssetPath,
                ["groupName"] = entry.parentGroup?.Name,
                ["readOnly"] = entry.ReadOnly,
                ["isFolder"] = entry.IsFolder,
                ["type"] = AssetDatabase.GetMainAssetTypeAtPath(entry.AssetPath)?.FullName,
                ["labels"] = new JArray(GetSortedLabels(entry)),
            };
        }

        private static bool MatchesFilter(AddressableAssetEntry entry, AddressableAssetGroup group, FindFilter filter)
        {
            if (!string.IsNullOrWhiteSpace(filter.Guid) && !string.Equals(entry.guid, filter.Guid, StringComparison.Ordinal))
                return false;

            if (!string.IsNullOrWhiteSpace(filter.GroupName) &&
                !string.Equals(group.Name, filter.GroupName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(filter.Label) &&
                !entry.labels.Contains(filter.Label))
                return false;

            if (!string.IsNullOrWhiteSpace(filter.Address) &&
                (entry.address == null || entry.address.IndexOf(filter.Address, StringComparison.OrdinalIgnoreCase) < 0))
                return false;

            if (!string.IsNullOrWhiteSpace(filter.Path) &&
                (entry.AssetPath == null || entry.AssetPath.IndexOf(filter.Path, StringComparison.OrdinalIgnoreCase) < 0))
                return false;

            return true;
        }

        private static List<AddRequest> ParseAddRequests(JObject arguments)
        {
            var assets = arguments["assets"] as JArray;
            var requests = new List<AddRequest>();
            if (assets == null)
                return requests;

            foreach (var token in assets)
            {
                if (token is not JObject item)
                    continue;

                requests.Add(new AddRequest
                {
                    Path = (string)item["path"],
                    Address = (string)item["address"],
                    GroupName = (string)item["groupName"],
                    Labels = ReadStringSet(item["labels"]),
                });
            }

            return requests;
        }

        private static AddressableAssetGroup ResolveGroup(
            AddressableAssetSettings settings,
            string groupName,
            IDictionary<string, AddressableAssetGroup> cache,
            bool create)
        {
            if (string.IsNullOrWhiteSpace(groupName))
            {
                if (settings.DefaultGroup == null)
                    throw new McpToolException(McpErrorCodes.ToolUnavailable, "Addressables settings has no default group.");
                return settings.DefaultGroup;
            }

            if (cache.TryGetValue(groupName, out var cached))
                return cached;

            var group = settings.FindGroup(groupName);
            if (group == null)
            {
                if (!create)
                    throw new McpToolException(McpErrorCodes.NotFound, $"Addressables group not found: '{groupName}'. Pass createGroup=true or call addressables.group_create first.");
                var templateSchemas = settings.DefaultGroup?.Schemas?.Where(s => s != null).ToList();
                group = templateSchemas is { Count: > 0 }
                    ? settings.CreateGroup(groupName, false, false, true, templateSchemas)
                    : settings.CreateGroup(groupName, false, false, true, null, typeof(ContentUpdateGroupSchema), typeof(BundledAssetGroupSchema));
            }

            cache[groupName] = group;
            return group;
        }

        private static SortedSet<string> ReadStringSet(JToken token)
        {
            var labels = new SortedSet<string>(StringComparer.Ordinal);
            if (token is not JArray arr)
                return labels;

            foreach (var child in arr)
            {
                if (child?.Type != JTokenType.String)
                    continue;

                var value = ((string)child)?.Trim();
                if (!string.IsNullOrEmpty(value))
                    labels.Add(value);
            }

            return labels;
        }

        private static IEnumerable<string> MergeLabels(IEnumerable<string> defaults, IEnumerable<string> itemLabels)
        {
            var merged = new SortedSet<string>(StringComparer.Ordinal);
            if (defaults != null)
            {
                foreach (var label in defaults)
                    merged.Add(label);
            }

            if (itemLabels != null)
            {
                foreach (var label in itemLabels)
                    merged.Add(label);
            }

            return merged;
        }

        private static string[] GetSortedLabels(AddressableAssetEntry entry)
            => entry.labels.OrderBy(label => label, StringComparer.Ordinal).ToArray();

        private static void AddRemovalTargets(Dictionary<string, RemovalTarget> targets, JArray values, bool isPath)
        {
            if (values == null)
                return;

            foreach (var value in values.Values<string>())
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var key = $"{(isPath ? "path" : "guid")}::{value}";
                targets[key] = new RemovalTarget { IsPath = isPath, RawValue = value };
            }
        }

        private sealed class AddRequest
        {
            public string Guid;
            public string Path;
            public string Address;
            public string GroupName;
            public SortedSet<string> Labels;
        }

        private sealed class RemovalTarget
        {
            public bool IsPath;
            public string RawValue;
            public string Guid;
            public string SortKey => $"{(IsPath ? "path" : "guid")}::{RawValue}";
        }

        private readonly struct FindFilter
        {
            public readonly string Address;
            public readonly string Label;
            public readonly string Path;
            public readonly string Guid;
            public readonly string GroupName;

            public FindFilter(string address, string label, string path, string guid, string groupName)
            {
                Address = address;
                Label = label;
                Path = path;
                Guid = guid;
                GroupName = groupName;
            }

            public bool HasAnyFilter =>
                !string.IsNullOrWhiteSpace(Address) ||
                !string.IsNullOrWhiteSpace(Label) ||
                !string.IsNullOrWhiteSpace(Path) ||
                !string.IsNullOrWhiteSpace(Guid) ||
                !string.IsNullOrWhiteSpace(GroupName);
        }
    }
}
#endif
