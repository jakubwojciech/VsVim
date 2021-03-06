﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Input;

namespace Vim.UI.Wpf
{
    /// <summary>
    /// Key utility for intrepretting WPF keyboard information
    /// </summary>
    public interface IKeyUtil
    {
        /// <summary>
        /// Is this a known dead key in the active Keyboard layout
        /// </summary>
        bool IsDeadKey(Key key);

        /// <summary>
        /// Is this the AltGr key combination.  This is not directly representable in WPF
        /// logic but the best that can be done is to check for Alt + Control
        /// </summary>
        bool IsAltGr(ModifierKeys modifierKeys);

        /// <summary>
        /// Get the KeyInput value for the given char and ModifierKeys
        /// </summary>
        KeyInput GetKeyInput(char c, ModifierKeys modifierKeys);

        /// <summary>
        /// Convert the given ModifierKeys into the corresponding KeyModifiers (WPF -> Vim)
        /// </summary>
        KeyModifiers GetKeyModifiers(ModifierKeys modifierKeys);

        /// <summary>
        /// Convert the given Key and ModifierKeys into the appropriate KeyInput.  This method 
        /// may take into account other ModifierKeys based on the current state of the 
        /// keyboard.  In particular it will consider extra shift states and the caps lock
        /// key
        /// </summary>
        bool TryConvertToKeyInput(Key key, ModifierKeys modifierKeys, out KeyInput keyInput);

        /// <summary>
        /// Try and convert the given VimKey to the Key value based on the active Keyboard
        /// layout
        /// </summary>
        bool TryConvertToKeyOnly(VimKey vimKey, out Key key);
    }
}
