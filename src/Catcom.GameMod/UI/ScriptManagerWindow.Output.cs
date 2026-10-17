using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Catcom.Core.Catalog;
using Catcom.Core.Logging;

namespace Catcom.GameMod.UI;

// Output tab (the script's log lines) and Code tab (read-only source view).
public sealed partial class ScriptManagerWindow
{
    private const double OutputRefreshSeconds = 0.2;
    private const double CodeRefreshSeconds = 1.0;
    private const int MaxCodeBytes = 256 * 1024;

    // output
    private IReadOnlyList<LogEntry> _outLines = [];
    private string _outScript = "";
    private double _outAt = double.NegativeInfinity;
    private int _outLastCount;
    private bool _outAutoScroll = true;

    // code
    private string _codePath = "";
    private DateTime _codeWriteUtc;
    private long _codeLength = -1;
    private double _codeCheckedAt = double.NegativeInfinity;
    private ImInputString? _codeBuffer;
    private string _codeFullText = "";
    private string _codeNote = "";
    private int _codeLines;

    // ------------------------------------------------------------------------------ output

    private void DrawOutput(ScriptInfo info)
    {
        double now = ImGui.GetTime();
        if (_outScript != info.Name || now - _outAt >= OutputRefreshSeconds)
        {
            _outLines = _host.Facade.OutputFor(info.Name);
            _outScript = info.Name;
            _outAt = now;
        }

        IReadOnlyList<LogEntry> lines = _outLines;

        ImGui.Checkbox("Auto-scroll##out_auto", ref _outAutoScroll);
        ImGui.SameLine();
        if (ImGui.Button("Copy##out_copy"))
        {
            ImGui.SetClipboardText(JoinLines(lines, $"[{info.Name}] "));
        }

        ImGuiWidgets.Tooltip("Copy every line to the clipboard.");
        ImGui.SameLine();
        if (ImGui.Button("Clear##out_clear"))
        {
            _host.Facade.ClearOutput(info.Name);
            _outLines = [];
            lines = _outLines;
            _outAt = double.NegativeInfinity;
        }

        ImGui.SameLine();
        ImGuiWidgets.TextMuted($"{lines.Count} line(s)");

        float height = Math.Max(100f, ImGui.GetContentRegionAvail().Y);
        bool visible = ImGui.BeginChild("##out_lines", new float2(0f, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        try
        {
            if (!visible)
            {
                return;
            }

            if (lines.Count == 0)
            {
                ImGuiWidgets.TextMuted("No output yet. Anything the script prints with ksa.console.log / warn / error (and its errors) appears here.");
            }

            string prefix = $"[{info.Name}] ";

            using (new MonoScope())
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    LogEntry line = lines[i];
                    string message = StripPrefix(line.Message, prefix);
                    switch (line.Level)
                    {
                        case LogLevel.Error:
                            ImGuiWidgets.TextColored(ImGuiWidgets.ErrorColor, message);
                            break;
                        case LogLevel.Warn:
                            ImGuiWidgets.TextColored(ImGuiWidgets.WarningColor, message);
                            break;
                        case LogLevel.Debug:
                            ImGuiWidgets.TextColored(ImGuiWidgets.MutedColor, message);
                            break;
                        default:
                            ImGui.Text(message);
                            break;
                    }
                }
            }

            // Follow the tail while the user stays at the bottom (or when new lines arrive and auto-scroll is on).
            bool grew = lines.Count != _outLastCount;
            _outLastCount = lines.Count;
            if (_outAutoScroll && (grew || ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f))
            {
                ImGui.SetScrollHereY(1f);
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>The host prefixes every retained line with <c>[script] </c>; the tab already says which script it is.</summary>
    private static string StripPrefix(string message, string prefix)
        => message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;

    private static string JoinLines(IReadOnlyList<LogEntry> lines, string prefix)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(StripPrefix(lines[i].Message, prefix)).Append('\n');
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------------------ code

    private void DrawCode(ScriptInfo info)
    {
        RefreshCode(info.Path);

        if (ImGui.SmallButton("Copy all##code_copy"))
        {
            ImGui.SetClipboardText(_codeFullText);
        }

        ImGui.SameLine();
        ImGuiWidgets.TextMuted($"{_codeLines} line(s), read-only (use Edit to change it)");

        if (_codeNote.Length > 0)
        {
            ImGuiWidgets.TextColoredWrapped(ImGuiWidgets.WarningColor, _codeNote);
        }

        if (_codeBuffer is null)
        {
            return;
        }

        float height = Math.Max(120f, ImGui.GetContentRegionAvail().Y);
        using (new MonoScope())
        {
            ImGui.InputTextMultiline("##code_view", _codeBuffer, new float2(-1f, height), ImGuiInputTextFlags.ReadOnly);
        }
    }

    /// <summary>Re-reads the file at most once per second, and only when its write time or length changed.</summary>
    private void RefreshCode(string path)
    {
        double now = ImGui.GetTime();
        bool samePath = string.Equals(_codePath, path, StringComparison.Ordinal);
        if (samePath && now - _codeCheckedAt < CodeRefreshSeconds)
        {
            return;
        }

        _codeCheckedAt = now;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                _codePath = path;
                _codeBuffer = null;
                _codeFullText = "";
                _codeLines = 0;
                _codeNote = "The file does not exist on disk.";
                _codeLength = -1;
                return;
            }

            if (samePath && file.LastWriteTimeUtc == _codeWriteUtc && file.Length == _codeLength)
            {
                return;
            }

            byte[] raw;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int toRead = (int)Math.Min(stream.Length, MaxCodeBytes);
                raw = new byte[toRead];
                stream.ReadExactly(raw, 0, toRead);
            }

            string text = Encoding.UTF8.GetString(raw).Replace("\r", "", StringComparison.Ordinal).Replace("\0", "", StringComparison.Ordinal);
            _codeFullText = text;
            string shown = text.Replace("\t", "    ", StringComparison.Ordinal);
            byte[] utf8 = Encoding.UTF8.GetBytes(shown);
            var buffer = new ImInputString(utf8.Length + 1);
            buffer.SetValue(utf8);
            _codeBuffer = buffer;
            _codeLines = shown.Length == 0 ? 0 : shown.Count(c => c == '\n') + 1;
            _codeNote = file.Length > MaxCodeBytes ? $"Showing the first {MaxCodeBytes / 1024} KiB of {file.Length / 1024} KiB." : "";
            _codePath = path;
            _codeWriteUtc = file.LastWriteTimeUtc;
            _codeLength = file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _codePath = path;
            _codeNote = $"Could not read the file: {ex.Message}";
        }
    }
}
