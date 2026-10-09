using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

// No network, clipboard access, chat submission, or background hotkey hook.
// StrokesPlus launches this only when the user presses the configured hotkey.
internal static class ReasoningSwitch
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    private static IntPtr targetWindow;
    private static string app = "";
    private static AutomationElement targetRoot;
    private static System.Windows.Rect chatInputBounds = System.Windows.Rect.Empty;
    private static System.Windows.Rect chatInputWindowBounds = System.Windows.Rect.Empty;
    private static StringBuilder trace = new StringBuilder();
    private static Stopwatch operationTimer = Stopwatch.StartNew();
    private const string BuildName = "20-wait-for-claude-controls";
    // Anchored: sidebar session titles may also contain the model name.
    private const string ClaudeModelPattern = @"^\s*(?:(?:Model|모델)\s*:?\s*(?:Claude\s+)?Opus\s+5\.5\b|(?:Claude\s+)?Opus\s+5\.5\s*$)";
    private static readonly ControlType[] choiceTypes = {
        ControlType.RadioButton, ControlType.MenuItem, ControlType.ListItem,
        ControlType.CheckBox, ControlType.Button
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--selftest")
        {
            try { return SelfTest(); }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-error.txt"), error.ToString());
                return 1;
            }
        }
        if (args.Length != 1 || (args[0] != "up" && args[0] != "down")) return 2;
        // Screenshot coordinates and cursor coordinates must use physical pixels.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        bool ownsLock;
        using (Mutex gate = new Mutex(true, "Local\\StrokesPlusReasoningSwitch", out ownsLock))
        {
            if (!ownsLock) return 0;
            try
            {
                targetWindow = GetForegroundWindow();
                uint pid;
                GetWindowThreadProcessId(targetWindow, out pid);
                app = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                if (app != "chatgpt" && app != "claude") return 0;
                // Require a stable release; a transient release can occur between chords.
                WaitForInputRelease();
                trace.AppendLine("Initial key release at " + operationTimer.ElapsedMilliseconds + " ms.");
                int direction = args[0] == "up" ? 1 : -1;
                AutomationElement root = AutomationElement.FromHandle(targetWindow);
                targetRoot = root;
                Stopwatch inputTimer = Stopwatch.StartNew();
                RememberChatInput(root);
                trace.AppendLine("Chat input lookup=" + inputTimer.ElapsedMilliseconds + " ms.");
                if (app == "chatgpt") SwitchCodex(root, direction);
                else SwitchClaude(root, direction);
                return 0;
            }
            catch (OperationCanceledException error)
            {
                trace.AppendLine(error.Message);
                Record("Stopped: input focus changed before the slider click.");
                return 0;
            }
            catch (Exception error)
            {
                WriteDiagnostic(error);
                Record("ERROR: " + error.Message);
                ShowNotice("추론 전환을 확인하지 못했습니다.\nReasoningHotkeys의 last-result.txt를 확인하세요.");
                return 1;
            }
            finally
            {
                gate.ReleaseMutex();
            }
        }
    }

    private static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
    private static bool InputHeld()
    {
        return IsDown(17) || IsDown(18) || IsDown(16) || IsDown(91) || IsDown(92) || IsDown(38) || IsDown(40);
    }
    private static void WaitForInputRelease()
    {
        Stopwatch timer = Stopwatch.StartNew();
        long releasedSince = -1;
        while (timer.ElapsedMilliseconds < 3000)
        {
            EnsureForeground();
            if (InputHeld()) releasedSince = -1;
            else
            {
                if (releasedSince < 0) releasedSince = timer.ElapsedMilliseconds;
                if (timer.ElapsedMilliseconds - releasedSince >= 20) return;
            }
            Thread.Sleep(5);
        }
        throw new InvalidOperationException("Release Ctrl, Alt and the arrow key, then press the hotkey again.");
    }
    private static void EnsureForeground()
    {
        if (GetForegroundWindow() != targetWindow) throw new InvalidOperationException("Active app changed; operation stopped.");
    }
    private static AutomationElement[] Elements(AutomationElement root, ControlType type)
    {
        Stopwatch queryTimer = Stopwatch.StartNew();
        System.Collections.Generic.List<AutomationElement> result = new System.Collections.Generic.List<AutomationElement>();
        {
            CacheRequest cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.IsEnabledProperty);
            cache.TreeScope = TreeScope.Element;
            using (cache.Activate())
            {
                AutomationElementCollection collection = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, type));
                foreach (AutomationElement element in collection)
                    try { if (!element.Cached.IsOffscreen && element.Cached.IsEnabled) result.Add(element); } catch { }
            }
        }
        trace.AppendLine("UIA " + type.ProgrammaticName + " lookup=" + queryTimer.ElapsedMilliseconds + " ms; visible=" + result.Count);
        return result.ToArray();
    }
    private static string Name(AutomationElement element)
    {
        try { return element.Cached.Name ?? ""; } catch { try { return element.Current.Name ?? ""; } catch { return ""; } }
    }
    private static string Describe(AutomationElement element)
    {
        string text = Name(element);
        foreach (AutomationElement child in Elements(element, ControlType.Text)) text += " " + Name(child);
        return text;
    }
    private static AutomationElement ModelButton(AutomationElement root, string pattern)
    {
        return ModelButton(Elements(root, ControlType.Button), pattern);
    }
    private static AutomationElement ModelButton(AutomationElement[] buttons, string pattern)
    {
        AutomationElement found = null;
        foreach (AutomationElement button in buttons)
        {
            string name = Name(button);
            bool matches = Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);
            if (!matches && (name.Length == 0 || Regex.IsMatch(name, @"model|effort|모델|추론", RegexOptions.IgnoreCase)))
                matches = Regex.IsMatch(Describe(button), pattern, RegexOptions.IgnoreCase);
            if (!matches) continue;
            if (found != null) throw new InvalidOperationException("More than one matching model control; no action taken.");
            found = button;
        }
        return found;
    }
    private static int Effort(string text)
    {
        if (Regex.IsMatch(text, @"\b(extra[\s-]*high|xhigh|엑스트라)\b|매우\s*높음|아주\s*높음", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(text, @"\bhigh\b|높음", RegexOptions.IgnoreCase)) return 1;
        if (Regex.IsMatch(text, @"\bmedium\b|중간|보통", RegexOptions.IgnoreCase)) return 0;
        return -1;
    }
    private static bool EffortOption(string text, int value)
    {
        string[] patterns = {
            @"^(medium|중간|보통)(\b|\s|$)",
            @"^(high|높음)(\b|\s|$)",
            @"^(extra[\s-]*high|xhigh|엑스트라|매우\s*높음|아주\s*높음)(\b|\s|$)"
        };
        return Regex.IsMatch(text.Trim(), patterns[value], RegexOptions.IgnoreCase);
    }
    private static bool IsEffortChoice(string name)
    {
        return EffortOption(name, 0) || EffortOption(name, 1) || EffortOption(name, 2);
    }
    private static int NextClaude(int current, int direction)
    {
        if (current < 0 || current > 5) throw new InvalidOperationException("Claude's current effort is not one of Low, Medium, High, Extra, Max, or Ultracode.");
        return Math.Max(0, Math.Min(5, current + direction));
    }
    private static int ClaudeEffort(string text)
    {
        if (Regex.IsMatch(text, @"\bultra[\s-]*code\b|울트라[\s-]*코드", RegexOptions.IgnoreCase)) return 5;
        if (Regex.IsMatch(text, @"\b(max|maximum)\b|최대", RegexOptions.IgnoreCase)) return 4;
        if (Regex.IsMatch(text, @"\b(extra(?:[\s-]*high)?|xhigh|엑스트라)\b|매우\s*높음|아주\s*높음", RegexOptions.IgnoreCase)) return 3;
        if (Regex.IsMatch(text, @"\bhigh\b|높음", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(text, @"\bmedium\b|중간|보통", RegexOptions.IgnoreCase)) return 1;
        if (Regex.IsMatch(text, @"\blow\b|낮음", RegexOptions.IgnoreCase)) return 0;
        return -1;
    }
    private static int GptEffort(string text)
    {
        if (Regex.IsMatch(text, @"\bultra\b|울트라", RegexOptions.IgnoreCase)) return 4;
        if (Regex.IsMatch(text, @"\b(extra[\s-]*high|xhigh)\b|매우\s*높음|아주\s*높음", RegexOptions.IgnoreCase)) return 3;
        if (Regex.IsMatch(text, @"\bhigh\b|높음", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(text, @"\bmedium\b|중간|보통", RegexOptions.IgnoreCase)) return 1;
        if (Regex.IsMatch(text, @"\b(light|low)\b|낮음|가벼움", RegexOptions.IgnoreCase)) return 0;
        return -1;
    }
    private static int NextGpt(int current, int direction)
    {
        if (current < 0 || current > 4) throw new InvalidOperationException("GPT's current effort is not Light, Medium, High, Extra High, or Ultra.");
        return Math.Max(0, Math.Min(4, current + direction));
    }
    private static string GptLabel(int value) { return new[] { "Light", "Medium", "High", "Extra High", "Ultra" }[value]; }
    private static AutomationElement CodexModelButton(AutomationElement root)
    {
        return CodexModelButton(Elements(root, ControlType.Button));
    }
    private static AutomationElement CodexModelButton(AutomationElement[] buttons)
    {
        // The popup also has a model link. Require effort text to select the composer button.
        const string model = @"(?:GPT[\s-]*)?6\.1[\s-]+Sol\b";
        AutomationElement found = null;
        foreach (AutomationElement button in buttons)
        {
            string description = Name(button);
            if (!Regex.IsMatch(description, model, RegexOptions.IgnoreCase) &&
                (description.Length == 0 || Regex.IsMatch(description, @"model|모델", RegexOptions.IgnoreCase)))
                description = Describe(button);
            if (Regex.IsMatch(description, model, RegexOptions.IgnoreCase) && GptEffort(description) < 0)
                description = Describe(button);
            if (!Regex.IsMatch(description, model, RegexOptions.IgnoreCase) || GptEffort(description) < 0) continue;
            if (found != null) throw new InvalidOperationException("More than one GPT model/effort control; no action taken.");
            found = button;
        }
        return found;
    }
    private static void SwitchCodex(AutomationElement root, int direction)
    {
        EnsureForeground();
        int current;
        bool wasOpen = GptImageSlider.TryReadCurrent(targetWindow, out current);
        AutomationElement buttonModel = null;
        trace.AppendLine("GPT popup initially open=" + wasOpen + ", thumb=" + current);
        // An open popup can hide the composer's controls from accessibility.
        // Its exact GPT-6.1 Sol image identifies the intended model directly.
        if (!wasOpen)
        {
            bool codexMode = false;
            AutomationElement[] buttons = Elements(root, ControlType.Button);
            foreach (AutomationElement button in buttons)
                if (Regex.IsMatch(Name(button), @"(mode|모드).*Codex", RegexOptions.IgnoreCase)) codexMode = true;
            trace.AppendLine("Codex mode=" + codexMode);
            if (!codexMode) throw new InvalidOperationException("The active ChatGPT window must be in Codex mode.");
            buttonModel = CodexModelButton(buttons);
        }
        Stopwatch readTimer = Stopwatch.StartNew();
        if (wasOpen)
        {
            while (current < 0 && readTimer.ElapsedMilliseconds < 1400)
            {
                EnsureForeground();
                Thread.Sleep(20);
                if (!GptImageSlider.TryReadCurrent(targetWindow, out current))
                    throw new InvalidOperationException("GPT popup closed while reading its thumb; no click sent.");
            }
            if (current < 0) throw new InvalidOperationException("Cannot identify one GPT slider thumb; no click sent.");
            trace.AppendLine("Current read from already open GPT popup: " + GptLabel(current));
        }
        else
        {
            do
            {
                EnsureForeground();
                if (buttonModel != null) break;
                buttonModel = CodexModelButton(root);
                if (buttonModel != null) break;
                Thread.Sleep(20);
            } while (readTimer.ElapsedMilliseconds < 1400);
            if (buttonModel == null) throw new InvalidOperationException("GPT 6.1 Sol is not selected, or its model control is unavailable.");
            string modelDescription = Name(buttonModel);
            if (GptEffort(modelDescription) < 0) modelDescription = Describe(buttonModel);
            current = GptEffort(modelDescription);
            trace.AppendLine("model=" + modelDescription + ", current=" + current);
        }
        int target = NextGpt(current, direction);
        if (target == current)
        {
            FinishWithInputFocus("GPT 6.1 Sol: " + GptLabel(target) + " (unchanged)");
            return;
        }
        EnsureForeground();
        if (!wasOpen)
        {
            // Invoke produces a synthetic click and skips the app's pointer-down
            // handler. Open exactly as a mouse click so positioning/focus agree.
            ClickModelButton(buttonModel);
        }
        ClickPoint(delegate
        {
            System.Drawing.Point freshPoint;
            Stopwatch openTimer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                if (GptImageSlider.TryLocate(targetWindow, current, target, out freshPoint))
                {
                    trace.AppendLine("StrokesPlus image match succeeded; target=" + GptLabel(target));
                    return freshPoint;
                }
                Thread.Sleep(20);
            } while (openTimer.ElapsedMilliseconds < 1600);
            throw new InvalidOperationException("GPT's slider image was not found; no click sent. Check theme and display scale.");
        }, true);
        // Confirm the changed thumb before dismissing the popup and restoring
        // the composer. Do not inject Escape or force keyboard focus.
        Stopwatch verifyTimer = Stopwatch.StartNew();
        int confirmed = -1;
        do
        {
            EnsureForeground();
            bool popupVisible = GptImageSlider.TryReadCurrent(targetWindow, out confirmed);
            if (!popupVisible)
            {
                // If the app itself dismisses the popup, use the restored composer label.
                confirmed = CurrentButtonEffort(buttonModel, true);
                if (confirmed < 0)
                {
                    buttonModel = CodexModelButton(root);
                    confirmed = CurrentButtonEffort(buttonModel, true);
                }
            }
            if (confirmed == target) break;
            Thread.Sleep(20);
        } while (verifyTimer.ElapsedMilliseconds < 2000);
        trace.AppendLine("confirmed=" + confirmed);
        if (confirmed != target) throw new InvalidOperationException("GPT's slider click did not confirm " + GptLabel(target) + ".");
        FinishWithInputFocus("GPT 6.1 Sol: " + GptLabel(target) + " (image verified)");
    }
    private static bool IsModelControl(string name)
    {
        return Regex.IsMatch(name, @"^\s*(?:Model|모델)\s*:", RegexOptions.IgnoreCase);
    }
    private static bool HasModelControl(AutomationElement[] buttons)
    {
        foreach (AutomationElement button in buttons) if (IsModelControl(Name(button))) return true;
        return false;
    }
    private static void SwitchClaude(AutomationElement root, int direction)
    {
        // Chromium builds its accessibility tree on request, so the first lookup
        // can return only the window frame. Wait for the composer's model control.
        AutomationElement[] buttons;
        AutomationElement buttonModel;
        Stopwatch treeTimer = Stopwatch.StartNew();
        int lookups = 0;
        do
        {
            EnsureForeground();
            buttons = Elements(root, ControlType.Button);
            lookups++;
            buttonModel = ModelButton(buttons, ClaudeModelPattern);
            if (buttonModel != null || HasModelControl(buttons)) break;
            Thread.Sleep(50);
        } while (treeTimer.ElapsedMilliseconds < 1500);
        if (lookups > 1) trace.AppendLine("Claude controls lookups=" + lookups + ", waited=" + treeTimer.ElapsedMilliseconds + " ms.");
        if (buttonModel == null) throw new InvalidOperationException("Opus 5.5 is not selected, or its model control is unavailable.");
        trace.AppendLine("model=" + Name(buttonModel));
        AutomationElement trigger = ClaudeEffortTrigger(buttons);
        if (trigger == null) throw new InvalidOperationException("Claude's effort button is unavailable or ambiguous.");
        int current = ClaudeEffort(Name(trigger));
        trace.AppendLine("effort trigger=" + Name(trigger) + ", current=" + current);
        int target = NextClaude(current, direction);
        if (target == current) { FinishWithInputFocus("Opus 5.5: " + Label(target) + " (unchanged)"); return; }

        System.Windows.Rect triggerBounds = trigger.Current.BoundingRectangle;
        if (!triggerBounds.IsEmpty)
            ClaudeImageSlider.ExpectNearButton(targetWindow, new System.Drawing.Rectangle((int)Math.Floor(triggerBounds.X),
                (int)Math.Floor(triggerBounds.Y), (int)Math.Ceiling(triggerBounds.Width), (int)Math.Ceiling(triggerBounds.Height)));
        object expandPattern;
        bool alreadyOpen = trigger.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expandPattern) &&
            ((ExpandCollapsePattern)expandPattern).Current.ExpandCollapseState == ExpandCollapseState.Expanded;
        alreadyOpen = alreadyOpen || ClaudeImageSlider.IsPopupVisible(targetWindow);
        if (!alreadyOpen) ClickControlButton(trigger, false);

        ClickPoint(delegate
        {
            System.Drawing.Point freshPoint;
            Stopwatch openTimer = Stopwatch.StartNew();
            do
            {
                EnsureForeground();
                if (ClaudeImageSlider.TryLocate(targetWindow, current, target, out freshPoint))
                {
                    trace.AppendLine("StrokesPlus image match succeeded; target=" + Label(target));
                    return freshPoint;
                }
                Thread.Sleep(25);
            } while (openTimer.ElapsedMilliseconds < 1600);
            throw new InvalidOperationException("Claude's slider image was not found; no click sent. Check theme and display scale.");
        }, true);
        // Confirm the change before dismissing the popup and restoring the composer.

        Stopwatch timer = Stopwatch.StartNew();
        int confirmed = -1;
        do
        {
            EnsureForeground();
            if (!ClaudeImageSlider.TryReadCurrent(targetWindow, out confirmed))
            {
                confirmed = CurrentButtonEffort(trigger, false);
                if (confirmed < 0)
                {
                    trigger = ClaudeEffortTrigger(root);
                    confirmed = CurrentButtonEffort(trigger, false);
                }
            }
            if (confirmed == target) break;
            Thread.Sleep(25);
        } while (timer.ElapsedMilliseconds < 2000);
        trace.AppendLine("confirmed=" + confirmed);
        if (confirmed != target) throw new InvalidOperationException("Claude's slider click did not confirm " + Label(target) + ".");
        FinishWithInputFocus("Opus 5.5: " + Label(target) + " (image verified)");
    }
    private static int CurrentButtonEffort(AutomationElement button, bool gpt)
    {
        try
        {
            if (button == null || button.Current.IsOffscreen || !button.Current.IsEnabled) return -1;
            // Read the live label, never the snapshot cached before the click.
            string name = button.Current.Name ?? "";
            if (gpt)
                return Regex.IsMatch(name, @"(?:GPT[\s-]*)?6\.1[\s-]+Sol\b", RegexOptions.IgnoreCase) ? GptEffort(name) : -1;
            return IsEffortTrigger(name) ? ClaudeEffort(name) : -1;
        }
        catch { return -1; }
    }
    private static bool IsEffortTrigger(string name)
    {
        return Regex.IsMatch(name, @"^(Effort|추론\s*강도|사고\s*강도|노력)(\b|\s|:|$)", RegexOptions.IgnoreCase);
    }
    private static AutomationElement ClaudeEffortTrigger(AutomationElement root)
    {
        return ClaudeEffortTrigger(Elements(root, ControlType.Button));
    }
    private static AutomationElement ClaudeEffortTrigger(AutomationElement[] buttons)
    {
        AutomationElement found = null;
        foreach (AutomationElement button in buttons)
        {
            string name = Name(button);
            if (!IsEffortTrigger(name)) continue;
            if (found != null) return null;
            found = button;
        }
        return found;
    }
    private static void ClickModelButton(AutomationElement button)
    {
        ClickControlButton(button, true);
    }
    private static void ClickControlButton(AutomationElement button, bool gpt)
    {
        ClickPoint(delegate
        {
            EnsureForeground();
            if (button == null || button.Current.IsOffscreen || !button.Current.IsEnabled)
                throw new InvalidOperationException("Reasoning button is unavailable; no click sent.");
            System.Windows.Rect bounds = button.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
                Double.IsNaN(bounds.X) || Double.IsNaN(bounds.Y) ||
                Double.IsInfinity(bounds.X) || Double.IsInfinity(bounds.Y))
                throw new InvalidOperationException("Reasoning button has no valid position; no click sent.");
            System.Windows.Point center = new System.Windows.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            if (targetRoot == null || !targetRoot.Current.BoundingRectangle.Contains(center))
                throw new InvalidOperationException("Reasoning button is outside the active window; no click sent.");
            trace.AppendLine("Opening " + (gpt ? "GPT model" : "Claude effort") + " popup with a mouse click.");
            System.Drawing.Rectangle buttonBounds = new System.Drawing.Rectangle((int)Math.Floor(bounds.X),
                (int)Math.Floor(bounds.Y), (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
            if (gpt) GptImageSlider.ExpectNearButton(targetWindow, buttonBounds);
            else ClaudeImageSlider.ExpectNearButton(targetWindow, buttonBounds);
            return new System.Drawing.Point((int)Math.Round(center.X), (int)Math.Round(center.Y));
        });
    }
    private static void ClickPoint(System.Drawing.Point point)
    {
        ClickPoint(delegate { return point; });
    }
    private static void ClickPoint(Func<System.Drawing.Point> locate, bool protectTextEntry = false)
    {
        System.Drawing.Point point;
        Stopwatch locateTimer = Stopwatch.StartNew();
        do
        {
            WaitForInputRelease();
            point = locate();
            EnsureForeground();
            if (protectTextEntry)
            {
                // GPT can retain accessibility focus on the composer while its
                // popover is visible. Only a dismissed popup makes that focus
                // evidence that the user has returned to typing.
                if (TextEntryFocused() && !(app == "chatgpt" && GptImageSlider.IsPopupVisible(targetWindow)))
                    throw new OperationCanceledException("Text entry regained focus; no slider click sent.");
            }
            if (!InputHeld()) break;
            if (locateTimer.ElapsedMilliseconds >= 4000)
                throw new InvalidOperationException("Release Ctrl, Alt and the arrow key, then press the hotkey again.");
        } while (true);
        System.Drawing.Point previous, after;
        bool restore = GetCursorPos(out previous);
        if (!SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Cannot position the cursor on the target control.");
        try
        {
            EnsureForeground();
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        }
        finally
        {
            if (restore && GetForegroundWindow() == targetWindow && GetCursorPos(out after) && after == point)
                SetCursorPos(previous.X, previous.Y);
        }
    }
    private static string Label(int value) { return new[] { "Low", "Medium", "High", "Extra", "Max", "Ultracode" }[value]; }
    private static bool TextEntryFocused()
    {
        AutomationElement focused = AutomationElement.FocusedElement;
        return IsTextEntry(focused);
    }
    private static bool IsTextEntry(AutomationElement element)
    {
        if (element == null) return false;
        if (element.Current.ControlType == ControlType.Edit) return true;
        if (!element.Current.IsKeyboardFocusable) return false;
        string metadata = element.Current.AutomationId + " " + element.Current.ClassName;
        if (Regex.IsMatch(metadata, @"prompt-textarea|chat[-_]?input|composer|prosemirror", RegexOptions.IgnoreCase)) return true;
        if (chatInputBounds.IsEmpty || targetRoot == null) return false;
        System.Windows.Rect window = targetRoot.Current.BoundingRectangle;
        System.Windows.Rect expected = chatInputBounds;
        expected.Offset(window.X - chatInputWindowBounds.X, window.Y - chatInputWindowBounds.Y);
        System.Windows.Rect actual = element.Current.BoundingRectangle;
        return !actual.IsEmpty && expected.Contains(actual);
    }
    private static bool ChatInputFocused()
    {
        return ChatInputFocused(AutomationElement.FocusedElement);
    }
    private static bool ChatInputFocused(AutomationElement focused)
    {
        if (!IsTextEntry(focused) || chatInputBounds.IsEmpty) return false;
        System.Windows.Rect bounds = focused.Current.BoundingRectangle;
        System.Windows.Rect window = targetRoot.Current.BoundingRectangle;
        System.Windows.Rect expected = chatInputBounds;
        expected.Offset(window.X - chatInputWindowBounds.X, window.Y - chatInputWindowBounds.Y);
        return !bounds.IsEmpty && expected.Contains(new System.Windows.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
    }
    private static int ChatInputScore(string name, string id, string className, System.Windows.Rect bounds, System.Windows.Rect window)
    {
        if (bounds.IsEmpty || bounds.Width < 180 || bounds.Height < 12 ||
            !window.Contains(new System.Windows.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)) ||
            Regex.IsMatch(name + " " + id, @"search|filter|rename|검색|찾기|이름", RegexOptions.IgnoreCase)) return -1;
        bool known = Regex.IsMatch(id + " " + className, @"prompt-textarea|chat[-_]?input|composer|prosemirror", RegexOptions.IgnoreCase);
        bool labeled = Regex.IsMatch(name, @"message|reply|prompt|chat|질문|메시지|답장|입력|요청", RegexOptions.IgnoreCase);
        bool composerShape = bounds.Width >= 300 && bounds.Bottom >= window.Top + window.Height * 0.4;
        if (!known && !labeled && !composerShape) return -1;
        return (known ? 1000 : 0) + (labeled ? 300 : 0) + (composerShape ? 100 : 0) + (bounds.Height >= 40 ? 20 : 0);
    }
    private static CacheRequest InputMetadataCache()
    {
        CacheRequest cache = new CacheRequest();
        cache.TreeScope = TreeScope.Element;
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.IsKeyboardFocusableProperty);
        cache.Add(AutomationElement.ProcessIdProperty);
        return cache;
    }
    private static void SaveChatInput(System.Windows.Rect window, System.Windows.Rect found)
    {
        chatInputBounds = found;
        chatInputWindowBounds = window;
        uint targetPid; GetWindowThreadProcessId(targetWindow, out targetPid);
        string[] values = { targetWindow.ToInt64().ToString(), targetPid.ToString(),
            window.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            window.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (found.X - window.X).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (found.Y - window.Y).ToString(System.Globalization.CultureInfo.InvariantCulture),
            found.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            found.Height.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        // Only window and rectangle numbers are persisted for already-open popups.
        File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chat-input-" + app + ".txt"), values);
    }
    private static void RememberChatInput(AutomationElement root)
    {
        // Read only textbox metadata, never its value or conversation text.
        try
        {
            System.Windows.Rect window = root.Current.BoundingRectangle;
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused != null)
                {
                    AutomationElement metadata = focused.GetUpdatedCache(InputMetadataCache());
                    string id = metadata.Cached.AutomationId ?? "", className = metadata.Cached.ClassName ?? "";
                    if (!metadata.Cached.IsOffscreen && metadata.Cached.IsEnabled && metadata.Cached.IsKeyboardFocusable &&
                        metadata.Cached.ProcessId == root.Current.ProcessId &&
                        Regex.IsMatch(id + " " + className, @"prompt-textarea|chat[-_]?input|composer|prosemirror", RegexOptions.IgnoreCase) &&
                        ChatInputScore(metadata.Cached.Name ?? "", id, className, metadata.Cached.BoundingRectangle, window) >= 0)
                    {
                        SaveChatInput(window, metadata.Cached.BoundingRectangle);
                        trace.AppendLine("Chat input identified from focused composer metadata; full input lookup skipped.");
                        return;
                    }
                }
            }
            catch { } // A stale focus or non-composer still uses the original full lookup.
            System.Windows.Rect found = System.Windows.Rect.Empty;
            int bestScore = -1;
            bool ambiguous = false;
            int candidateCount = 0, acceptedCount = 0;
            CacheRequest cache = InputMetadataCache();
            using (cache.Activate())
                foreach (AutomationElement input in root.FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new AndCondition(new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
                            new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane))))))
                {
                    if (input.Cached.IsOffscreen || !input.Cached.IsEnabled) continue;
                    candidateCount++;
                    System.Windows.Rect bounds = input.Cached.BoundingRectangle;
                    int score = ChatInputScore(input.Cached.Name ?? "", input.Cached.AutomationId ?? "",
                        input.Cached.ClassName ?? "", bounds, window);
                    if (input.Cached.ControlType != ControlType.Edit &&
                        !Regex.IsMatch(input.Cached.AutomationId + " " + input.Cached.ClassName,
                            @"prompt-textarea|chat[-_]?input|composer|prosemirror", RegexOptions.IgnoreCase) &&
                        !Regex.IsMatch(input.Cached.Name ?? "", @"^(message|reply|prompt|chat input|질문|메시지|답장|입력|요청)", RegexOptions.IgnoreCase))
                        score = -1;
                    // Metadata only; never log the textbox name/value or its contents.
                    trace.AppendLine("Input candidate type=" + input.Cached.ControlType.ProgrammaticName +
                        ", id=" + input.Cached.AutomationId + ", class=" + input.Cached.ClassName +
                        ", bounds=" + bounds + ", score=" + score);
                    if (score < 0) continue;
                    acceptedCount++;
                    if (score > bestScore) { bestScore = score; found = bounds; ambiguous = false; }
                    else if (score == bestScore) ambiguous = true;
                }
            trace.AppendLine("Input candidates=" + candidateCount + ", accepted=" + acceptedCount + ", ambiguous=" + ambiguous);
            if (!found.IsEmpty && !ambiguous)
            {
                SaveChatInput(window, found);
                trace.AppendLine("Chat input identified from textbox metadata.");
                return;
            }
            if (chatInputBounds.IsEmpty) ReadRememberedChatInput(window);
        }
        catch { trace.AppendLine("Chat input metadata not available."); }
    }
    private static void ReadRememberedChatInput(System.Windows.Rect window)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chat-input-" + app + ".txt");
        if (!File.Exists(path)) return;
        string[] values = File.ReadAllLines(path);
        uint pid; GetWindowThreadProcessId(targetWindow, out pid);
        if (values.Length != 8 || values[0] != targetWindow.ToInt64().ToString() || values[1] != pid.ToString()) return;
        double[] numbers = new double[6];
        for (int i = 0; i < numbers.Length; i++)
            if (!Double.TryParse(values[i + 2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out numbers[i]) ||
                Double.IsNaN(numbers[i]) || Double.IsInfinity(numbers[i])) return;
        if (Math.Abs(numbers[0] - window.Width) > 1 || Math.Abs(numbers[1] - window.Height) > 1) return;
        System.Windows.Rect bounds = new System.Windows.Rect(window.X + numbers[2], window.Y + numbers[3], numbers[4], numbers[5]);
        if (!window.Contains(bounds) || bounds.Width < 180 || bounds.Height < 12) return;
        chatInputBounds = bounds;
        chatInputWindowBounds = window;
        trace.AppendLine("Chat input position reused for the same window and size.");
    }
    private static bool TryInputClickPoint(System.Windows.Rect input, System.Drawing.Rectangle popup, out System.Drawing.Point point)
    {
        point = System.Drawing.Point.Empty;
        if (input.IsEmpty || input.Width < 24 || input.Height < 12) return false;
        foreach (double x in new[] { input.X + input.Width / 2, input.Left + 12, input.Right - 12 })
        {
            System.Drawing.Point candidate = new System.Drawing.Point((int)Math.Round(x),
                (int)Math.Round(input.Bottom - Math.Min(12, input.Height / 2)));
            if (!popup.Contains(candidate)) { point = candidate; return true; }
        }
        return false;
    }
    private static void FinishWithInputFocus(string message)
    {
        if (app == "chatgpt") { FinishGptWithInputFocus(message); return; }
        bool focused = false;
        try
        {
            EnsureForeground();
            if (TextEntryFocused())
            {
                focused = ChatInputFocused();
                trace.AppendLine("Text entry already focused; no extra click sent.");
            }
            else
            {
                System.Windows.Rect window = targetRoot.Current.BoundingRectangle;
                if (chatInputBounds.IsEmpty) RememberChatInput(targetRoot);
                if (!chatInputBounds.IsEmpty && Math.Abs(window.Width - chatInputWindowBounds.Width) <= 1 &&
                    Math.Abs(window.Height - chatInputWindowBounds.Height) <= 1)
                {
                    ClickPoint(delegate
                    {
                        if (TextEntryFocused()) throw new OperationCanceledException("Input already focused; no extra click sent.");
                        System.Windows.Rect currentWindow = targetRoot.Current.BoundingRectangle;
                        if (Math.Abs(currentWindow.Width - window.Width) > 1 || Math.Abs(currentWindow.Height - window.Height) > 1)
                            throw new OperationCanceledException("Window resized before input focus; no click sent.");
                        System.Windows.Rect input = chatInputBounds;
                        input.Offset(currentWindow.X - chatInputWindowBounds.X, currentWindow.Y - chatInputWindowBounds.Y);
                        System.Drawing.Rectangle popup = app == "chatgpt" ? GptImageSlider.PopupBounds : ClaudeImageSlider.PopupBounds;
                        popup.Inflate(3, 3);
                        System.Drawing.Point point;
                        if (!TryInputClickPoint(input, popup, out point))
                            throw new OperationCanceledException("Chat input is covered by the reasoning popup; no click sent.");
                        return point;
                    }, true);
                    trace.AppendLine("Chat input clicked once at " + operationTimer.ElapsedMilliseconds + " ms; no further input will be sent.");
                    Stopwatch focusTimer = Stopwatch.StartNew();
                    do
                    {
                        EnsureForeground();
                        focused = ChatInputFocused();
                        if (focused) break;
                        Thread.Sleep(10);
                    } while (focusTimer.ElapsedMilliseconds < 300);
                }
            }
        }
        catch (OperationCanceledException) { try { focused = ChatInputFocused(); } catch { } }
        catch { }
        trace.AppendLine("Chat input focus confirmed=" + focused);
        Record(message + (focused ? "; chat input focused" : "; chat input focus unconfirmed"));
    }
    private struct GptFocusState
    {
        internal bool PopupVisible, ChatFocused, OtherTextFocused;
        internal bool Complete { get { return !PopupVisible && ChatFocused; } }
    }
    private static GptFocusState CompleteGptFocus(Func<GptFocusState> readState, Action<int> clickInput,
        Func<long> elapsed, Action poll, Action<string> log)
    {
        GptFocusState state = readState();
        log("GPT finish initial: popup=" + state.PopupVisible + ", chat focus=" + state.ChatFocused);
        if (state.Complete || (!state.PopupVisible && state.OtherTextFocused)) return state;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            clickInput(attempt);
            long started = elapsed(), completeSince = -1;
            do
            {
                state = readState();
                long now = elapsed();
                if (state.Complete)
                {
                    if (completeSince < 0) completeSince = now;
                    // Check two frames so a pending popup close/focus event
                    // cannot be mistaken for a finished transition.
                    if (now - completeSince >= 20) return state;
                }
                else completeSince = -1;
                if (!state.PopupVisible && state.OtherTextFocused) return state;
                if (now - started >= 300) break;
                poll();
            } while (true);
            log("GPT input click " + attempt + " result: popup=" + state.PopupVisible + ", chat focus=" + state.ChatFocused);
            if (attempt == 1) log("GPT dismissal/focus unconfirmed; one verified retry.");
        }
        return state;
    }
    private static GptFocusState ReadGptFocusState(bool fullSearch)
    {
        EnsureForeground();
        bool popup = fullSearch ? GptImageSlider.IsPopupVisible(targetWindow) : GptImageSlider.IsPopupVisibleAtLastLocation(targetWindow);
        AutomationElement focused = AutomationElement.FocusedElement;
        bool chat = ChatInputFocused(focused);
        return new GptFocusState { PopupVisible = popup, ChatFocused = chat, OtherTextFocused = !chat && IsTextEntry(focused) };
    }
    private static void FinishGptWithInputFocus(string message)
    {
        GptFocusState final = new GptFocusState { PopupVisible = true };
        bool stateRead = false;
        try
        {
            EnsureForeground();
            if (chatInputBounds.IsEmpty) RememberChatInput(targetRoot);
            System.Windows.Rect initialWindow = targetRoot.Current.BoundingRectangle;
            if (chatInputBounds.IsEmpty || Math.Abs(initialWindow.Width - chatInputWindowBounds.Width) > 1 ||
                Math.Abs(initialWindow.Height - chatInputWindowBounds.Height) > 1)
            {
                trace.AppendLine("GPT finish skipped: chat input position unavailable for this window size.");
                final = ReadGptFocusState(true);
                stateRead = true;
            }
            else
            {
                bool initialRead = true;
                final = CompleteGptFocus(delegate
                {
                    GptFocusState state = ReadGptFocusState(initialRead);
                    initialRead = false;
                    final = state;
                    stateRead = true;
                    return state;
                }, delegate(int attempt)
                {
                    ClickPoint(delegate
                    {
                        // Recheck the popup before every actual click. Focus
                        // alone must never skip dismissing a visible GPT menu.
                        bool visible = GptImageSlider.IsPopupVisible(targetWindow);
                        if (!visible && TextEntryFocused())
                            throw new OperationCanceledException("Popup dismissed and text entry focused; no extra input click.");
                        System.Windows.Rect currentWindow = targetRoot.Current.BoundingRectangle;
                        if (Math.Abs(currentWindow.Width - initialWindow.Width) > 1 || Math.Abs(currentWindow.Height - initialWindow.Height) > 1)
                            throw new OperationCanceledException("Window resized before GPT input focus; no click sent.");
                        System.Windows.Rect input = chatInputBounds;
                        input.Offset(currentWindow.X - chatInputWindowBounds.X, currentWindow.Y - chatInputWindowBounds.Y);
                        System.Drawing.Rectangle popup = visible ? GptImageSlider.PopupBounds : System.Drawing.Rectangle.Empty;
                        if (visible) popup.Inflate(3, 3);
                        System.Drawing.Point point;
                        if (!TryInputClickPoint(input, popup, out point))
                            throw new OperationCanceledException("Chat input is covered by GPT's popup; no click sent.");
                        return point;
                    }, true);
                    trace.AppendLine("GPT chat input click " + attempt + " at " + operationTimer.ElapsedMilliseconds + " ms.");
                }, delegate { return operationTimer.ElapsedMilliseconds; }, delegate { Thread.Sleep(10); }, delegate(string value) { trace.AppendLine(value); });
            }
        }
        catch (OperationCanceledException error)
        {
            trace.AppendLine("GPT finish stopped: " + error.Message);
            try { if (GetForegroundWindow() == targetWindow) { final = ReadGptFocusState(true); stateRead = true; } } catch { }
        }
        catch (Exception error) { trace.AppendLine("GPT finish read/click failed: " + error.GetType().Name); }
        trace.AppendLine("GPT popup closed confirmed=" + (stateRead && !final.PopupVisible) + "; chat input focus confirmed=" + (stateRead && final.ChatFocused));
        Record(message + (!stateRead ? "; popup/input focus unconfirmed" : final.Complete ? "; popup closed; chat input focused" :
            final.PopupVisible ? "; reasoning popup still open; chat input focus unconfirmed" : "; popup closed; chat input focus unconfirmed"));
    }
    private static void GptFocusSelfTest()
    {
        // Replay retained textbox focus, a missed outside click, and a pending
        // focus event without any window, screenshot or injected input.
        for (int scenario = 0; scenario < 7; scenario++)
        {
            int clicks = 0;
            long clock = 0;
            int test = scenario;
            GptFocusState result = CompleteGptFocus(delegate
            {
                if (test == 0) return new GptFocusState { ChatFocused = true };
                if (test == 3) return new GptFocusState { OtherTextFocused = true };
                if (test == 4) return new GptFocusState { PopupVisible = true, ChatFocused = true };
                if (clicks == 0) return new GptFocusState { PopupVisible = true, ChatFocused = test == 1 };
                if (test == 2 && clicks == 1) return new GptFocusState { PopupVisible = true };
                if (test == 5 && clicks == 1) return new GptFocusState();
                if (test == 6 && clicks == 1) return new GptFocusState { ChatFocused = clock < 20 };
                return new GptFocusState { ChatFocused = true };
            }, delegate(int attempt) { clicks++; }, delegate { return clock; },
                delegate { clock += 10; }, delegate(string value) { });
            int expectedClicks = test == 0 || test == 3 ? 0 : test == 1 ? 1 : 2;
            bool expectedComplete = test != 3 && test != 4;
            if (clicks != expectedClicks || result.Complete != expectedComplete || (test == 4 && !result.PopupVisible))
                throw new InvalidOperationException("GPT popup/input completion regression failed: scenario " + test);
        }
    }
    private static void WriteDiagnostic(Exception error)
    {
        try
        {
            StringBuilder details = new StringBuilder();
            details.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + app + " build=" + BuildName);
            details.AppendLine(error.GetType().Name + ": " + error.Message);
            details.Append(trace);
            // Only model/effort controls; never dump chat text, editors, or the whole UI.
            if (targetRoot != null)
                foreach (ControlType type in choiceTypes)
                    foreach (AutomationElement item in Elements(targetRoot, type))
                    {
                        string name = Name(item);
                        if (!IsEffortChoice(name) && !IsEffortTrigger(name) && !(app == "claude" && ClaudeEffort(name) >= 0 && name.Length < 60) &&
                            !Regex.IsMatch(name, ClaudeModelPattern + @"|^(?:GPT[\s-]*)?6\.1[\s-]+Sol\b", RegexOptions.IgnoreCase)) continue;
                        if (name.Length > 180) name = name.Substring(0, 180);
                        details.Append(type.ProgrammaticName + " name=" + name + " patterns=");
                        foreach (AutomationPattern pattern in item.GetSupportedPatterns()) details.Append(pattern.ProgrammaticName + " ");
                        details.AppendLine();
                    }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-diagnostic.txt"), details.ToString());
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-diagnostic-" + app + ".txt"), details.ToString());
        }
        catch { }
    }
    private static void Record(string message)
    {
        try
        {
            string result = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + app + " " + message + Environment.NewLine;
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-result.txt"), result);
            trace.AppendLine(StrokesImageSearch.Summary);
            string action = result + "build=" + BuildName + "; elapsed=" + operationTimer.ElapsedMilliseconds + " ms" + Environment.NewLine + trace;
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-action.txt"), action);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-result-" + app + ".txt"), result);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-action-" + app + ".txt"), action);
        }
        catch { }
    }
    private static void ShowNotice(string message)
    {
        if (GetForegroundWindow() != targetWindow) return;
        using (Notice notice = new Notice(message)) Application.Run(notice);
    }
    private sealed class Notice : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; }
        }
        internal Notice(string message)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; Width = 420; Height = 78;
            System.Drawing.Rectangle area = Screen.FromHandle(targetWindow).WorkingArea;
            Left = area.Right - Width - 20; Top = area.Bottom - Height - 20;
            BackColor = System.Drawing.Color.FromArgb(35, 35, 40);
            Controls.Add(new Label { Dock = DockStyle.Fill, Text = message, ForeColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Malgun Gothic", 10), Padding = new Padding(12) });
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 2400 };
            timer.Tick += delegate { timer.Stop(); Close(); };
            timer.Start(); FormClosed += delegate { timer.Dispose(); };
        }
    }
    private static int SelfTest()
    {
        StrokesImageSearch.SelfTest();
        GptFocusSelfTest();
        // Protect the final click from search fields, off-window edits and popup overlap.
        System.Windows.Rect testWindow = new System.Windows.Rect(0, 0, 1000, 900);
        System.Windows.Rect testInput = new System.Windows.Rect(200, 600, 600, 100);
        if (ChatInputScore("Search", "prompt-textarea", "", testInput, testWindow) >= 0 ||
            ChatInputScore("", "", "", new System.Windows.Rect(200, 20, 600, 30), testWindow) >= 0 ||
            ChatInputScore("Message", "prompt-textarea", "", new System.Windows.Rect(1200, 600, 600, 100), testWindow) >= 0 ||
            ChatInputScore("Message", "prompt-textarea", "", testInput, testWindow) < 0)
            throw new InvalidOperationException("Chat input identification selftest failed.");
        System.Windows.Rect singleLineInput = new System.Windows.Rect(200, 650, 600, 18);
        if (ChatInputScore("", "", "ProseMirror", singleLineInput, testWindow) < 0 ||
            ChatInputScore("Search", "", "", singleLineInput, testWindow) >= 0)
            throw new InvalidOperationException("Single-line Claude input must be accepted without matching search.");
        System.Drawing.Point inputPoint;
        System.Drawing.Rectangle testPopup = new System.Drawing.Rectangle(450, 580, 400, 140);
        if (!TryInputClickPoint(testInput, testPopup, out inputPoint) || testPopup.Contains(inputPoint) ||
            !testInput.Contains(new System.Windows.Point(inputPoint.X, inputPoint.Y)) ||
            TryInputClickPoint(testInput, new System.Drawing.Rectangle(190, 590, 620, 120), out inputPoint))
            throw new InvalidOperationException("Chat input click must avoid the popup.");
        if (!TryInputClickPoint(singleLineInput, System.Drawing.Rectangle.Empty, out inputPoint) ||
            !singleLineInput.Contains(new System.Windows.Point(inputPoint.X, inputPoint.Y)))
            throw new InvalidOperationException("Short Claude input click must stay inside its text area.");
        if (Effort("6.1 Sol High") != 1 || Effort("6.1 Sol Extra High") != 2 || Effort("Opus 5.5 Medium") != 0 ||
            Effort("xHigh") != 2 || Effort("highest") != -1 || Effort("매우 높음") != 2 ||
            Effort("노력: 엑스트라") != 2 || Effort("노력: 높음") != 1 || Effort("노력: 보통") != 0 ||
            EffortOption("Extra High", 1) || !EffortOption("Extra High", 2)) return 1;
        string[] korean = { "노력: 낮음", "노력: 중간", "노력: 높음", "노력: 엑스트라", "노력: 최대", "노력: Ultracode" };
        string[] english = { "Effort: Low", "Effort: Medium", "Effort: High", "Effort: Extra", "Effort: Max", "Effort: Ultracode" };
        for (int level = 0; level < 6; level++)
        {
            if (ClaudeEffort(korean[level]) != level || ClaudeEffort(english[level]) != level ||
                NextClaude(level, 1) != (level == 5 ? 5 : level + 1) ||
                NextClaude(level, -1) != (level == 0 ? 0 : level - 1)) return 1;
        }
        if (ClaudeEffort("노력: 보통") != 1 || ClaudeEffort("노력: 매우 높음") != 3 ||
            ClaudeEffort("노력: xHigh") != 3 || ClaudeEffort("노력: 울트라코드") != 5 ||
            ClaudeEffort("unknown") != -1) return 1;
        string[] gpt = { "GPT-6.1 Sol Light", "GPT-6.1 Sol Medium", "GPT-6.1 Sol High", "GPT-6.1 Sol Extra High", "GPT-6.1 Sol Ultra" };
        for (int level = 0; level < 5; level++)
            if (GptEffort(gpt[level]) != level || NextGpt(level, 1) != (level == 4 ? 4 : level + 1) ||
                NextGpt(level, -1) != (level == 0 ? 0 : level - 1)) return 1;
        if (GptEffort("GPT-6.1 Sol") != -1) return 1;
        string[] modelNames = { "모델: Opus 5.5", "Model: Opus 5.5", "Opus 5.5", " Opus 5.5" };
        string[] sessionNames = { "유휴 가이드 Opus 5.5 업그레이드", "가이드 Opus 5.5 업그레이드에 대한 더 많은 옵션", "Opus 5.5 업그레이드에 대한 더 많은 옵션" };
        foreach (string name in modelNames) if (!Regex.IsMatch(name, ClaudeModelPattern, RegexOptions.IgnoreCase)) return 1;
        foreach (string name in sessionNames) if (Regex.IsMatch(name, ClaudeModelPattern, RegexOptions.IgnoreCase)) return 1;
        // Another model's control means the tree is ready; frame buttons alone mean it is not.
        foreach (string name in new[] { "모델: Opus 5.5", "모델: Sonnet 5.5", "Model: Haiku 5.5", " model : Opus 5.5" }) if (!IsModelControl(name)) return 1;
        foreach (string name in new[] { "최소화", "최대화", "닫기", "노력: 높음", "Opus 5.5", sessionNames[0] }) if (IsModelControl(name)) return 1;
        bool rejected = false;
        try { NextClaude(-1, 1); } catch (InvalidOperationException) { rejected = true; }
        return rejected && ClaudeImageSlider.SelfTest() && GptImageSlider.SelfTest() ? 0 : 1;
    }
}
