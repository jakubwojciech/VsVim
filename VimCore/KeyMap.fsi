﻿
namespace Vim

type internal KeyMap =

    interface IKeyMap

    new : settings : IVimGlobalSettings -> KeyMap 

