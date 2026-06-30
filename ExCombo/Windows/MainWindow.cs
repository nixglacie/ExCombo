using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using ExCombo.Flow;

namespace ExCombo.Windows;

public class MainWindow : Window {
    private readonly Configuration  _config;
    private readonly FlowEditorWindow _editor;
    private readonly ConfigWindow   _configWindow;

    // Rename state
    private string  _pendingRename = "";
    private string? _renamingId;

    // New-flow dialog state
    private bool   _dlgVisible;
    private bool   _dlgFocusName;
    private string _newFlowName = "";
    private string _newFlowJob  = "";

    // Status
    private string? _statusMsg;
    private float   _statusTimer;
    private bool    _statusError;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Job icon data (icon IDs = 62000 + ClassJob.RowId) ────────────────
    private static readonly Dictionary<string, uint> JobIcons = new() {
        ["WAR"] = 62021, ["PLD"] = 62019, ["DRK"] = 62032, ["GNB"] = 62037,
        ["WHM"] = 62024, ["SCH"] = 62028, ["AST"] = 62033, ["SGE"] = 62040,
        ["MNK"] = 62020, ["DRG"] = 62022, ["NIN"] = 62030, ["SAM"] = 62034,
        ["RPR"] = 62039, ["VPR"] = 62041,
        ["BRD"] = 62023, ["MCH"] = 62031, ["DNC"] = 62038,
        ["BLM"] = 62025, ["SMN"] = 62027, ["RDM"] = 62035, ["PCT"] = 62042,
    };

    private static readonly (string Label, string[] Jobs)[] RoleGroups = [
        ("Tank",   ["WAR", "PLD", "DRK", "GNB"]),
        ("Healer", ["WHM", "SCH", "AST", "SGE"]),
        ("Melee",  ["MNK", "DRG", "NIN", "SAM", "RPR", "VPR"]),
        ("Ranged", ["BRD", "MCH", "DNC"]),
        ("Caster", ["BLM", "SMN", "RDM", "PCT"]),
    ];

    private IDalamudTextureWrap? GetIconWrap(uint iconId) {
        if (iconId == 0) return null;
        try { return Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty(); }
        catch { return null; }
    }

    // ─────────────────────────────────────────────────────────────────────

