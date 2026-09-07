using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Diagnostics;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace LittleBrushGames.Mcp.Editor.UI
{
    internal sealed class McpTestHistoryPanel : VisualElement
    {
        private readonly string _path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "McpUsage", "test-history.json"));
        private readonly Label _summary = new() { name = "test-history-summary" };
        private readonly TextField _search = new("Search tests");
        private readonly FloatField _minimum = new("Longer than (seconds)");
        private readonly DropdownField _sort = new("Sort", new() { "Latest duration", "Successful median", "Test name" }, 0);
        private readonly Label _count = new();
        private readonly TextField _selected = new("Selected test") { isReadOnly = true, multiline = true };
        private readonly ListView _list;
        private JArray _rows = new();
        private bool _loading;

        internal McpTestHistoryPanel()
        {
            name = "test-history-panel";
            AddToClassList("tab-content");
            var help = new Foldout { text = "How test history is measured", value = false };
            help.Add(new HelpBox("Local history of MCP test runs. Medians include successful cases only, grouped by test name, mode and Unity version. A slow test is not necessarily a regression. Retention: up to 20 runs / 16 MiB.", HelpBoxMessageType.Info));
            var refresh = new Button(Refresh) { text = "Refresh history" };
            refresh.AddToClassList("btn-secondary");
            var actions = new VisualElement();
            actions.AddToClassList("action-row");
            actions.Add(refresh);
            Add(actions);
            Add(help);
            _summary.AddToClassList("settings-hint");
            _summary.style.whiteSpace = WhiteSpace.Normal;
            Add(_summary); Add(_search); Add(_minimum); Add(_sort); Add(_count);
            _search.RegisterValueChangedCallback(_ => Filter());
            _minimum.RegisterValueChangedCallback(_ => Filter());
            _sort.RegisterValueChangedCallback(_ => Filter());
            _list = new ListView { fixedItemHeight = 44, selectionType = SelectionType.Single };
            _list.style.height = 360;
            _list.makeItem = () =>
            {
                var row = new VisualElement();
                row.AddToClassList("test-history-row");
                var title = new Label { name = "test-name" };
                title.style.overflow = Overflow.Hidden;
                title.style.textOverflow = TextOverflow.Ellipsis;
                row.Add(title);
                var timing = new Label { name = "test-timing" };
                timing.AddToClassList("test-history-timing");
                row.Add(timing);
                return row;
            };
            _list.bindItem = (element, index) =>
            {
                var row = (JObject)_list.itemsSource[index];
                var nameLabel = element.Q<Label>("test-name");
                nameLabel.text = (string)row["fullName"];
                nameLabel.tooltip = nameLabel.text;
                element.Q<Label>("test-timing").text = $"{row["status"]} · Last {(double?)row["lastSec"] ?? 0:0.###} s · Median {Median(row)} · {row["successfulSamples"]}/{row["samples"]} passed";
                element.tooltip = $"{row["mode"]}, Unity {row["unityVersion"]}. Median uses retained successful samples; no machine or code-change normalization.";
            };
            Add(_list);
            Add(_selected);
            _list.selectionChanged += selection =>
            {
                var row = selection.OfType<JObject>().FirstOrDefault();
                _selected.SetValueWithoutNotify(row == null ? "" : $"{row["fullName"]} ({row["mode"]}, Unity {row["unityVersion"]})");
            };
        }

        private static string Median(JObject row) => row["medianSec"]?.Type == JTokenType.Null ? "—" : $"{(double?)row["medianSec"]:0.###} s";

        internal async void Refresh()
        {
            if (_loading) return;
            _loading = true;
            _summary.text = "Loading test history…";
            try
            {
                var result = await Task.Run(() =>
                {
                    var history = McpTestHistory.Read(_path);
                    return (History: history, Rows: McpTestHistory.Summarize(history));
                });
                _rows = result.Rows;
                var runs = (JArray)result.History["runs"];
                var last = runs.LastOrDefault();
                _summary.text = last == null ? "No completed MCP runs recorded yet. Run tests through MCP, then refresh."
                    : $"{runs.Count} retained runs. Latest: {last["status"]}, {last["passCount"]} passed / {last["failCount"]} failed. Tests {(double?)last["durationSec"] ?? 0:0.###} s; full run {(double?)last["wallDurationSec"] ?? 0:0.###} s (includes preparation and restoration).";
                Filter();
            }
            catch (Exception ex)
            {
                _rows = new JArray(); Filter();
                _summary.text = $"Cannot read test history: {ex.Message} File: {_path}";
            }
            finally { _loading = false; }
        }

        private void Filter()
        {
            var rows = _rows.OfType<JObject>().Where(r => ((string)r["fullName"] ?? "").IndexOf(_search.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0
                && ((double?)r["lastSec"] ?? 0) >= Math.Max(0, _minimum.value));
            rows = _sort.index == 2 ? rows.OrderBy(r => (string)r["fullName"], StringComparer.Ordinal)
                : rows.OrderByDescending(r => (double?)r[_sort.index == 1 ? "medianSec" : "lastSec"] ?? -1);
            var items = rows.ToList();
            _selected.SetValueWithoutNotify("");
            _list.itemsSource = items;
            _list.Rebuild();
            _list.style.display = items.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _selected.style.display = items.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _count.text = $"{items.Count} matching tests. Times are seconds; samples cover retained runs.";
        }
    }
}
