﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Input;
using Vim.Extensions;

namespace Vim.UI.Wpf.Implementation.Keyboard
{
    /// <summary>
    /// This class is used to build up a KeyboardMap instance from a given keyboard layout.  My understanding
    /// of how keyboard layouts work and the proper way to use them from managed code comes almost
    /// exclusively from the blog of Michael Kaplan.  In particular the "Getting all you can out of a keyboard
    /// layout" series
    ///
    /// http://blogs.msdn.com/b/michkap/archive/2006/04/06/569632.aspx
    ///
    /// Any changes made to this logic should first consult this series.  It's invaluable
    /// </summary>
    internal sealed class KeyboardMapBuilder
    {
        private readonly IVirtualKeyboard _virtualKeyboard;
        private readonly KeyboardState _keyboard;
        private Dictionary<KeyState, VimKeyData> _keyStateToVimKeyDataMap;
        private Dictionary<KeyInput, FrugalList<KeyState>> _keyInputToWpfKeyDataMap;
        private Lazy<List<uint>> _possibleModifierVirtualKey;
        private bool _lookedForOem1ModifierVirtualKey;
        private bool _lookedForOem2ModifierVirtualKey;

        internal KeyboardMapBuilder(IVirtualKeyboard virtualKeyboard)
        {
            _virtualKeyboard = virtualKeyboard;
            _keyboard = _virtualKeyboard.KeyboardState;
            _lookedForOem1ModifierVirtualKey = _keyboard.Oem1ModifierVirtualKey.HasValue;
            _lookedForOem2ModifierVirtualKey = _keyboard.Oem2ModifierVirtualKey.HasValue;
            _possibleModifierVirtualKey = new Lazy<List<uint>>(GetPossibleVirtualKeyModifiers);
            _keyboard = new KeyboardState();
        }

        internal void Create(
            out Dictionary<KeyState, VimKeyData> keyStateToVimKeyDataMap,
            out Dictionary<KeyInput, FrugalList<KeyState>> keyInputToWpfKeyDataMap)
        {
            _keyStateToVimKeyDataMap = new Dictionary<KeyState, VimKeyData>();
            _keyInputToWpfKeyDataMap = new Dictionary<KeyInput, FrugalList<KeyState>>();

            var map = BuildKeyInputData();
            BuildRemainingData(map);

            keyStateToVimKeyDataMap = _keyStateToVimKeyDataMap;
            keyInputToWpfKeyDataMap = _keyInputToWpfKeyDataMap;
        }

        /// <summary>
        /// Build up the information about the known set of vim key input
        /// </summary>
        private Dictionary<char, KeyInput> BuildKeyInputData()
        {
            var map = new Dictionary<char, KeyInput>();
            foreach (var current in KeyInputUtil.VimKeyInputList)
            {
                if (current.Key == VimKey.Nop)
                {
                    continue;
                }

                uint virtualKey;
                if (TrySpecialVimKeyToVirtualKey(current.Key, out virtualKey))
                {
                    // This is a key for which we do strictly virtual key mapping.  Add it to the map 
                    // now.  Even though we don't care about text for this key it's possible that the
                    // layout maps it to text (I believe).  
                    string text;
                    bool unused;
                    if (!_virtualKeyboard.TryGetText(virtualKey, VirtualKeyModifiers.None, out text, out unused))
                    {
                        text = String.Empty;
                    }

                    var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
                    if (Key.None == key)
                    {
                        continue;
                    }

                    var keyState = new KeyState(key, ModifierKeys.None);
                    AddMapping(keyState, current, text);
                }
                else
                {
                    // At this point we should have a char for all other inputs.  Also there shouldn't be any
                    // modifiers in this list.  This is just the basic char set 
                    Debug.Assert(current.RawChar.IsSome());
                    Debug.Assert(current.KeyModifiers == KeyModifiers.None);

                    // Do a quick check to see if we have an extended shift state modifier in our midst.
                    VirtualKeyModifiers virtualKeyModifiers;
                    if (_virtualKeyboard.TryMapChar(current.Char, out virtualKey, out virtualKeyModifiers) &&
                        0 != (virtualKeyModifiers & VirtualKeyModifiers.Extended))
                    {
                        LookForOemModifiers(current.Char, virtualKey, virtualKeyModifiers);
                    }

                    map[current.Char] = current;
                }
            }

            return map;
        }

