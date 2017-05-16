using System;
using System.Collections.Generic;
using de4dot.blocks;
using de4dot.code.deobfuscators.Confuser;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public class DeobfuscatorInfo : DeobfuscatorInfoBase
    {
        public const string TheName = "ConfuserEx";
        public const string TheType = "cx";
        const string DefaultRegex = DeobfuscatorBase.DEFAULT_VALID_NAME_REGEX;

        private readonly BoolOption _removeAntiDebug;
        private readonly BoolOption _removeAntiDump;
        private readonly BoolOption _decryptMainAsm;

        public DeobfuscatorInfo() : base(DefaultRegex)
        {
            _removeAntiDebug = new BoolOption(null, MakeArgName("antidb"), "Remove anti debug code", true);
            _removeAntiDump = new BoolOption(null, MakeArgName("antidump"), "Remove anti dump code", true);
            _decryptMainAsm = new BoolOption(null, MakeArgName("decrypt-main"), "Decrypt main embedded assembly", true);
        }

        public override string Type => TheType;
        public override string Name => TheName;

        public override IDeobfuscator CreateDeobfuscator()
        {
            return new Deobfuscator(new Deobfuscator.Options
            {
                ValidNameRegex = validNameRegex.Get(),
                RemoveAntiDebug = _removeAntiDebug.Get(),
                RemoveAntiDump = _removeAntiDump.Get(),
                DecryptMainAsm = _decryptMainAsm.Get(),
            });
        }

        protected override IEnumerable<Option> GetOptionsInternal()
        {
            yield return _removeAntiDebug;
            yield return _removeAntiDump;
            yield return _decryptMainAsm;
        }
    }

    internal class Deobfuscator : DeobfuscatorBase
    {
        private AntiDebugger _antiDebugger;

        public Deobfuscator(Options options) : base(options)
        {
        }

        public override string Type => DeobfuscatorInfo.TheType;
        public override string TypeLong => DeobfuscatorInfo.TheName;
        public override string Name => DeobfuscatorInfo.TheName;

        private IEnumerable<IProtectorDetector> AllDetectors
        {
            get
            {
                if (_antiDebugger != null)
                    yield return _antiDebugger;
            }
        }

        protected override void ScanForObfuscator()
        {
            RemoveObfuscatorAttribute();

            _antiDebugger = new AntiDebugger(module);
            _antiDebugger.Detect();
        }

        protected override int DetectInternal()
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<int> GetStringDecrypterMethods()
        {
            throw new NotImplementedException();
        }

        void RemoveObfuscatorAttribute()
        {
            foreach (var type in module.Types)
            {
                if (type.FullName == "ConfusedByAttribute")
                {
                    AddAttributeToBeRemoved(type, "Obfuscator attribute");
                    break;
                }
            }
        }

        /*protected override void SetConfuserVersion(TypeDef type)
        {
            var s = DotNetUtils.GetCustomArgAsString(GetModuleAttribute(type) ?? GetAssemblyAttribute(type), 0);
            if (s == null)
                return;
            var val = System.Text.RegularExpressions.Regex.Match(s, @"^ConfuserEx v(\d+)\.(\d+)\.(\d+)$");
            if (val.Groups.Count < 4)
                return;
            approxVersion = new Version(int.Parse(val.Groups[1].ToString()),
                int.Parse(val.Groups[2].ToString()),
                int.Parse(val.Groups[3].ToString()));
        }*/

        /*protected override Confuser.AntiDebugger CreateAntiDebugger()
            => new AntiDebugger(module);*/

        internal class Options : OptionsBase
        {
            public bool RemoveAntiDebug { get; set; }
            public bool RemoveAntiDump { get; set; }
            public bool DecryptMainAsm { get; set; }
        }
    }

    internal interface IAntiDebuggerLocator
    {
        bool CheckMethod(TypeDef type, MethodDef initMethod);
    }


    internal interface IProtectorDetector
    {
        bool Detected { get; }
        void Detect();
    }

    internal class AntiDebugger : IProtectorDetector
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

            if (method == null || method.Body == null)
                return;

            foreach (var instr in method.Body.Instructions)
            {
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

    internal class SafeAntiDebuggerLocator : IAntiDebuggerLocator
    {
        private readonly ModuleDef _module;

        public SafeAntiDebuggerLocator(ModuleDef module)
        {
            _module = module;
        }

        public bool CheckMethod(TypeDef type, MethodDef initMethod)
        {
            if (type != DotNetUtils.GetModuleType(_module))
                return false;

            if (!DotNetUtils.HasString(initMethod, "GetEnvironmentVariable") ||
                !DotNetUtils.HasString(initMethod, "_ENABLE_PROFILING"))
                return false;

            int failFastCalls = ConfuserUtils.CountCalls(initMethod, "System.Void System.Environment::FailFast(System.String)");
            if (failFastCalls != 1)
                return false;

            return true;
        }
    }
}