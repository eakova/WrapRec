﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WrapRec
{
    public class WrapRecException : Exception
    {
        public WrapRecException(string message) : base(message)
        { }
    }
}
