using UnityEngine;
using UnityEngine.InputSystem;

namespace PHLPracticeModPack
{
    /// <summary>Maps multisheet_client.json bind strings to the Input System (keyboard + mouse).</summary>
    internal static class ClientKeybindHelper
    {
        internal const string MouseMiddleBind = "MouseMiddle";
        internal const string MouseBackBind = "MouseBack";
        internal const string MouseForwardBind = "MouseForward";

        /// <summary>
        /// False while chat, pause menu, or settings has focus — same signals as ToastersReskinLoader
        /// <c>IsBlockingUIOpen</c>, plus our UIChat StartInput/StopInput patch.
        /// </summary>
        internal static bool ShouldProcessKeybinds()
        {
            if (CustomLevelPlugin.ChatInputActive)
                return false;

            try
            {
                UIManager ui = MonoBehaviourSingleton<UIManager>.Instance;
                if (ui != null)
                {
                    if (ui.PauseMenu != null && ui.PauseMenu.IsVisible)
                        return false;
                    if (ui.Chat != null && ui.Chat.IsFocused)
                        return false;
                }

                UISettings settings = MonoBehaviourSingleton<UISettings>.Instance;
                if (settings != null && settings.IsVisible)
                    return false;
            }
            catch { }

            return true;
        }

        internal static bool WasKeyPressedThisFrame(string bindName)
        {
            return WasBindPressedThisFrame(bindName);
        }

        internal static bool WasBindPressedThisFrame(string bindName)
        {
            if (!ShouldProcessKeybinds())
                return false;

            if (TryGetMouseButtonControl(bindName, out UnityEngine.InputSystem.Controls.ButtonControl mouseButton))
            {
                return mouseButton != null && mouseButton.wasPressedThisFrame;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return false;

            Key key = ParseKey(bindName);
            if (key == Key.None)
                return false;

            UnityEngine.InputSystem.Controls.KeyControl control = keyboard[key];
            return control != null && control.wasPressedThisFrame;
        }

        /// <summary>Poll while a rebind row is listening — captures side/extra mouse buttons.</summary>
        internal static bool TryCaptureMouseBindPress(out string bindName)
        {
            bindName = null;
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return false;

            if (mouse.middleButton.wasPressedThisFrame)
            {
                bindName = MouseMiddleBind;
                return true;
            }

            if (mouse.backButton.wasPressedThisFrame)
            {
                bindName = MouseBackBind;
                return true;
            }

            if (mouse.forwardButton.wasPressedThisFrame)
            {
                bindName = MouseForwardBind;
                return true;
            }

            return false;
        }

        internal static bool IsIgnoredRebindKeyCode(KeyCode keyCode)
        {
            return keyCode == KeyCode.Tab
                || keyCode == KeyCode.LeftShift
                || keyCode == KeyCode.RightShift
                || keyCode == KeyCode.LeftControl
                || keyCode == KeyCode.RightControl
                || keyCode == KeyCode.LeftAlt
                || keyCode == KeyCode.RightAlt;
        }

        /// <summary>UITK KeyDownEvent → stored bind string, or null when ignored.</summary>
        internal static string KeyCodeToBindName(KeyCode keyCode)
        {
            if (keyCode == KeyCode.Escape || IsIgnoredRebindKeyCode(keyCode))
                return null;

            switch (keyCode)
            {
                case KeyCode.Mouse2:
                    return MouseMiddleBind;
                case KeyCode.Mouse3:
                    return MouseBackBind;
                case KeyCode.Mouse4:
                    return MouseForwardBind;
            }

            string pressed = keyCode.ToString();
            if (pressed.Length == 1)
                pressed = pressed.ToUpperInvariant();
            return pressed;
        }

        internal static string NormalizeDisplayKey(string bindName)
        {
            if (string.IsNullOrWhiteSpace(bindName))
                return "?";

            string trimmed = bindName.Trim();
            switch (trimmed)
            {
                case MouseMiddleBind:
                    return "MMB";
                case MouseBackBind:
                    return "Mouse4";
                case MouseForwardBind:
                    return "Mouse5";
            }

            if (trimmed.Length == 1)
                return trimmed.ToUpperInvariant();

            if (trimmed.StartsWith("Alpha", System.StringComparison.Ordinal) && trimmed.Length == 6)
                return trimmed.Substring(5);

            return trimmed;
        }

        internal static Key ParseKey(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName))
                return Key.None;

            if (IsMouseBind(keyName))
                return Key.None;

            string name = keyName.Trim();
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if (c >= 'A' && c <= 'Z')
                    return (Key)((int)Key.A + (c - 'A'));
                if (c >= '0' && c <= '9')
                    return (Key)((int)Key.Digit0 + (c - '0'));
                return Key.None;
            }

            switch (name)
            {
                case "Alpha0": return Key.Digit0;
                case "Alpha1": return Key.Digit1;
                case "Alpha2": return Key.Digit2;
                case "Alpha3": return Key.Digit3;
                case "Alpha4": return Key.Digit4;
                case "Alpha5": return Key.Digit5;
                case "Alpha6": return Key.Digit6;
                case "Alpha7": return Key.Digit7;
                case "Alpha8": return Key.Digit8;
                case "Alpha9": return Key.Digit9;
                case "LeftShift": return Key.LeftShift;
                case "RightShift": return Key.RightShift;
                case "LeftControl": return Key.LeftCtrl;
                case "RightControl": return Key.RightCtrl;
                case "LeftAlt": return Key.LeftAlt;
                case "RightAlt": return Key.RightAlt;
                case "Return":
                case "Enter": return Key.Enter;
                case "Escape": return Key.Escape;
                case "BackQuote": return Key.Backquote;
                case "Minus": return Key.Minus;
                case "Equals": return Key.Equals;
                case "LeftBracket": return Key.LeftBracket;
                case "RightBracket": return Key.RightBracket;
                case "Backslash": return Key.Backslash;
                case "Semicolon": return Key.Semicolon;
                case "Quote": return Key.Quote;
                case "Comma": return Key.Comma;
                case "Period": return Key.Period;
                case "Slash": return Key.Slash;
            }

            if (System.Enum.TryParse(name, true, out Key parsed))
                return parsed;

            return Key.None;
        }

        private static bool IsMouseBind(string bindName)
        {
            if (string.IsNullOrWhiteSpace(bindName))
                return false;

            string name = bindName.Trim();
            return name == MouseMiddleBind
                || name == MouseBackBind
                || name == MouseForwardBind
                || name == "Mouse4"
                || name == "Mouse5"
                || name == "MMB";
        }

        private static bool TryGetMouseButtonControl(
            string bindName,
            out UnityEngine.InputSystem.Controls.ButtonControl button)
        {
            button = null;
            if (string.IsNullOrWhiteSpace(bindName))
                return false;

            Mouse mouse = Mouse.current;
            if (mouse == null)
                return false;

            switch (bindName.Trim())
            {
                case MouseMiddleBind:
                case "MMB":
                case "MiddleMouse":
                    button = mouse.middleButton;
                    return true;
                case MouseBackBind:
                case "Mouse4":
                case "MouseButton4":
                    button = mouse.backButton;
                    return true;
                case MouseForwardBind:
                case "Mouse5":
                case "MouseButton5":
                    button = mouse.forwardButton;
                    return true;
                default:
                    return false;
            }
        }
    }
}