    public MainWindow(Configuration config, FlowEditorWindow editor, ConfigWindow configWindow)
        : base("ExCombo###ExComboMain") {
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(440, 180),
            MaximumSize = new Vector2(720, 900),
        };
        _config       = config;
        _editor       = editor;
        _configWindow = configWindow;
    }

    public override void PreDraw()  => Style.Push();
    public override void PostDraw() => Style.Pop();

    public override void Draw() {
        // ── Toolbar ──────────────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.455f, 0.765f, 1.000f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.592f, 0.831f, 1.000f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.350f, 0.650f, 0.900f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(14f, 6f));
        if (ImGui.Button("+ New Flow")) {
            _newFlowName  = "";
            _newFlowJob   = "";
            _dlgVisible   = true;
            _dlgFocusName = true;
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        ImGui.SameLine(0, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 6f));
        if (ImGui.Button("Import")) TryImport();
        ImGui.PopStyleVar();

        // Settings button — right-aligned
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(7f, 6f));
            float btnW = ImGui.GetFontSize() + 14f; // icon + 2*framePadX
            float padX = ImGui.GetStyle().WindowPadding.X;
            ImGui.SameLine(ImGui.GetWindowWidth() - padX - btnW);
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.173f, 0.180f, 0.200f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.333f, 0.353f, 0.388f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.455f, 0.765f, 1f, 0.20f));
            ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.565f, 0.573f, 0.588f, 1f));
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
                _configWindow.IsOpen = !_configWindow.IsOpen;
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar();
            if (ImGui.IsItemHovered()) Tip("Settings");
        }

        if (_statusMsg != null) {
            _statusTimer -= ImGui.GetIO().DeltaTime;
            if (_statusTimer <= 0f) { _statusMsg = null; }
            else {
                ImGui.SameLine(0, 12f);
                var sa = Math.Min(1f, _statusTimer);
                if (_statusError) {
                    ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.1f, sa), FontAwesomeIcon.ExclamationTriangle.ToIconString());
                    ImGui.PopFont();
                    ImGui.SameLine(0, 6f);
                }
                var sc = _statusError ? new Vector4(1f, 0.45f, 0.45f, sa)
                                      : new Vector4(0.635f, 0.855f, 0.549f, sa);
                ImGui.TextColored(sc, _statusMsg);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 5f)); // top padding before list

        // ── Flow list ─────────────────────────────────────────────────────
        const float ListIconSz = 22f;
        const float RowPadX    = 8f;
        const float RowPadY    = 6f;
        const float RowGap     = 5f;

        const float ListIndent = 10f;

        var dl   = ImGui.GetWindowDrawList();
        var rowH = Math.Max(ImGui.GetFrameHeight(), ListIconSz) + RowPadY * 2f;

        string? toDelete = null;
        string? toEdit   = null;

        // Bucket flows by job, preserving RoleGroups job order
        var jobOrder = RoleGroups.SelectMany(g => g.Jobs).ToList();
        var byJob    = _config.Flows
            .GroupBy(f => f.Job.Length > 0 ? f.Job : "?")
            .ToDictionary(g => g.Key, g => g.ToList());
        var orderedJobs = jobOrder
            .Where(j => byJob.ContainsKey(j))
            .Concat(byJob.Keys.Where(k => !jobOrder.Contains(k)).OrderBy(k => k))
            .ToList();

        bool anyFlow = false;
        foreach (var jobName in orderedJobs) {
            var groupFlows = byJob[jobName];
            anyFlow = true;

            var  hdrScrPos = ImGui.GetCursorScreenPos();
            float hH       = ImGui.GetFrameHeight();

            ImGui.PushStyleColor(ImGuiCol.Header,        new Vector4(0.173f, 0.180f, 0.200f, 1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.200f, 0.210f, 0.235f, 1f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive,  new Vector4(0.220f, 0.230f, 0.258f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.565f, 0.573f, 0.588f, 1f));
            bool grpOpen = ImGui.CollapsingHeader(
                $"      {jobName}  ({groupFlows.Count})##grp_{jobName}",
                ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.PopStyleColor(4);

            // Overlay job icon on header (after the arrow indicator square)
            if (JobIcons.TryGetValue(jobName, out var hdrIconId)) {
                var hdrWrap = GetIconWrap(hdrIconId);
                if (hdrWrap != null) {
                    float iSz = hH - 4f;
                    var   p0  = new Vector2(hdrScrPos.X + hH + ImGui.GetStyle().ItemInnerSpacing.X, hdrScrPos.Y + 2f);
                    dl.AddImage(hdrWrap.Handle, p0, p0 + new Vector2(iSz, iSz));
                }
            }

            if (!grpOpen) continue;

            ImGui.Dummy(new Vector2(0f, 2f));
            ImGui.Indent(ListIndent);

            foreach (var flow in groupFlows) {
                ImGui.PushID(flow.Id);

                bool isActive = _editor.ActiveFlowId == flow.Id && _editor.IsOpen;

                var   startCursorPos = ImGui.GetCursorPos();
                float availW         = ImGui.GetContentRegionAvail().X;
                var   rowMin         = ImGui.GetCursorScreenPos() - new Vector2(RowPadX, RowPadY);
                var   rowMax         = new Vector2(rowMin.X + availW + RowPadX, rowMin.Y + rowH);

                var mouse = ImGui.GetMousePos();
                bool hover = mouse.X >= rowMin.X && mouse.X <= rowMax.X
                          && mouse.Y >= rowMin.Y && mouse.Y <= rowMax.Y;

                uint rowBg   = isActive ? Col(0.455f, 0.765f, 1f, 0.08f)
                             : hover    ? Col(0.173f, 0.180f, 0.200f, 0.9f)
                                        : Col(0.145f, 0.149f, 0.169f, 0.7f);
                uint rowBord = isActive ? Col(0.455f, 0.765f, 1f, 0.40f)
                                        : Col(0.333f, 0.353f, 0.388f, 0.6f);

                dl.AddRectFilled(rowMin, rowMax, rowBg,   6f);
                dl.AddRect      (rowMin, rowMax, rowBord, 6f, ImDrawFlags.None, 1f);

                float midY    = rowMin.Y + rowH / 2f;
                float fh      = ImGui.GetFrameHeight();
                var   winPos  = ImGui.GetWindowPos();
                float scrollY = ImGui.GetScrollY();

                ImGui.SetCursorPos(new Vector2(startCursorPos.X, startCursorPos.Y));

                // ── Enable toggle ──────────────────────────────────────────
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
                var enabled = flow.Enabled;
                if (ImGui.Checkbox($"##en_{flow.Id}", ref enabled)) {
                    if (enabled) {
                        var c = FindTriggerConflict(flow);
                        if (c is { } hit)
                            SetStatus($"Can't enable \"{flow.Name}\": trigger {hit.label} is already used by active flow \"{hit.other.Name}\" — disable it first.", error: true);
                        else { flow.Enabled = true; _config.Save(); }
                    } else {
                        flow.Enabled = false; _config.Save();
                    }
                }
                ImGui.PopStyleVar();
                ImGui.SameLine(0, 6f);

                // ── Job icon ───────────────────────────────────────────────
                if (flow.Job.Length > 0 && JobIcons.TryGetValue(flow.Job, out var jobIconId)) {
                    var wrap = GetIconWrap(jobIconId);
                    if (wrap != null) {
                        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, midY - ListIconSz / 2f));
                        ImGui.Image(wrap.Handle, new Vector2(ListIconSz, ListIconSz));
                        ImGui.SameLine(0, 6f);
                    }
                }

                // ── Name / rename ──────────────────────────────────────────
                if (_renamingId == flow.Id) {
                    ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, midY - ImGui.GetFrameHeight() / 2f));
                    ImGui.SetNextItemWidth(160f);
                    if (ImGui.InputText("##rename", ref _pendingRename, 64, ImGuiInputTextFlags.EnterReturnsTrue))
                        CommitRename(flow);
                    if (ImGui.IsKeyPressed(ImGuiKey.Escape)) _renamingId = null;
                    ImGui.SameLine(0, 4f);
                    if (ImGui.SmallButton("OK")) CommitRename(flow);
                } else {
                    var textSz  = ImGui.CalcTextSize(flow.Name);
                    var namePos = new Vector2(ImGui.GetCursorScreenPos().X, midY - textSz.Y / 2f);
                    float nr = isActive ? 0.455f : 0.827f;
                    float ng = isActive ? 0.765f : 0.831f;
                    float nb = isActive ? 1.000f : 0.839f;
                    dl.AddText(namePos, ImGui.GetColorU32(new Vector4(nr, ng, nb, 1f)), flow.Name);

                    ImGui.SetCursorScreenPos(namePos);
                    ImGui.InvisibleButton($"##name_{flow.Id}", textSz);
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
                        _renamingId    = flow.Id;
                        _pendingRename = flow.Name;
                    }

                    // ── Conflict warning (disabled flow blocked by an active one) ──
                    if (!flow.Enabled && FindTriggerConflict(flow) is { } cf) {
                        ImGui.SameLine(0, 8f);
                        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
                        var icoStr = FontAwesomeIcon.ExclamationTriangle.ToIconString();
                        var icoSz  = ImGui.CalcTextSize(icoStr);
                        var icoPos = new Vector2(ImGui.GetCursorScreenPos().X, midY - icoSz.Y / 2f);
                        dl.AddText(icoPos, ImGui.GetColorU32(new Vector4(1f, 0.6f, 0.1f, 1f)), icoStr);
                        ImGui.PopFont();
                        ImGui.SetCursorScreenPos(icoPos);
                        ImGui.InvisibleButton($"##warn_{flow.Id}", icoSz);
                        if (ImGui.IsItemHovered())
                            Tip($"Conflicts with active flow \"{cf.other.Name}\" (trigger {cf.label}). Disable it to enable this one.");
                    }
                }

                // ── Action buttons (right-aligned, icon-only) ──────────────
                const float IcoSz  = 26f;
                const float IcoGap = 4f;
                float winW  = ImGui.GetWindowWidth();
                float padX  = ImGui.GetStyle().WindowPadding.X;
                float delX  = winW - padX - RowPadX - IcoSz;
                float expX  = delX  - IcoGap - IcoSz;
                float editX = expX  - IcoGap - IcoSz;
                float btnY  = midY - IcoSz / 2f - winPos.Y + scrollY;

                var icoSize = new Vector2(IcoSz, IcoSz);

                // ── Trigger action icons (left of the edit button) ─────────
                const float TrigSz  = 26f;
                const float TrigGap = 4f;
                var trigNodes = flow.Nodes.FindAll(n => n.Type == NodeType.Trigger && n.IconId != 0);
                float trigTotalW = trigNodes.Count * TrigSz + Math.Max(0, trigNodes.Count - 1) * TrigGap;
                float trigStartX = editX - 10f - trigTotalW;
                float trigY      = midY - TrigSz / 2f - winPos.Y + scrollY;
                for (var ti = 0; ti < trigNodes.Count; ti++) {
                    var tw = GetIconWrap(trigNodes[ti].IconId);
                    if (tw == null) continue;
                    ImGui.SetCursorPos(new Vector2(trigStartX + ti * (TrigSz + TrigGap), trigY));
                    var p0 = ImGui.GetCursorScreenPos();
                    var p1 = p0 + new Vector2(TrigSz, TrigSz);
                    dl.AddImageRounded(tw.Handle, p0, p1, Vector2.Zero, Vector2.One, 0xFFFFFFFFu, 5f);
                    dl.AddRect(p0, p1, Col(0.333f, 0.353f, 0.388f, 0.8f), 5f, ImDrawFlags.None, 1f);
                    ImGui.InvisibleButton($"##trig_{flow.Id}_{ti}", new Vector2(TrigSz, TrigSz));
                    if (ImGui.IsItemHovered()) Tip(trigNodes[ti].ActionLabel);
                }

                // Edit (pen)
                ImGui.SetCursorPos(new Vector2(editX, btnY));
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.455f, 0.765f, 1f, 0.12f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.455f, 0.765f, 1f, 0.25f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.455f, 0.765f, 1f, 0.40f));
                ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.455f, 0.765f, 1f, 0.85f));
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Pen, icoSize)) toEdit = flow.Id;
                ImGui.PopStyleColor(4);
                if (ImGui.IsItemHovered()) Tip("Edit");

                // Export (file-export)
                ImGui.SetCursorPos(new Vector2(expX, btnY));
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.173f, 0.180f, 0.200f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.333f, 0.353f, 0.388f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.455f, 0.765f, 1f, 0.20f));
                ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.565f, 0.573f, 0.588f, 1f));
                if (ImGuiComponents.IconButton(FontAwesomeIcon.FileExport, icoSize)) ExportFlow(flow);
                ImGui.PopStyleColor(4);
                if (ImGui.IsItemHovered()) Tip("Export to clipboard");

                // Delete (trash)
                ImGui.SetCursorPos(new Vector2(delX, btnY));
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.173f, 0.180f, 0.200f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.522f, 0.569f, 0.18f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(1f, 0.522f, 0.569f, 0.35f));
                ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(1f, 0.522f, 0.569f, 0.80f));
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash, icoSize)) toDelete = flow.Id;
                ImGui.PopStyleColor(4);
                if (ImGui.IsItemHovered()) Tip("Delete");

                // Advance past row + gap (startCursorPos-based — fixes overlap)
                ImGui.SetCursorPos(new Vector2(startCursorPos.X, startCursorPos.Y + rowH + RowGap));

                ImGui.PopID();
            }

            ImGui.Unindent(ListIndent);
            ImGui.Dummy(new Vector2(0f, 3f));
        }

        if (toDelete != null) {
            _config.Flows.RemoveAll(f => f.Id == toDelete);
            if (_editor.ActiveFlowId == toDelete) _editor.IsOpen = false;
            _config.Save();
        }
        if (toEdit != null) {
            var f = _config.Flows.Find(f2 => f2.Id == toEdit);
            if (f != null) { _editor.SetFlow(f); _editor.IsOpen = true; }
        }

        if (!anyFlow) {
            ImGui.Spacing();
            ImGui.TextDisabled("No flows — click '+ New Flow' to create one.");
        }

        // ── New Flow dialog (non-blocking floating window) ────────────────
        if (_dlgVisible) {
            ImGui.SetNextWindowSizeConstraints(new Vector2(360f, 0f), new Vector2(360f, float.MaxValue));
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

            ImGui.Begin("##newFlowDlg", ref _dlgVisible,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse);

            ImGui.TextColored(new Vector4(0.455f, 0.765f, 1f, 1f), "New Flow");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text("Name:");
            ImGui.SetNextItemWidth(-1f);
            if (_dlgFocusName) { ImGui.SetKeyboardFocusHere(); _dlgFocusName = false; }
            ImGui.InputText("##nfname", ref _newFlowName, 64);

            ImGui.Spacing();
            ImGui.Text("Select job:");
            ImGui.Spacing();

            DrawJobPicker();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            bool canCreate = _newFlowName.Trim().Length > 0 && _newFlowJob.Length > 0;
            if (!canCreate) ImGui.BeginDisabled();
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.455f, 0.765f, 1f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.592f, 0.831f, 1f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
            if (ImGui.Button("Create", new Vector2(-1f, 0f))) {
                var flow = new ComboFlow {
                    Name    = _newFlowName.Trim(),
                    Job     = _newFlowJob,
                    Enabled = true,
                };
                _config.Flows.Add(flow);
                _config.Save();
                _editor.SetFlow(flow);
                _editor.IsOpen = true;
                _dlgVisible = false;
            }
            ImGui.PopStyleColor(3);
            if (!canCreate) ImGui.EndDisabled();

            ImGui.Spacing();
            if (ImGui.Button("Cancel", new Vector2(-1f, 0f))) _dlgVisible = false;

            ImGui.End();
        }
    }

    private void DrawJobPicker() {
        const float IconSz  = 34f;
        const float IconGap = 3f;

        var wdl = ImGui.GetWindowDrawList();

        foreach (var (label, abbrevs) in RoleGroups) {
            ImGui.TextDisabled(label);

            for (int ji = 0; ji < abbrevs.Length; ji++) {
                var abbrev = abbrevs[ji];
                bool sel   = _newFlowJob == abbrev;

                if (ji > 0) ImGui.SameLine(0, IconGap);

                var pos = ImGui.GetCursorScreenPos();

                // Selection background
                if (sel)
                    wdl.AddRectFilled(pos - new Vector2(2f, 2f),
                                      pos + new Vector2(IconSz + 2f, IconSz + 2f),
                                      Col(0.455f, 0.765f, 1f, 0.22f), 5f);

                // Icon or text fallback
                IDalamudTextureWrap? wrap = JobIcons.TryGetValue(abbrev, out var iid) ? GetIconWrap(iid) : null;
                if (wrap != null) {
                    ImGui.Image(wrap.Handle, new Vector2(IconSz, IconSz));
                } else {
                    ImGui.PushStyleColor(ImGuiCol.Button,
                        sel ? new Vector4(0.455f, 0.765f, 1f, 0.3f) : new Vector4(0.173f, 0.180f, 0.200f, 1f));
                    ImGui.Button(abbrev, new Vector2(IconSz, IconSz));
                    ImGui.PopStyleColor();
                }

                if (ImGui.IsItemClicked())
                    _newFlowJob = sel ? "" : abbrev;

                if (ImGui.IsItemHovered()) {
                    wdl.AddRectFilled(pos - new Vector2(2f, 2f),
                                      pos + new Vector2(IconSz + 2f, IconSz + 2f),
                                      Col(0.455f, 0.765f, 1f, 0.10f), 5f);
                    Tip(abbrev);
                }

                // Selection border
                if (sel)
                    wdl.AddRect(pos - new Vector2(2f, 2f),
                                pos + new Vector2(IconSz + 2f, IconSz + 2f),
                                Col(0.455f, 0.765f, 1f), 5f, ImDrawFlags.None, 2f);
            }
        }

    }

    private void CommitRename(ComboFlow flow) {
        if (_pendingRename.Trim().Length > 0) flow.Name = _pendingRename.Trim();
        _renamingId = null;
        _config.Save();
    }

    private void ExportFlow(ComboFlow flow) {
        try   { ImGui.SetClipboardText(JsonSerializer.Serialize(flow, JsonOpts)); SetStatus("Copied!"); }
        catch { SetStatus("Export failed."); }
    }

    private void TryImport() {
        try {
            var imported = JsonSerializer.Deserialize<ComboFlow>(ImGui.GetClipboardText(), JsonOpts);
            if (imported == null) { SetStatus("Nothing valid in clipboard."); return; }
            imported.Id   = Guid.NewGuid().ToString();
            imported.Name = imported.Name.EndsWith(" (imported)") ? imported.Name : imported.Name + " (imported)";
            _config.Flows.Add(imported);
            _config.Save();
            SetStatus($"Imported \"{imported.Name}\"");
        } catch {
            SetStatus("Clipboard doesn't contain a valid flow.");
        }
    }

    // Tooltip with a visible border (the theme's PopupBorderSize is 0).
    private static void Tip(string text) {
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.40f, 0.42f, 0.46f, 1f));
        ImGui.SetTooltip(text);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void SetStatus(string msg, bool error = false) {
        _statusMsg   = msg;
        _statusTimer = error ? 5f : 3f;
        _statusError = error;
    }

    private static HashSet<uint> TriggerActionIds(ComboFlow flow) {
        var s = new HashSet<uint>();
        foreach (var n in flow.Nodes)
            if (n.Type == NodeType.Trigger && n.ActionId != 0) s.Add(n.ActionId);
        return s;
    }

    // First active same-job flow that shares a trigger with `flow`, plus the shared action's label.
    private (ComboFlow other, string label)? FindTriggerConflict(ComboFlow flow) {
        var mine = TriggerActionIds(flow);
        if (mine.Count == 0) return null;
        foreach (var other in _config.Flows) {
            if (other.Id == flow.Id || !other.Enabled || other.Job != flow.Job) continue;
            foreach (var n in other.Nodes) {
                if (n.Type != NodeType.Trigger || n.ActionId == 0) continue;
                if (mine.Contains(n.ActionId))
                    return (other, n.ActionLabel.Length > 0 ? n.ActionLabel : n.ActionId.ToString());
            }
        }
        return null;
    }

    private static uint Col(float r, float g, float b, float a = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
