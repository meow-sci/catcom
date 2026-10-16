using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Runtime;

namespace KsaScripting.GameMod.UI;

// Modal dialogs: New script, confirmations, scripts folder, plus the toast line.
public sealed partial class ScriptManagerWindow
{
    private const string NewPopup = "New script###ksa_new_dialog";
    private const string FolderPopup = "Scripts folder###ksa_folder_dialog";
    private const string ConfirmPopup = "###ksa_confirm_dialog";

    // Popups are opened at the window's top-level ID scope, never from inside a child window.
    private bool _requestNew;
    private bool _requestFolder;
    private bool _requestConfirm;
    private bool _requestAddBinding;

    // toast
    private string _toast = "";
    private bool _toastError;
    private double _toastUntil;

    // New dialog
    private readonly ImInputString _newName = new(128);
    private int _newLanguage;
    private int _newTemplate;
    private string _newCreateError = "";

    // confirm dialog
    private string _confirmTitle = "";
    private string _confirmMessage = "";
    private string _confirmLabel = "OK";
    private Action? _confirmAction;

    // ------------------------------------------------------------------------------ plumbing

    private void DrawDialogs()
    {
        if (_requestNew)
        {
            _requestNew = false;
            _newName.Clear();
            _newCreateError = "";
            ImGui.OpenPopup(NewPopup);
        }

        if (_requestFolder)
        {
            _requestFolder = false;
            ImGui.OpenPopup(FolderPopup);
        }

        if (_requestConfirm)
        {
            _requestConfirm = false;
            ImGui.OpenPopup(ConfirmPopup);
        }

        if (_requestAddBinding)
        {
            _requestAddBinding = false;
            PrepareAddBinding();
            ImGui.OpenPopup(AddBindingPopup);
        }

        Guard("new dialog", DrawNewDialog);
        Guard("folder dialog", DrawFolderDialog);
        Guard("confirm dialog", DrawConfirmDialog);
        Guard("add binding dialog", DrawAddBindingDialog);
    }

    private void RequestConfirm(string title, string message, string confirmLabel, Action onConfirm)
    {
        _confirmTitle = title;
        _confirmMessage = message;
        _confirmLabel = confirmLabel;
        _confirmAction = onConfirm;
        _requestConfirm = true;
    }

    private void ShowToast(string message, bool error)
    {
        _toast = message;
        _toastError = error;
        _toastUntil = ImGui.GetTime() + 10.0;
    }

    private void DrawToast()
    {
        if (_toast.Length == 0)
        {
            return;
        }

        if (ImGui.GetTime() > _toastUntil)
        {
            _toast = "";
            return;
        }

        ImGuiWidgets.TextColoredWrapped(_toastError ? ImGuiWidgets.ErrorColor : ImGuiWidgets.SuccessColor, _toast);
        ImGui.SameLine();
        if (ImGui.SmallButton("dismiss##toast"))
        {
            _toast = "";
        }
    }

    // ------------------------------------------------------------------------------ confirm

