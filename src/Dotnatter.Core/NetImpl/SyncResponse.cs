﻿using System.Collections.Generic;
using Dotnatter.Core.HashgraphImpl.Model;

namespace Dotnatter.Core.NetImpl
{
    public class SyncResponse
    {

        public int FromId { get; set; }
        public bool SyncLimit{ get; set; }
        public WireEvent[] Events { get; set; }
        public Dictionary<int, int> Known { get; set; }

    }
}