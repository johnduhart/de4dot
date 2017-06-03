using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using AssemblyData;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal class ConstantsDecrypter : IProtectionDetector
    {
        private readonly ModuleDef _module;
        private readonly ISimpleDeobfuscator _deobfuscator;

        private MethodDef _initMethod;
        private FieldDef _bField;
        private byte[] _bValue;
        private Dictionary<MethodDef, DecoderDesc> _getterMethods = new Dictionary<MethodDef, DecoderDesc>();

        public ConstantsDecrypter(ModuleDef module, ISimpleDeobfuscator deobfuscator)
        {
            _module = module;
            _deobfuscator = deobfuscator;
        }

        public bool Detected => _initMethod != null;
        public void Detect()
        {
            MethodDef method = DotNetUtils.GetModuleTypeCctor(_module);

            if (method?.Body == null)
                return;

            TypeDef moduleType = method.DeclaringType;

            List<FieldDef> containsByteArray = moduleType.Fields.Where(PossibleField)
                .ToList();

            if (containsByteArray.Count == 0)
                return;

            foreach (var m in DotNetUtils.GetMethodCalls(method))
            {
                var calledMethod = m as MethodDef;
                if (calledMethod == null)
                    continue;

                if (!DotNetUtils.IsMethod(calledMethod, "System.Void", "()"))
                    continue;

                string preGraph = new Blocks(calledMethod).ToBlockScope().ToGraph();
                _deobfuscator.Deobfuscate(calledMethod, SimpleDeobfuscatorFlags.Force);
                string postGraph = new Blocks(calledMethod).ToBlockScope().ToGraph();

                IList<Instruction> instructions = calledMethod.Body.Instructions;

                Instruction setFieldInstr = instructions[instructions.Count - 2];

                if (setFieldInstr.OpCode != OpCodes.Stsfld)
                    continue;

                FieldDef field = containsByteArray.FirstOrDefault(f => f == setFieldInstr.Operand);

                if (field == null)
                    continue;

                _initMethod = calledMethod;
                _bField = field;

                return;
            }
        }

        public void Init(StaticStringInliner stringInliner)
        {
            LoadDataBytes();
            LoadGetterMethods();

            // Create inliners
            foreach (var pair in _getterMethods)
            {
                stringInliner.Add(pair.Key, (method, gim, args) => DecryptString(method, gim, (uint)args[0]));
            }
        }
        static bool VerifyGenericArg(MethodSpec gim, ElementType etype)
        {
            if (gim == null)
                return false;
            var gims = gim.GenericInstMethodSig;
            if (gims == null || gims.GenericArguments.Count != 1)
                return false;
            return gims.GenericArguments[0].GetElementType() == etype;
        }

        private string DecryptString(MethodDef method, MethodSpec gim, uint id)
        {
            if (!VerifyGenericArg(gim, ElementType.String))
                return null;

            var desc = _getterMethods[method];

            // decode the id
            id = id * MathsUtils.modInv(desc.Key1) ^ desc.Key2;
            uint type = id >> 30;
            Debug.Assert(type == desc.StringID);

            id &= 0x3fffffff;
            id <<= 2;

            int length = _bValue[id++] | (_bValue[id++] << 8) | (_bValue[id++] << 16) | (_bValue[id++] << 24);
            return Encoding.UTF8.GetString(_bValue, (int) id, length);
        }


        private void LoadGetterMethods()
        {
            var methods = _initMethod.DeclaringType.Methods;
            var getterMethods = new List<MethodDef>();

            foreach (MethodDef methodDef in methods)
            {
                if (methodDef.GenericParameters.Count != 1)
                    continue;

                if (methodDef.Parameters.Count != 1)
                    continue;

                if (methodDef.Parameters[0].Type.ElementType != ElementType.U4)
                    continue;

                getterMethods.Add(methodDef);
            }

            foreach (MethodDef getterMethod in getterMethods)
            {
                _deobfuscator.Deobfuscate(getterMethod, SimpleDeobfuscatorFlags.Force);
                IList<Instruction> instructions = getterMethod.Body.Instructions;

                var blocks = new Blocks(getterMethod);

                var nonBranchBlocks = blocks.MethodBlocks.BaseBlocks.OfType<Block>()
                    .Where(b => !b.IsConditionalBranch() && b.LastInstr.OpCode != OpCodes.Ret && !b.IsNopBlock())
                    .ToList();


                //var stack = new Stack<byte>(3);

                /*var initialBlock = (Block) blocks.MethodBlocks.BaseBlocks[0];
                Debug.Assert(initialBlock.LastInstr.OpCode == OpCodes.Bne_Un_S || initialBlock.LastInstr.OpCode == OpCodes.Bne_Un);
                stack.Push((byte) initialBlock.Instructions[initialBlock.Instructions.Count - 2].GetLdcI4Value());*/

                /*for (var i = 0; i < instructions.Count && stack.Count < 3; i++)
                {
                    Instruction instruction = instructions[i];

                    if (instruction.OpCode != OpCodes.Bne_Un && instruction.OpCode != OpCodes.Bne_Un_S)
                        continue;

                    var ld = instructions[i - 2];
                    if (!ld.IsLdcI4())
                        continue;

                    stack.Push((byte) ld.GetLdcI4Value());
                }

                Debug.Assert(stack.Count == 3, "Getter ID stack does not contain 3 bytes");*/

                var desc = new DecoderDesc()
                {
                    //InitializerID = stack.Pop(),
                    //NumberID = stack.Pop(),
                    //StringID = stack.Pop()
                };

                foreach (Block block in nonBranchBlocks)
                {
                    // Figure out the value for this block
                    Debug.Assert(block.Sources.Count == 1);
                    var sourceBlock = block.Sources[0];

                    if (sourceBlock.IsNopBlock())
                        sourceBlock = sourceBlock.Sources[0];

                    var loadLdc = sourceBlock.Instructions[sourceBlock.Instructions.Count - 2];
                    Debug.Assert(loadLdc.OpCode == OpCodes.Ldc_I8);

                    var value = (byte) (long) loadLdc.Operand;

                    // Let's see if we can get away without checking the branch type
                    /*if (sourceBlock.LastInstr.OpCode == OpCodes.Bne_Un ||
                        sourceBlock.LastInstr.OpCode == OpCodes.Bne_Un_S)
                    {

                    }*/

                    // Fingerprint the block type
                    Instr callInstr = block.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Call);
                    Debug.Assert(callInstr != null, "Getter block doesn't make any call");
                    var firstCall = (MemberRef) callInstr.Operand;

                    switch (firstCall.FullName)
                    {
                        case "System.Text.Encoding System.Text.Encoding::get_UTF8()":
                            desc.StringID = value;
                            continue;
                        case "System.Void System.Buffer::BlockCopy(System.Array,System.Int32,System.Array,System.Int32,System.Int32)":
                            desc.InitializerID = value;
                            continue;
                        case "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)":
                            desc.NumberID = value;
                            continue;
                        default:
                            Debug.Assert(false, "Invalid getter call found");
                            break;
                    }
                }

                // NormalMode
                for (var i = 0; i < instructions.Count; i++)
                {
                    Instruction instruction = instructions[i];

                    if (!instruction.IsLdarg())
                        continue;

                    var key1 = instructions[i + 1];
                    if (!key1.IsLdcI4())
                        continue;

                    var key2 = instructions[i + 3];
                    if (!key2.IsLdcI4())
                        continue;

                    desc.Key1 = MathsUtils.modInv((uint) key1.GetLdcI4Value());
                    desc.Key2 = (uint) key2.GetLdcI4Value();
                    break;
                }

                _getterMethods.Add(getterMethod, desc);
            }
        }

        private void LoadDataBytes()
        {
            IList<Instruction> instructions = _initMethod.Body.Instructions;

            // Load the compressed data
            Instruction ldTokenInstr = instructions.First(i => i.OpCode == OpCodes.Ldtoken);
            byte[] rawByteArray = ((FieldDef) ldTokenInstr.Operand).InitialValue;
            var compressedData = new uint[rawByteArray.Length / 4];
            Buffer.BlockCopy(rawByteArray, 0, compressedData, 0, compressedData.Length * 4);

            // Load key
            uint keySeed = 0;
            for (int i = 0; i < instructions.Count - 2; i++)
            {
                if (instructions[i].OpCode != OpCodes.Newarr)
                    continue;

                var ldInstr = instructions[i + 2];
                if (!ldInstr.IsLdcI4())
                    continue;

                keySeed = (uint) ldInstr.GetLdcI4Value();
                break;
            }
            var key = new uint[16];
            for (int i = 0; i < 16; i++)
            {
                keySeed ^= keySeed >> 12;
                keySeed ^= keySeed << 25;
                keySeed ^= keySeed >> 27;
                key[i] = keySeed;
            }

            // Decrypt the array
            int num = 0;
            int num2 = 0;
            uint[] array2 = new uint[16];
            byte[] unencryptedData = new byte[compressedData.Length * 4u];
            while (num < compressedData.Length)
            {
                for (int j = 0; j < 16; j++)
                {
                    array2[j] = compressedData[num + j];
                }
                // TODO: this can vary
                array2[0] = (array2[0] ^ key[0]);
                array2[1] = (array2[1] ^ key[1]);
                array2[2] = (array2[2] ^ key[2]);
                array2[3] = (array2[3] ^ key[3]);
                array2[4] = (array2[4] ^ key[4]);
                array2[5] = (array2[5] ^ key[5]);
                array2[6] = (array2[6] ^ key[6]);
                array2[7] = (array2[7] ^ key[7]);
                array2[8] = (array2[8] ^ key[8]);
                array2[9] = (array2[9] ^ key[9]);
                array2[10] = (array2[10] ^ key[10]);
                array2[11] = (array2[11] ^ key[11]);
                array2[12] = (array2[12] ^ key[12]);
                array2[13] = (array2[13] ^ key[13]);
                array2[14] = (array2[14] ^ key[14]);
                array2[15] = (array2[15] ^ key[15]);
                for (int k = 0; k < 16; k++)
                {
                    uint num3 = array2[k];
                    unencryptedData[num2++] = (byte) num3;
                    unencryptedData[num2++] = (byte) (num3 >> 8);
                    unencryptedData[num2++] = (byte) (num3 >> 16);
                    unencryptedData[num2++] = (byte) (num3 >> 24);
                    key[k] ^= num3;
                }
                num += 16;
            }

            _bValue = Lzma.Decompress(unencryptedData);
        }

        private bool PossibleField(FieldDef f)
        {
            return f.IsStatic && f.IsAssembly && f.FieldType.IsSZArray && f.FieldType.ReflectionFullName == "System.Byte[]";
        }

        private class DecoderDesc
        {
            public byte InitializerID;
            public byte NumberID;
            public byte StringID;

            public uint Key1;
            public uint Key2;
        }
    }
}