﻿using System.Collections.Generic;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal class AntiDebugger : IProtectionDetector
    {
        private readonly ModuleDefMD _module;
        private FoundImplementation? _found;

        public AntiDebugger(ModuleDefMD module)
        {
            _module = module;
        }

        public bool Detected => _found.HasValue;

        public void Detect()
        {
            MethodDef method = DotNetUtils.GetModuleTypeCctor(_module);

            if (method?.Body == null)
                return;

            foreach (var m in DotNetUtils.GetMethodCalls(method))
            {
                var calledMethod = m as MethodDef;
                if (calledMethod == null)
                    continue;

                if (!DotNetUtils.IsMethod(calledMethod, "System.Void", "()"))
                    continue;

                TypeDef type = calledMethod.DeclaringType;
                if (type == null)
                    continue;

                foreach (IAntiDebuggerLocator locator in GetLocators())
                {
                    if (locator.CheckMethod(type, calledMethod))
                    {
                        _found = new FoundImplementation(locator, calledMethod);
                        return;
                    }
                }
            }
        }

        protected IEnumerable<IAntiDebuggerLocator> GetLocators()
        {
            yield return new SafeAntiDebuggerLocator(_module);
        }

        private struct FoundImplementation
        {
            public FoundImplementation(IAntiDebuggerLocator locator, MethodDef method)
            {
                Locator = locator;
                Method = method;
            }

            public IAntiDebuggerLocator Locator { get; }
            public MethodDef Method { get; }
        }
    }
}