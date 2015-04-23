//
// DO NOT MODIFY! THIS IS AUTOGENERATED FILE!
//
namespace Xilium.CefGlue
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using Xilium.CefGlue.Interop;
    
    // Role: PROXY
    public sealed unsafe partial class CefCommandLine : IDisposable
    {
        internal static CefCommandLine FromNative(cef_command_line_t* ptr)
        {
            return new CefCommandLine(ptr);
        }
        
        internal static CefCommandLine FromNativeOrNull(cef_command_line_t* ptr)
        {
            if (ptr == null) return null;
            return new CefCommandLine(ptr);
        }
        
        private cef_command_line_t* _self;
        
        private CefCommandLine(cef_command_line_t* ptr)
        {
            if (ptr == null) throw new ArgumentNullException("ptr");
            _self = ptr;
        }
        
        ~CefCommandLine()
        {
            Dispose();
        }
        
        public void Dispose()
        {
            if (_self != null)
            {
                Release();
            }
        }
        
        internal void AddRef()
        {
            cef_command_line_t.add_ref(_self);
        }
        
        internal bool Release()
        {
            bool retVal = cef_command_line_t.release(_self);
            if (retVal)
            {
                _self = null;
                Dispose();
            }
            return retVal;
        }
        
        internal bool HasOneRef
        {
            get { return cef_command_line_t.has_one_ref(_self); }
        }
        
        internal cef_command_line_t* ToNative()
        {
            AddRef();
            return _self;
        }
    }
}