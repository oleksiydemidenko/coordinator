using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace Coordinator.Editor
{
    internal sealed class CoordinatorDeferredWork
    {
        private const double BatchBudgetMs = 30.0;
        private const double GlideSeconds = 0.15;
        private const float GlideRate = 18f;
        private const float MaxFrameStep = 1f / 30f;

        private readonly EditorWindow owner;
        private IEnumerator<float> routine;
        private Action whenDone;
        private bool painted;
        private bool barVisible;
        private bool finished;
        private bool batchedOnce;
        private float target;
        private float shown;
        private double lastTick;
        private double targetChangedAt;
        private double workSeconds;

        public CoordinatorDeferredWork(EditorWindow owner)
        {
            this.owner = owner;
        }

        public bool Pending => routine != null;
        public bool Blocking => routine != null && !Quiet;
        public bool Quiet { get; private set; }
        public string Label { get; set; } = string.Empty;
        public float Progress => shown;
        public double LastSeconds { get; private set; } = -1.0;

        public void Request(string label, Action step, bool quiet = false) => Run(label, Single(step), null, quiet);

        public void Run(string label, IEnumerator<float> work, Action onDone = null, bool quiet = false)
        {
            if (routine == null)
            {
                painted = false;
                barVisible = false;
                shown = 0f;
                target = 0f;
                batchedOnce = false;
                workSeconds = 0.0;
            }
            Quiet = quiet;
            routine?.Dispose();
            Label = label;
            routine = work;
            whenDone = onDone;
            finished = false;
            lastTick = targetChangedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            owner.Repaint();
        }

        public void Cancel()
        {
            routine?.Dispose();
            routine = null;
            whenDone = null;
            EditorApplication.update -= Tick;
        }

        public void MarkPainted()
        {
            if (Event.current.type == EventType.Repaint)
                painted = true;
        }

        public void DrawStatus()
        {
            if (routine == null)
                return;
            if (Quiet)
            {
                MarkPainted();
                return;
            }
            var rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            rect.x += 4f;
            rect.width = Mathf.Max(0f, rect.width - 8f);
            rect.y += 2f;
            rect.height -= 4f;
            DrawBar(rect);
            ConsumeInput();
        }

        public void DrawBar(Rect rect)
        {
            MarkPainted();
            if (Event.current.type != EventType.Repaint)
                return;
            barVisible = true;
            EditorGUI.ProgressBar(rect, shown, Label + "…");
        }

        private static void ConsumeInput()
        {
            var current = Event.current;
            if (current == null || current.type == EventType.Layout || current.type == EventType.Repaint || current.type == EventType.Used)
                return;
            if (current.isMouse
                || current.isKey
                || current.type == EventType.DragUpdated
                || current.type == EventType.DragPerform
                || current.type == EventType.DragExited
                || current.type == EventType.ValidateCommand
                || current.type == EventType.ExecuteCommand)
                current.Use();
        }

        private static IEnumerator<float> Single(Action step)
        {
            step();
            yield return 1f;
        }

        private void Tick()
        {
            if (routine == null)
            {
                EditorApplication.update -= Tick;
                return;
            }
            if (owner == null)
            {
                Cancel();
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            var dt = Mathf.Min((float)(now - lastTick), MaxFrameStep);
            lastTick = now;
            if (shown < target)
            {
                shown = Mathf.Min(target, shown + (target - shown) * (1f - Mathf.Exp(-GlideRate * dt)) + 0.25f * dt);
                if (target - shown < 0.005f)
                    shown = target;
                owner.Repaint();
            }

            if (!painted && !Quiet)
                return;
            var gliding = barVisible && shown < target && now - targetChangedAt < GlideSeconds;
            if (gliding)
                return;

            if (finished)
            {
                Complete();
                return;
            }

            painted = false;
            var watch = Stopwatch.StartNew();
            var before = target;
            try
            {
                do
                {
                    if (!routine.MoveNext())
                    {
                        finished = true;
                        target = 1f;
                        break;
                    }
                    target = Mathf.Clamp(Mathf.Max(target, routine.Current), 0f, 1f);
                }
                while (watch.Elapsed.TotalMilliseconds < BatchBudgetMs);
                workSeconds += watch.Elapsed.TotalSeconds;
            }
            catch
            {
                var onDone = whenDone;
                Cancel();
                onDone?.Invoke();
                owner.Repaint();
                throw;
            }

            if (finished && (!batchedOnce || Quiet))
            {
                Complete();
                return;
            }
            batchedOnce = true;
            if (Quiet)
                return;
            if (target > before)
                targetChangedAt = EditorApplication.timeSinceStartup;
            lastTick = EditorApplication.timeSinceStartup;
            owner.Repaint();
        }

        private void Complete()
        {
            EditorApplication.update -= Tick;
            routine?.Dispose();
            routine = null;
            var onDone = whenDone;
            whenDone = null;
            shown = target = 1f;
            LastSeconds = workSeconds;
            onDone?.Invoke();
            if (owner != null)
                owner.Repaint();
        }
    }
}
