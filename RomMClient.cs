﻿using Playnite.SDK;
using System;

namespace RomM
{
    public class RomMClient : LibraryClient
    {
        public override bool IsInstalled => true;

        public override void Open()
        {
            throw new NotImplementedException();
        }

        public override string Icon => RomM.Icon;
    }
}
