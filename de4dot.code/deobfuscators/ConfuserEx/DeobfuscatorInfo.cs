using System;
using System.Collections.Generic;
using de4dot.blocks;
using de4dot.code.deobfuscators.Confuser;
using dnlib.DotNet;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public class DeobfuscatorInfo : DeobfuscatorInfoBase
    {
        public const string TheName = "ConfuserEx";
        public const string TheType = "cx";
        const string DefaultRegex = DeobfuscatorBase.DEFAULT_VALID_NAME_REGEX;

        BoolOption _removeAntiDebug;
        BoolOption _removeAntiDump;
        BoolOption _decryptMainAsm;

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
            return new Deobfuscator(new Confuser.Deobfuscator.Options
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

    internal class Deobfuscator : Confuser.Deobfuscator
    {
        public Deobfuscator(Options options) : base(options)
        {
        }

        public override string Type => DeobfuscatorInfo.TheType;
        public override string TypeLong => DeobfuscatorInfo.TheName;
        public override string Name => DeobfuscatorInfo.TheName;

        protected override void SetConfuserVersion(TypeDef type)
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
        }

        protected override Confuser.AntiDebugger CreateAntiDebugger()
            => new AntiDebugger(module);
    }

    internal class AntiDebugger : Confuser.AntiDebugger
    {
        public AntiDebugger(ModuleDefMD module)
            : base(module)
        {
        }

        protected override IEnumerable<IAntiDebuggerLocator> GetLocators()
        {
            yield return new SafeAntiDebuggerLocator(module);
        }
    }

    internal class SafeAntiDebuggerLocator : IAntiDebuggerLocator
    {
        private readonly ModuleDef _module;

        public SafeAntiDebuggerLocator(ModuleDef module)
        {
            _module = module;
        }

        public bool CheckMethod(TypeDef type, MethodDef initMethod, out Confuser.AntiDebugger.ConfuserVersion detectedVersion)
        {
            detectedVersion = Confuser.AntiDebugger.ConfuserVersion.Unknown;

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