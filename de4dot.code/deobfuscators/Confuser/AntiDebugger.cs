/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.Confuser {

    internal interface IAntiDebuggerLocator
    {
        bool CheckMethod(TypeDef type, MethodDef initMethod, out AntiDebugger.ConfuserVersion detectedVersion);
    }

    internal class AntiDebuggerLocatorBase
    {
        protected static MethodDef GetAntiDebugMethod(TypeDef type, MethodDef initMethod)
        {
            foreach (var method in type.Methods)
            {
                if (method.Body == null || method == initMethod)
                    continue;
                if (!method.IsStatic || method.Name == ".cctor")
                    continue;
                if (!method.IsPrivate)
                    continue;
                if (!DotNetUtils.IsMethod(method, "System.Void", "()") && !DotNetUtils.IsMethod(method, "System.Void", "(System.Object)"))
                    continue;

                return method;
            }
            return null;
        }

        protected static bool CheckProfilerStrings1(MethodDef method)
        {
            if (!DotNetUtils.HasString(method, "COR_ENABLE_PROFILING"))
                return false;
            if (!DotNetUtils.HasString(method, "COR_PROFILER"))
                return false;

            return true;
        }

        protected static bool CheckProfilerStrings2(MethodDef method)
        {
            if (!DotNetUtils.HasString(method, "COR_"))
                return false;
            if (!DotNetUtils.HasString(method, "ENABLE_PROFILING"))
                return false;
            if (!DotNetUtils.HasString(method, "PROFILER"))
                return false;

            return true;
        }
    }

    class NormalAntiDebuggerLocator : AntiDebuggerLocatorBase, IAntiDebuggerLocator
    {
        public bool CheckMethod(TypeDef type, MethodDef initMethod, out AntiDebugger.ConfuserVersion detectedVersion)
        {
            detectedVersion = AntiDebugger.ConfuserVersion.Unknown;

            var ntQueryInformationProcess = DotNetUtils.GetPInvokeMethod(type, "ntdll", "NtQueryInformationProcess");
            if (ntQueryInformationProcess == null)
                return false;
            if (DotNetUtils.GetPInvokeMethod(type, "ntdll", "NtSetInformationProcess") == null)
                return false;
            if (DotNetUtils.GetPInvokeMethod(type, "kernel32", "CloseHandle") == null)
                return false;
            var antiDebugMethod = GetAntiDebugMethod(type, initMethod);
            if (antiDebugMethod == null)
                return false;

            bool hasDebuggerStrings = DotNetUtils.HasString(antiDebugMethod, "Debugger detected (Managed)");

            if (DotNetUtils.CallsMethod(initMethod, "System.Void System.Threading.Thread::.ctor(System.Threading.ParameterizedThreadStart)"))
            {
                int failFastCalls = ConfuserUtils.CountCalls(antiDebugMethod, "System.Void System.Environment::FailFast(System.String)");
                if (failFastCalls != 6 && failFastCalls != 8)
                    return false;

                if (!CheckProfilerStrings1(initMethod))
                    return false;

                if (!DotNetUtils.CallsMethod(antiDebugMethod, "System.Void System.Threading.Thread::.ctor(System.Threading.ParameterizedThreadStart)"))
                {
                    if (!hasDebuggerStrings)
                        return false;
                    if (ConfuserUtils.CountCalls(antiDebugMethod, ntQueryInformationProcess) != 2)
                        return false;
                    detectedVersion = AntiDebugger.ConfuserVersion.v16_r61954_normal;
                }
                else if (failFastCalls == 8)
                {
                    if (!hasDebuggerStrings)
                        return false;
                    if (ConfuserUtils.CountCalls(antiDebugMethod, ntQueryInformationProcess) != 2)
                        return false;
                    detectedVersion = AntiDebugger.ConfuserVersion.v17_r73822_normal;
                }
                else if (failFastCalls == 6)
                {
                    if (DotNetUtils.GetPInvokeMethod(type, "IsDebuggerPresent") == null)
                        return false;
                    if (ConfuserUtils.CountCalls(antiDebugMethod, ntQueryInformationProcess) != 0)
                        return false;
                    if (hasDebuggerStrings)
                        detectedVersion = AntiDebugger.ConfuserVersion.v17_r74021_normal;
                    else
                        detectedVersion = AntiDebugger.ConfuserVersion.v19_r78363_normal;
                }
                else
                    return false;
            }
            else if (!DotNetUtils.CallsMethod(initMethod, "System.Void System.Threading.ThreadStart::.ctor(System.Object,System.IntPtr)"))
            {
                if (!hasDebuggerStrings)
                    return false;
                if (!DotNetUtils.CallsMethod(initMethod, "System.Void System.Diagnostics.Process::EnterDebugMode()"))
                    return false;
                if (!CheckProfilerStrings1(antiDebugMethod))
                    return false;
                detectedVersion = AntiDebugger.ConfuserVersion.v14_r57588_normal;
            }
            else
            {
                if (!hasDebuggerStrings)
                    return false;
                if (!DotNetUtils.CallsMethod(initMethod, "System.Void System.Diagnostics.Process::EnterDebugMode()"))
                    return false;
                if (!CheckProfilerStrings1(antiDebugMethod))
                    return false;
                detectedVersion = AntiDebugger.ConfuserVersion.v14_r60785_normal;
            }

            return true;
        }
    }

    class SafeAntiDebuggerLocator : AntiDebuggerLocatorBase, IAntiDebuggerLocator
    {
        private ModuleDef _module;

        public SafeAntiDebuggerLocator(ModuleDef module)
        {
            _module = module;
        }

        public bool CheckMethod(TypeDef type, MethodDef initMethod, out AntiDebugger.ConfuserVersion detectedVersion)
        {
            detectedVersion = AntiDebugger.ConfuserVersion.Unknown;

            if (type == DotNetUtils.GetModuleType(_module))
            {
                if (!DotNetUtils.HasString(initMethod, "Debugger detected (Managed)"))
                    return false;
                if (!CheckProfilerStrings1(initMethod))
                    return false;

                detectedVersion = AntiDebugger.ConfuserVersion.v14_r57588_safe;
            }
            else
            {
                var ntQueryInformationProcess = DotNetUtils.GetPInvokeMethod(type, "ntdll", "NtQueryInformationProcess");
                if (ntQueryInformationProcess == null)
                    return false;
                if (DotNetUtils.GetPInvokeMethod(type, "ntdll", "NtSetInformationProcess") == null)
                    return false;
                if (DotNetUtils.GetPInvokeMethod(type, "kernel32", "CloseHandle") == null)
                    return false;
                var antiDebugMethod = GetAntiDebugMethod(type, initMethod);
                if (antiDebugMethod == null)
                    return false;
                bool hasDebuggerStrings = DotNetUtils.HasString(antiDebugMethod, "Debugger detected (Managed)") ||
                                          DotNetUtils.HasString(antiDebugMethod, "Debugger is detected (Managed)");
                if (!DotNetUtils.CallsMethod(initMethod, "System.Void System.Threading.Thread::.ctor(System.Threading.ParameterizedThreadStart)"))
                    return false;
                if (ConfuserUtils.CountCalls(antiDebugMethod, ntQueryInformationProcess) != 0)
                    return false;
                if (!CheckProfilerStrings1(initMethod) && !CheckProfilerStrings2(initMethod))
                    return false;

                int failFastCalls = ConfuserUtils.CountCalls(antiDebugMethod, "System.Void System.Environment::FailFast(System.String)");
                if (failFastCalls != 2)
                    return false;

                if (hasDebuggerStrings)
                {
                    if (!DotNetUtils.CallsMethod(antiDebugMethod, "System.Void System.Threading.Thread::.ctor(System.Threading.ParameterizedThreadStart)"))
                        detectedVersion = AntiDebugger.ConfuserVersion.v16_r61954_safe;
                    else if (DotNetUtils.GetPInvokeMethod(type, "IsDebuggerPresent") == null)
                        detectedVersion = AntiDebugger.ConfuserVersion.v17_r73822_safe;
                    else if (CheckProfilerStrings1(initMethod))
                        detectedVersion = AntiDebugger.ConfuserVersion.v17_r74021_safe;
                    else
                        detectedVersion = AntiDebugger.ConfuserVersion.v19_r76119_safe;
                }
                else
                {
                    detectedVersion = AntiDebugger.ConfuserVersion.v19_r78363_safe;
                }
            }

            return true;
        }
    }

    class AntiDebugger : IVersionProvider
    {
        private readonly IEnumerable<IAntiDebuggerLocator> _locators;

		protected ModuleDefMD module;
		MethodDef initMethod;
		ConfuserVersion version = ConfuserVersion.Unknown;

		public enum ConfuserVersion {
			Unknown,
			v14_r57588_normal,
			v14_r57588_safe,
			v14_r60785_normal,
			v16_r61954_normal,
			v16_r61954_safe,
			v17_r73822_normal,
			v17_r73822_safe,
			v17_r74021_normal,
			v17_r74021_safe,
			v19_r76119_safe,
			v19_r78363_normal,
			v19_r78363_safe,
		}

		public MethodDef InitMethod {
			get { return initMethod; }
		}

		public TypeDef Type {
			get { return initMethod != null ? initMethod.DeclaringType : null; }
		}

		public bool Detected {
			get { return initMethod != null; }
		}

		public AntiDebugger(ModuleDefMD module)
		{
		    this.module = module;

		    _locators = new IAntiDebuggerLocator[]
		    {
		        new NormalAntiDebuggerLocator(),
                new SafeAntiDebuggerLocator(module)
		    };
		}

		public void Find() {
			if (CheckMethod(DotNetUtils.GetModuleTypeCctor(module)))
				return;
		}

        protected virtual IEnumerable<IAntiDebuggerLocator> GetLocators() => _locators;

        bool CheckMethod(MethodDef method) {
			if (method == null || method.Body == null)
				return false;

			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code != Code.Call)
					continue;
				var calledMethod = instr.Operand as MethodDef;
				if (calledMethod == null || !calledMethod.IsStatic)
					continue;
				if (!DotNetUtils.IsMethod(calledMethod, "System.Void", "()"))
					continue;
				var type = calledMethod.DeclaringType;
				if (type == null)
					continue;

			    foreach (IAntiDebuggerLocator locator in GetLocators())
			    {
			        if (locator.CheckMethod(type, calledMethod, out ConfuserVersion v))
			        {
			            version = v;
			            initMethod = calledMethod;
			            return true;
			        }
			    }
			}

			return false;
		}

		public bool GetRevisionRange(out int minRev, out int maxRev) {
			switch (version) {
			case ConfuserVersion.Unknown:
				minRev = maxRev = 0;
				return false;

			case ConfuserVersion.v14_r57588_safe:
				minRev = 57588;
				maxRev = 60787;
				return true;

			case ConfuserVersion.v16_r61954_safe:
				minRev = 61954;
				maxRev = 73791;
				return true;

			case ConfuserVersion.v17_r73822_safe:
				minRev = 73822;
				maxRev = 73822;
				return true;

			case ConfuserVersion.v17_r74021_safe:
				minRev = 74021;
				maxRev = 76101;
				return true;

			case ConfuserVersion.v19_r76119_safe:
				minRev = 76119;
				maxRev = 78342;
				return true;

			case ConfuserVersion.v19_r78363_safe:
				minRev = 78363;
				maxRev = int.MaxValue;
				return true;

			case ConfuserVersion.v14_r57588_normal:
				minRev = 57588;
				maxRev = 60408;
				return true;

			case ConfuserVersion.v14_r60785_normal:
				minRev = 60785;
				maxRev = 60787;
				return true;

			case ConfuserVersion.v16_r61954_normal:
				minRev = 61954;
				maxRev = 73791;
				return true;

			case ConfuserVersion.v17_r73822_normal:
				minRev = 73822;
				maxRev = 73822;
				return true;

			case ConfuserVersion.v17_r74021_normal:
				minRev = 74021;
				maxRev = 78342;
				return true;

			case ConfuserVersion.v19_r78363_normal:
				minRev = 78363;
				maxRev = int.MaxValue;
				return true;

			default: throw new ApplicationException("Invalid version");
			}
		}
	}
}