        /// <summary>
        /// Build up the set of dead keys in this keyboard layout
        /// </summary>
        private void BuildRemainingData(Dictionary<char, KeyInput> map)
        {
            var shiftStateModifiers = GetShiftStateModifiers();
            foreach (Key key in Enum.GetValues(typeof(Key)))
            {
                foreach (var shiftStateModifier in shiftStateModifiers)
                {
                    var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

                    bool isDeadKey;
                    string text;
                    if (_virtualKeyboard.TryGetText(virtualKey, shiftStateModifier, out text, out isDeadKey))
                    {
                        // TODO: This is wrong.  Since certain Keypad entries have the same char as 
                        // a non-keypad char this will overwrite the key pad with non-keypad data.  Need
                        // to fix this
                        KeyInput keyInput;
                        if (text.Length == 1 && map.TryGetValue(text[0], out keyInput))
                        {
                            var keyState = new KeyState(key, shiftStateModifier);
                            AddMapping(keyState, keyInput, text);
                        }
                    }
                    else if (isDeadKey)
                    {
                        var keyState = new KeyState(key, VirtualKeyModifiers.None);
                        _keyStateToVimKeyDataMap[keyState] = VimKeyData.DeadKey;
                    }
                }
            }
        }

        /// <summary>
        /// Get all of the interesting shift state modifiers for this keyboard layout
        ///
        /// Several non-sense combinations like Alt or Shift + altleft out because they 
        /// are unused.  Source
        ///
        /// http://blogs.msdn.com/b/michkap/archive/2006/04/13/575500.aspx
        /// </summary>
        private List<VirtualKeyModifiers> GetShiftStateModifiers()
        {
            var list = new List<VirtualKeyModifiers>();
            list.Add(VirtualKeyModifiers.None);
            list.Add(VirtualKeyModifiers.Shift);
            list.Add(VirtualKeyModifiers.Shift | VirtualKeyModifiers.Control);
            list.Add(VirtualKeyModifiers.Shift | VirtualKeyModifiers.Control | VirtualKeyModifiers.Alt);
            list.Add(VirtualKeyModifiers.Control);
            list.Add(VirtualKeyModifiers.Control | VirtualKeyModifiers.Alt);
            list.Add(VirtualKeyModifiers.CapsLock);

            if (_keyboard.Oem1ModifierVirtualKey.HasValue)
            {
                list.Add(VirtualKeyModifiers.Oem1);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Shift);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Control);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Alt);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Control | VirtualKeyModifiers.Alt);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Control);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Control | VirtualKeyModifiers.Alt);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Alt);
            }

            if (_keyboard.Oem2ModifierVirtualKey.HasValue)
            {
                list.Add(VirtualKeyModifiers.Oem2);
                list.Add(VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Shift);
                list.Add(VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Control);
                list.Add(VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Alt);
                list.Add(VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Control | VirtualKeyModifiers.Alt);
                list.Add(VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Control);
                list.Add(VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Control | VirtualKeyModifiers.Alt);
                list.Add(VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Alt);
            }

            if (_keyboard.Oem1ModifierVirtualKey.HasValue && _keyboard.Oem2ModifierVirtualKey.HasValue)
            {
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Oem2);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Shift);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Control);
                list.Add(VirtualKeyModifiers.Oem1 | VirtualKeyModifiers.Oem2 | VirtualKeyModifiers.Alt);
            }

            return list;
        }

        private void AddMapping(KeyState keyState, KeyInput keyInput, string text)
        {
            _keyStateToVimKeyDataMap[keyState] = new VimKeyData(keyInput, text);

            FrugalList<KeyState> list;
            if (!_keyInputToWpfKeyDataMap.TryGetValue(keyInput, out list))
            {
                _keyInputToWpfKeyDataMap[keyInput] = new FrugalList<KeyState>(keyState);
            }
            else
            {
                list.Add(keyState);
            }
        }

        /// <summary>
        /// Get the virtual key code for the provided VimKey.  This will only work for Vim keys which
        /// are meant for very specific keys.  It doesn't work for alphas
        ///
        /// All constant values derived from the list at the following 
        /// location
        ///   http://msdn.microsoft.com/en-us/library/ms645540(VS.85).aspx
        ///
        /// </summary>
        private static bool TrySpecialVimKeyToVirtualKey(VimKey vimKey, out uint virtualKeyCode)
        {
            var found = true;
            switch (vimKey)
            {
                case VimKey.Enter: virtualKeyCode = 0xD; break;
                case VimKey.Tab: virtualKeyCode = 0x9; break;
                case VimKey.Escape: virtualKeyCode = 0x1B; break;
                case VimKey.LineFeed: virtualKeyCode = 0; break;
                case VimKey.Back: virtualKeyCode = 0x8; break;
                case VimKey.Delete: virtualKeyCode = 0x2E; break;
                case VimKey.Left: virtualKeyCode = 0x25; break;
                case VimKey.Up: virtualKeyCode = 0x26; break;
                case VimKey.Right: virtualKeyCode = 0x27; break;
                case VimKey.Down: virtualKeyCode = 0x28; break;
                case VimKey.Help: virtualKeyCode = 0x2F; break;
                case VimKey.Insert: virtualKeyCode = 0x2D; break;
                case VimKey.Home: virtualKeyCode = 0x24; break;
                case VimKey.End: virtualKeyCode = 0x23; break;
                case VimKey.PageUp: virtualKeyCode = 0x21; break;
                case VimKey.PageDown: virtualKeyCode = 0x22; break;
                case VimKey.Break: virtualKeyCode = 0x03; break;
                case VimKey.F1: virtualKeyCode = 0x70; break;
                case VimKey.F2: virtualKeyCode = 0x71; break;
                case VimKey.F3: virtualKeyCode = 0x72; break;
                case VimKey.F4: virtualKeyCode = 0x73; break;
                case VimKey.F5: virtualKeyCode = 0x74; break;
                case VimKey.F6: virtualKeyCode = 0x75; break;
                case VimKey.F7: virtualKeyCode = 0x76; break;
                case VimKey.F8: virtualKeyCode = 0x77; break;
                case VimKey.F9: virtualKeyCode = 0x78; break;
                case VimKey.F10: virtualKeyCode = 0x79; break;
                case VimKey.F11: virtualKeyCode = 0x7a; break;
                case VimKey.F12: virtualKeyCode = 0x7b; break;
                case VimKey.KeypadMultiply: virtualKeyCode = 0x6A; break;
                case VimKey.KeypadPlus: virtualKeyCode = 0x6B; break;
                case VimKey.KeypadMinus: virtualKeyCode = 0x6D; break;
                case VimKey.KeypadDecimal: virtualKeyCode = 0x6E; break;
                case VimKey.KeypadDivide: virtualKeyCode = 0x6F; break;
                case VimKey.Keypad0: virtualKeyCode = 0x60; break;
                case VimKey.Keypad1: virtualKeyCode = 0x61; break;
                case VimKey.Keypad2: virtualKeyCode = 0x62; break;
                case VimKey.Keypad3: virtualKeyCode = 0x63; break;
                case VimKey.Keypad4: virtualKeyCode = 0x64; break;
                case VimKey.Keypad5: virtualKeyCode = 0x65; break;
                case VimKey.Keypad6: virtualKeyCode = 0x66; break;
                case VimKey.Keypad7: virtualKeyCode = 0x67; break;
                case VimKey.Keypad8: virtualKeyCode = 0x68; break;
                case VimKey.Keypad9: virtualKeyCode = 0x69; break;
                default:
                    virtualKeyCode = 0;
                    found = false;
                    break;
            }

            return found;
        }

        private void LookForOemModifiers(char c, uint virtualKey, VirtualKeyModifiers virtualKeyModifiers)
        {
            // These are flags but we can only search one at a time here.  If both are present it's not
            // possible to distinguish one from the others
            var regular = virtualKeyModifiers & VirtualKeyModifiers.Regular;
            var extended = virtualKeyModifiers & VirtualKeyModifiers.Extended;
            switch (extended)
            {
                case VirtualKeyModifiers.Oem1:
                    if (!_lookedForOem1ModifierVirtualKey)
                    {
                        _lookedForOem1ModifierVirtualKey = true;
                        _keyboard.Oem1ModifierVirtualKey = LookForOemModifiersSingle(c, virtualKey, regular);
                    }
                    break;
                case VirtualKeyModifiers.Oem2:
                    if (!_lookedForOem2ModifierVirtualKey)
                    {
                        _lookedForOem2ModifierVirtualKey = true;
                        _keyboard.Oem2ModifierVirtualKey = LookForOemModifiersSingle(c, virtualKey, regular);
                    }
                    break;
            }
        }

        private uint? LookForOemModifiersSingle(char c, uint virtualKey, VirtualKeyModifiers regularKeyModifiers)
        {
            var target = c.ToString();
            _keyboard.Clear();
            foreach (var code in _possibleModifierVirtualKey.Value)
            {
                // Set the keyboard state 
                _keyboard.SetKey(code);

                // Now try to get the text value with the previous key down
                string text;
                bool unused;
                if (_virtualKeyboard.TryGetText(virtualKey, regularKeyModifiers, out text, out unused) && text == target)
                {
                    return code;
                }
            }

            return null;
        }

        /// <summary>
        /// In the case where we find keys with extended virtual key modifiers we need to look for
        /// the virtual keys which could actually trigger them.  This function will make a guess
        /// at what those keys could be
        /// </summary>
        private List<uint> GetPossibleVirtualKeyModifiers()
        {
            var list = new List<uint>();
            for (uint i = 0xba; i < 0xe5; i++)
            {
                bool isDeadKey;
                string unused;
                if (!_virtualKeyboard.TryGetText(i, VirtualKeyModifiers.None, out unused, out isDeadKey) && !isDeadKey)
                {
                    list.Add(i);
                }
            }

            return list;
        }
    }
}
