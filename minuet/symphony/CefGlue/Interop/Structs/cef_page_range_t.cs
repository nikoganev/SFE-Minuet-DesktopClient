﻿//
// This file manually written from cef/include/internal/cef_types.h.
//
namespace Xilium.CefGlue.Interop
{
    using System;
    using System.Runtime.InteropServices;

    [StructLayout(LayoutKind.Sequential, Pack = libcef.ALIGN)]
    internal unsafe struct cef_page_range_t
    {
        public int From;
        public int To;

        public cef_page_range_t(int width, int height)
        {
            this.From = width;
            this.To = height;
        }
    }
}