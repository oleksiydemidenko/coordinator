using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Coordinator.Editor
{
    public sealed class CoordinatorWindow : EditorWindow
    {
        private const float ColumnWidth = 250f;
        private const float DetailWidth = 420f;
        private CoordinatorWorkspace workspace;
        private IReadOnlyList<CoordinatorDiagnostic> diagnostics = Array.Empty<CoordinatorDiagnostic>();
        private readonly Dictionary<string, CoordinatorTaskStatus> statuses = new(StringComparer.Ordinal);
        private IReadOnlyList<CoordinatorBoardColumn> columns = Array.Empty<CoordinatorBoardColumn>();
        private string loadError = string.Empty;
        private string notice = string.Empty;
        [SerializeField] private string taskFilter = string.Empty;
        [SerializeField] private bool showFinished = true;
        [SerializeField] private bool showDiagnostics;
        [SerializeField] private string openCardKey = string.Empty;
        private string openCardText = string.Empty;
        private Vector2 boardScroll;
        private Vector2 detailScroll;
        private Vector2 diagnosticsScroll;
        private GUIStyle cardStyle;
        private GUIStyle selectedCardStyle;
        private GUIStyle detailStyle;

        [MenuItem("Window/Coordinator")]
        public static void Open()
        {
            var window = GetWindow<CoordinatorWindow>();
            window.titleContent = new GUIContent("Coordinator");
            window.minSize = new Vector2(560f, 320f);
            window.Show();
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        private const double QuietRefreshSeconds = 0.5;
        [NonSerialized] private CoordinatorDeferredWork refresh;
        [NonSerialized] private string lastFingerprint;

        private void OnEnable()
        {
            titleContent = new GUIContent("Coordinator");
            CoordinatorAgentBridge.Changed += OnDataChanged;
            CoordinatorHistory.Changed += Repaint;
            Reload(false);
        }

        private void OnDisable()
        {
            CoordinatorAgentBridge.Changed -= OnDataChanged;
            CoordinatorHistory.Changed -= Repaint;
            refresh?.Cancel();
        }

        private void OnFocus()
        {
            Reload(false);
        }

        private void OnDataChanged() => Reload(false);

        private void Reload(bool force)
        {
            refresh ??= new CoordinatorDeferredWork(this);
            if (force)
                lastFingerprint = null;
            var quiet = workspace != null && refresh.LastSeconds >= 0.0 && refresh.LastSeconds < QuietRefreshSeconds;
            refresh.Run("Reading tasks", Refresh(), null, quiet);
        }

        private IEnumerator<float> Refresh()
        {
            var git = new CoordinatorGit(ProjectRoot);
            string fingerprint = null;
            var error = Guarded(() =>
            {
                git.SnapshotRefs();
                fingerprint = git.RefsFingerprint + "\n" + DataFingerprint();
            });
            if (error == null && fingerprint == lastFingerprint)
                yield break;
            yield return 0.1f;

            CoordinatorWorkspace loaded = null;
            IReadOnlyList<CoordinatorDiagnostic> found = Array.Empty<CoordinatorDiagnostic>();
            var computed = new Dictionary<string, CoordinatorTaskStatus>(StringComparer.Ordinal);
            if (error == null)
            {
                refresh.Label = "Reading tasks";
                error = Guarded(() => loaded = CoordinatorWorkspace.Load(ProjectRoot));
                yield return 0.2f;
            }
            if (error == null)
            {
                refresh.Label = "Checking tasks";
                error = Guarded(() => found = CoordinatorValidation.Validate(loaded, git));
                yield return 0.35f;
            }
            if (error == null)
            {
                var tasks = loaded.Tasks.OrderBy(task => string.IsNullOrEmpty(taskFilter) || task.Task.id == taskFilter ? 0 : 1).ToArray();
                for (var index = 0; index < tasks.Length && error == null; index++)
                {
                    var task = tasks[index];
                    refresh.Label = $"Reading branches {index + 1} / {tasks.Length}";
                    error = Guarded(() => computed[task.Task.id] = CoordinatorStates.Compute(task, git));
                    yield return 0.35f + 0.65f * (index + 1f) / tasks.Length;
                }
            }

            loadError = error ?? string.Empty;
            if (loaded != null)
            {
                workspace = loaded;
                diagnostics = found;
                statuses.Clear();
                foreach (var pair in computed)
                    statuses[pair.Key] = pair.Value;
            }
            lastFingerprint = error == null ? fingerprint : null;
            CoordinatorGitMemo.Save();
            RebuildBoard();
        }

        private static string Guarded(Action step)
        {
            try
            {
                step();
                return null;
            }
            catch (Exception exception) when (exception is CoordinatorGitException or IOException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
            {
                return exception.Message;
            }
        }

        private static string DataFingerprint()
        {
            var text = new System.Text.StringBuilder();
            foreach (var folder in new[]
                     {
                         Path.Combine(ProjectRoot, CoordinatorPaths.RootFolder),
                         Path.Combine(ProjectRoot, CoordinatorArchitector.RootFolder)
                     })
            {
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var info = new FileInfo(file);
                    text.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
            }
            return text.ToString();
        }

        private void RebuildBoard()
        {
            columns = workspace == null ? Array.Empty<CoordinatorBoardColumn>() : CoordinatorBoard.Build(workspace, statuses, taskFilter, showFinished);
            var open = FindCard(openCardKey);
            openCardText = open == null ? string.Empty : CoordinatorCards.Assignment(workspace.Project, open.Task, open.Assignment, workspace.Architector);
            if (open == null)
                openCardKey = string.Empty;
            Repaint();
        }

        private CoordinatorBoardCard FindCard(string key) =>
            string.IsNullOrEmpty(key) ? null : columns.SelectMany(column => column.Cards).FirstOrDefault(card => card.Key == key);

        private void OnGUI()
        {
            EnsureStyles();
            refresh ??= new CoordinatorDeferredWork(this);
            using var locked = new EditorGUI.DisabledScope(refresh.Blocking);
            if (!refresh.Blocking)
                HandleUndoRedoShortcut();
            DrawToolbar();
            refresh.DrawStatus();
            if (loadError.Length > 0)
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
            if (notice.Length > 0)
                EditorGUILayout.HelpBox(notice, MessageType.Info);
            if (workspace == null)
                return;
            if (workspace.Tasks.Count == 0)
            {
                EditorGUILayout.HelpBox($"No tasks in {CoordinatorPaths.RootFolder}/{CoordinatorPaths.TasksFolder}.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                DrawBoard();
                if (openCardKey.Length > 0)
                    DrawDetail();
            }
            if (showDiagnostics)
                DrawDiagnostics();
        }

        private void DrawToolbar()
        {
            using var toolbar = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);
            if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60f)))
                Reload(true);
            using (new EditorGUI.DisabledScope(!CoordinatorHistory.CanUndo))
                if (GUILayout.Button(new GUIContent("Undo", CoordinatorHistory.CanUndo ? $"Undo {CoordinatorHistory.NextUndoName} (Ctrl+Z)" : "Nothing to undo"), EditorStyles.toolbarButton, GUILayout.Width(44f)))
                    UndoRedo(true);
            using (new EditorGUI.DisabledScope(!CoordinatorHistory.CanRedo))
                if (GUILayout.Button(new GUIContent("Redo", CoordinatorHistory.CanRedo ? $"Redo {CoordinatorHistory.NextRedoName} (Ctrl+Y)" : "Nothing to redo"), EditorStyles.toolbarButton, GUILayout.Width(44f)))
                    UndoRedo(false);
            if (workspace != null && workspace.Tasks.Count > 0)
            {
                var tasks = new[] { string.Empty }.Concat(workspace.Tasks.Select(task => task.Task.id)).ToArray();
                var labels = new[] { "All tasks" }.Concat(workspace.Tasks.Select(task => $"{task.Task.title} · {(statuses.TryGetValue(task.Task.id, out var status) ? status.State : "?")}")).ToArray();
                var index = Mathf.Max(0, Array.IndexOf(tasks, taskFilter));
                var chosen = EditorGUILayout.Popup(index, labels, EditorStyles.toolbarPopup, GUILayout.Width(260f));
                var finished = GUILayout.Toggle(showFinished, "Closed", EditorStyles.toolbarButton, GUILayout.Width(56f));
                if (chosen != index || finished != showFinished)
                {
                    taskFilter = tasks[chosen];
                    showFinished = finished;
                    RebuildBoard();
                }
                if (GUILayout.Button("Generate Cards", EditorStyles.toolbarButton, GUILayout.Width(104f)))
                    notice = $"{CoordinatorCards.WriteAll(workspace)} card file(s) written.";
                var selectedTask = workspace.FindTask(taskFilter);
                if (selectedTask != null && selectedTask.Task.approval == null && selectedTask.Task.cancelled == null
                    && GUILayout.Button("Approve…", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                    Approve(selectedTask);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Agents", EditorStyles.toolbarButton, GUILayout.Width(56f)))
                CoordinatorAgentsWindow.Open();
            var errors = diagnostics.Count(diagnostic => diagnostic.IsError);
            var warnings = diagnostics.Count - errors;
            showDiagnostics = GUILayout.Toggle(showDiagnostics, $"Diagnostics {errors}/{warnings}", EditorStyles.toolbarButton, GUILayout.Width(110f));
        }

        private void HandleUndoRedoShortcut()
        {
            var current = Event.current;
            if (current.type != EventType.KeyDown || !(current.control || current.command) || EditorGUIUtility.editingTextField)
                return;
            if (current.keyCode == KeyCode.Z && !current.shift)
            {
                UndoRedo(true);
                current.Use();
            }
            else if (current.keyCode == KeyCode.Y || current.keyCode == KeyCode.Z && current.shift)
            {
                UndoRedo(false);
                current.Use();
            }
        }

        private void UndoRedo(bool undo)
        {
            notice = undo ? CoordinatorHistory.Undo() : CoordinatorHistory.Redo();
            Reload(true);
            GUIUtility.ExitGUI();
        }

        private void Approve(CoordinatorTaskData task)
        {
            var errors = diagnostics.Count(diagnostic => diagnostic.IsError
                                                         && diagnostic.File.StartsWith(CoordinatorPaths.RepositoryTaskFolder(task.Task.id) + "/", StringComparison.Ordinal));
            var message = $"Approve the split of '{task.Task.title}' into {task.Assignments.Count} assignment(s)? Assignments whose decisions and inputs are ready can start."
                          + (errors > 0 ? $"\n\nThe task has {errors} validation error(s)." : string.Empty);
            if (!EditorUtility.DisplayDialog("Approve Task", message, "Approve", "Cancel"))
                return;
            var result = CoordinatorAgentApi.ApproveByPerson(ProjectRoot, task.Task.id, Environment.UserName);
            notice = result.Message;
            Reload(true);
        }

        private void DrawBoard()
        {
            boardScroll = EditorGUILayout.BeginScrollView(boardScroll, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (var column in columns)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(ColumnWidth), GUILayout.ExpandHeight(true)))
                    {
                        EditorGUILayout.LabelField($"{column.Title} ({column.Cards.Count})", EditorStyles.boldLabel);
                        foreach (var card in column.Cards)
                            DrawCard(card);
                        GUILayout.FlexibleSpace();
                    }
                }
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawCard(CoordinatorBoardCard card)
        {
            var selected = card.Key == openCardKey;
            using (new EditorGUILayout.VerticalScope(selected ? selectedCardStyle : cardStyle))
            {
                EditorGUILayout.LabelField(card.Assignment.title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(card.Task.Task.title, EditorStyles.miniLabel);
                var previous = GUI.contentColor;
                GUI.contentColor = StateColor(card.State);
                EditorGUILayout.LabelField(string.IsNullOrEmpty(card.State) ? "?" : card.State, EditorStyles.miniBoldLabel);
                GUI.contentColor = previous;
                var details = card.Status?.BlockedBy.Count > 0 ? string.Join("; ", card.Status.BlockedBy)
                    : !string.IsNullOrEmpty(card.Assignment.executor?.handle) ? card.Assignment.executor.handle
                    : card.Assignment.executor?.branch;
                if (!string.IsNullOrEmpty(details))
                    EditorGUILayout.LabelField(details, EditorStyles.wordWrappedMiniLabel);
            }
            var rect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && rect.Contains(Event.current.mousePosition))
            {
                openCardKey = selected ? string.Empty : card.Key;
                RebuildBoard();
                Event.current.Use();
            }
        }

        private void DrawDetail()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(DetailWidth), GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Card", EditorStyles.boldLabel);
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(24f)))
                    {
                        openCardKey = string.Empty;
                        RebuildBoard();
                        GUIUtility.ExitGUI();
                    }
                }
                detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
                GUILayout.Label(openCardText, detailStyle);
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawDiagnostics()
        {
            diagnosticsScroll = EditorGUILayout.BeginScrollView(diagnosticsScroll, GUILayout.MaxHeight(180f));
            if (diagnostics.Count == 0)
                EditorGUILayout.HelpBox("No diagnostics.", MessageType.Info);
            foreach (var diagnostic in diagnostics)
                EditorGUILayout.HelpBox($"{diagnostic.Code} · {diagnostic.File} · {diagnostic.Subject}\n{diagnostic.Message}",
                    diagnostic.IsError ? MessageType.Error : MessageType.Warning);
            EditorGUILayout.EndScrollView();
        }

        private void EnsureStyles()
        {
            cardStyle ??= new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(2, 2, 4, 4) };
            if (selectedCardStyle == null)
            {
                selectedCardStyle = new GUIStyle(cardStyle);
                var tint = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                tint.SetPixel(0, 0, EditorGUIUtility.isProSkin ? new Color(0.17f, 0.33f, 0.5f) : new Color(0.62f, 0.76f, 0.95f));
                tint.Apply();
                selectedCardStyle.normal.background = tint;
            }
            detailStyle ??= new GUIStyle(EditorStyles.wordWrappedLabel) { richText = false, font = EditorStyles.label.font };
        }

        private static Color StateColor(string state)
        {
            return state switch
            {
                CoordinatorAssignmentStates.Blocked => new Color(0.9f, 0.45f, 0.4f),
                CoordinatorAssignmentStates.Returned => new Color(0.95f, 0.6f, 0.3f),
                CoordinatorAssignmentStates.InProgress => new Color(0.45f, 0.7f, 1f),
                CoordinatorAssignmentStates.Delivered => new Color(0.75f, 0.6f, 1f),
                CoordinatorAssignmentStates.Accepted or CoordinatorAssignmentStates.Merged or CoordinatorAssignmentStates.Closed => new Color(0.45f, 0.85f, 0.5f),
                _ => EditorGUIUtility.isProSkin ? new Color(0.85f, 0.85f, 0.85f) : new Color(0.2f, 0.2f, 0.2f)
            };
        }
    }
}