    private void DrawConfirmDialog()
    {
        ImGuiWidgets.SetNextModalWidth(460f);
        bool open = true;
        if (!ImGui.BeginPopupModal($"{_confirmTitle}{ConfirmPopup}", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        try
        {
            ImGuiWidgets.NoteTextInput();
            ImGuiWidgets.TextWrapped(_confirmMessage);
            ImGui.Spacing();
            ImGui.Separator();

            if (ImGuiWidgets.DestructiveButton($"{_confirmLabel}##confirm_ok", new float2(120f, 0f)))
            {
                Action? action = _confirmAction;
                _confirmAction = null;
                ImGui.CloseCurrentPopup();
                action?.Invoke();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##confirm_cancel", new float2(120f, 0f)))
            {
                _confirmAction = null;
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    // ------------------------------------------------------------------------------ folder

    private void DrawFolderDialog()
    {
        ImGuiWidgets.SetNextModalWidth(620f);
        bool open = true;
        if (!ImGui.BeginPopupModal(FolderPopup, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        try
        {
            ImGuiWidgets.NoteTextInput();
            ModPaths paths = _host.Paths;
            DrawPathRow("Scripts", paths.ScriptsDir);
            DrawPathRow("API stubs", paths.StubsDir);
            DrawPathRow("Shipped libs", paths.ScriptsLibDir);
            DrawPathRow("Config", paths.ConfigFile);
            DrawPathRow("Bindings", paths.BindingsFile);
            DrawPathRow("Terminals", paths.TerminalsFile);
            ImGui.Spacing();
            ImGuiWidgets.TextMuted("The game cannot open a file manager for you; copy a path and paste it into your file manager.");
            ImGui.Separator();
            if (ImGui.Button("Close##folder_close", new float2(120f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private static void DrawPathRow(string label, string path)
    {
        using (new IdScope(label))
        {
            ImGui.AlignTextToFramePadding();
            ImGuiWidgets.TextMuted($"{label}:");
            ImGui.SameLine(110f);
            ImGui.Text(path);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy"))
            {
                ImGui.SetClipboardText(path);
            }
        }
    }

    // ------------------------------------------------------------------------------ New script

    private static readonly string[] LanguageLabels = ["Lua", "TypeScript"];

    private void DrawNewDialog()
    {
        ImGuiWidgets.SetNextModalWidth(560f);
        bool open = true;
        if (!ImGui.BeginPopupModal(NewPopup, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        try
        {
            ImGuiWidgets.NoteTextInput();
            string language = _newLanguage == 1 ? ScriptLanguages.TypeScript : ScriptLanguages.Lua;
            string extension = ScriptTemplates.Extension(language);

            if (ImGuiWidgets.BeginFormTableFixed("##new_form", 90f))
            {
                try
                {
                    ImGuiWidgets.FormRow("Name");
                    if (ImGui.IsWindowAppearing())
                    {
                        ImGui.SetKeyboardFocusHere();
                    }

                    if (ImGuiWidgets.InputWithHint("##new_name", "e.g. ascent  or  tools/report", _newName))
                    {
                        _newCreateError = "";
                    }

                    ImGuiWidgets.FormRow("Language");
                    ImGui.RadioButton("Lua (.lua)##new_lang_lua", ref _newLanguage, 0);
                    ImGui.SameLine();
                    ImGui.RadioButton("TypeScript (.ts)##new_lang_ts", ref _newLanguage, 1);
                }
                finally
                {
                    ImGuiWidgets.EndFormTable();
                }
            }

            ImGui.SeparatorText("Template");
            for (int i = 0; i < ScriptTemplates.All.Count; i++)
            {
                (ScriptTemplateKind _, string label, string description) = ScriptTemplates.All[i];
                ImGui.RadioButton($"{label}##new_tpl{i}", ref _newTemplate, i);
                ImGui.Indent(24f);
                ImGuiWidgets.TextMuted(description);
                ImGui.Unindent(24f);
            }

            ImGui.Spacing();
            string error = ValidateNewName(_newName.ToString(), extension, out string normalized, out string path);
            if (error.Length == 0 && normalized.Length > 0)
            {
                ImGuiWidgets.TextMuted($"Creates: scripts/{normalized}{extension}");
            }
            else if (error.Length > 0)
            {
                ImGuiWidgets.TextColoredWrapped(ImGuiWidgets.WarningColor, error);
            }
            else
            {
                ImGui.Text(" ");
            }

            if (_newCreateError.Length > 0)
            {
                ImGuiWidgets.TextColoredWrapped(ImGuiWidgets.ErrorColor, _newCreateError);
            }

            ImGui.Separator();
            bool canCreate = error.Length == 0 && normalized.Length > 0;
            if (ImGuiWidgets.Button("Create##new_create", canCreate, null, new float2(140f, 0f)))
            {
                string created = CreateScript(normalized, extension, language, path);
                if (created.Length > 0)
                {
                    _newCreateError = created;
                }
                else
                {
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##new_cancel", new float2(120f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    /// <summary>
    ///     Validates the typed script name. Returns an error text (empty when fine, or when nothing was typed
    ///     yet); <paramref name="normalized"/> is the catalog name without extension and <paramref name="fullPath"/>
    ///     the target file.
    /// </summary>
    private string ValidateNewName(string raw, string extension, out string normalized, out string fullPath)
    {
        normalized = "";
        fullPath = "";

        string name = raw.Trim().Replace('\\', '/').Trim('/');
        foreach (string known in new[] { ".lua", ".ts" })
        {
            if (name.EndsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^known.Length];
                break;
            }
        }

        if (name.Length == 0)
        {
            return "";
        }

        if (name.Length > 100)
        {
            return "The name is too long (100 characters at most).";
        }

        string[] segments = name.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0)
            {
                return "Empty folder name in the path.";
            }

            if (segment[0] == '.' || segment[^1] == '.')
            {
                return "Names and folders cannot start or end with a dot.";
            }

            foreach (char c in segment)
            {
                bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '.';
                if (!ok)
                {
                    return $"'{c}' is not allowed: use letters, digits, '_', '-' and '.' (and '/' for folders).";
                }
            }
        }

        if (segments.Length > 1 && segments[0] is "lib" or "stubs")
        {
            return "The top-level folders 'lib' and 'stubs' are reserved (never scanned).";
        }

        if (name.EndsWith(".d", StringComparison.OrdinalIgnoreCase))
        {
            return "Names ending in '.d' would be treated as declaration files (.d.ts).";
        }

        normalized = name;
        fullPath = _host.Paths.ScriptPath(name + extension);

        string root = Path.GetFullPath(_host.Paths.ScriptsDir);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalized = "";
            return "The name resolves outside the scripts folder.";
        }

        if (File.Exists(fullPath))
        {
            return $"'{name}{extension}' already exists.";
        }

        return "";
    }

    /// <summary>Writes the template file; returns an error text, or empty on success (then the follow-up catalog work is posted).</summary>
    private string CreateScript(string normalized, string extension, string language, string fullPath)
    {
        try
        {
            ScriptTemplateKind kind = ScriptTemplates.All[Math.Clamp(_newTemplate, 0, ScriptTemplates.All.Count - 1)].Kind;
            string text = ScriptTemplates.Render(kind, language, normalized);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (IOException ex)
        {
            return $"Could not create the file: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Could not create the file: {ex.Message}";
        }

        string name = normalized + extension;
        _selected = name;
        _hiddenUntil.Remove(name);
        _scriptsAt = double.NegativeInfinity;
        Post("scan", () => _host.Catalog.Scan());
        if (_host.Editor.IsAvailable)
        {
            OpenEditor(name, fullPath, null);
        }

        Log.Info($"Created script '{name}' from the {ScriptTemplates.All[Math.Clamp(_newTemplate, 0, ScriptTemplates.All.Count - 1)].Label} template.");
        return "";
    }
}
