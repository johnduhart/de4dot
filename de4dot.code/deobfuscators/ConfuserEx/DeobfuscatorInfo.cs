using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;

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
}