using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal class ConstantsDecrypter : IProtectionDetector
    {
        private readonly ModuleDef _module;

        private MethodDef _initMethod;
        private FieldDef _bField;
        private byte[] _bValue;
        private Dictionary<MethodDef, DecoderDesc> _getterMethods = new Dictionary<MethodDef, DecoderDesc>();

        public ConstantsDecrypter(ModuleDef module)
        {
            _module = module;
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
                IList<Instruction> instructions = getterMethod.Body.Instructions;
                var stack = new Stack<byte>(3);

                for (var i = 0; i < instructions.Count && stack.Count < 3; i++)
                {
                    Instruction instruction = instructions[i];

                    if (instruction.OpCode != OpCodes.Bne_Un && instruction.OpCode != OpCodes.Bne_Un_S)
                        continue;

                    var ld = instructions[i - 2];
                    if (!ld.IsLdcI4())
                        continue;

                    stack.Push((byte) ld.GetLdcI4Value());
                }

                var desc = new DecoderDesc()
                {
                    InitializerID = stack.Pop(),
                    NumberID = stack.Pop(),
                    StringID = stack.Pop()
                };

                // NormalMode
                for (var i = 0; i < instructions.Count && stack.Count < 3; i++)
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