using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;

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
        private readonly Options _options;
        private bool _foundAttribute;
        private AntiDebugger _antiDebugger;
        private NormalMethodsDecrypter _normalMethodsDecrypter;

        public Deobfuscator(Options options) : base(options)
        {
            _options = options;
        }

        public override string Type => DeobfuscatorInfo.TheType;
        public override string TypeLong => DeobfuscatorInfo.TheName;

        public override string Name => DeobfuscatorInfo.TheName;

        public override IEnumerable<IBlocksDeobfuscator> BlocksDeobfuscators
        {
            get { yield return new BlocksDeobfuscator(); }
        }

        private IEnumerable<IProtectionDetector> AllDetectors
        {
            get
            {
                if (_normalMethodsDecrypter != null)
                    yield return _normalMethodsDecrypter;
                if (_antiDebugger != null)
                    yield return _antiDebugger;
            }
        }

        protected override void ScanForObfuscator()
        {
            _normalMethodsDecrypter = new NormalMethodsDecrypter(module);
            _normalMethodsDecrypter.Detect();
            if (_normalMethodsDecrypter.Detected)
                return;

            ScanForNonDecrypterProtections();
        }

        private void ScanForNonDecrypterProtections()
        {
            RemoveObfuscatorAttribute();

            _antiDebugger = new AntiDebugger(module);
            _antiDebugger.Detect();
        }

        protected override int DetectInternal()
        {
            int detected = AllDetectors.Where(d => d.Detected)
                .Select(_ => 1)
                .Sum();

            if (detected > 0 || _foundAttribute)
            {
                return 100 + 10 * (detected - 1);
            }

            return 0;
        }

        public override IEnumerable<int> GetStringDecrypterMethods()
        {
            return Enumerable.Empty<int>();
            //throw new NotImplementedException();
        }

        public override bool GetDecryptedModule(int count, ref byte[] newFileData, ref DumpedMethods dumpedMethods)
        {
            if (_normalMethodsDecrypter == null || !_normalMethodsDecrypter.Detected)
                return false;

            byte[] fileBytes = DeobUtils.ReadModule(module);
            using (var peImage = new MyPEImage(fileBytes))
            {
                if (_normalMethodsDecrypter.Decrypt(peImage, fileBytes))
                {
                    newFileData = fileBytes;
                    ModuleBytes = newFileData;
                    return true;
                }
            }

            return false;
        }

        public override IDeobfuscator ModuleReloaded(ModuleDefMD module)
        {
            var newDeob = new Deobfuscator(_options)
            {
                DeobfuscatedFile = DeobfuscatedFile,
                ModuleBytes = ModuleBytes
            };

            newDeob.SetModule(module);
            newDeob.ScanForNonDecrypterProtections();
            newDeob.AddModuleCctorInitCallToBeRemoved(_normalMethodsDecrypter.InitMethod);
            newDeob.AddMethodToBeRemoved(_normalMethodsDecrypter.InitMethod, "Anti-tamper init method");

            return newDeob;
        }

        void RemoveObfuscatorAttribute()
        {
            foreach (var type in module.Types)
            {
                if (type.FullName == "ConfusedByAttribute")
                {
                    _foundAttribute = true;
                    AddAttributeToBeRemoved(type, "Obfuscator attribute");
                    break;
                }
            }
        }

        internal class Options : OptionsBase
        {
            public bool RemoveAntiDebug { get; set; }
            public bool RemoveAntiDump { get; set; }
            public bool DecryptMainAsm { get; set; }
        }
    }
}