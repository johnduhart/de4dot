using System;
using System.Collections.Generic;
using System.IO;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.IO;
using dnlib.PE;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal class NormalMethodsDecrypter : IProtectionDetector
    {
        private readonly ModuleDef _module;

        public NormalMethodsDecrypter(ModuleDef module)
        {
            _module = module;
        }

        public bool Detected => InitMethod != null;

        public MethodDef InitMethod { get; private set; }

        public void Detect()
        {
            MethodDef method = DotNetUtils.GetModuleTypeCctor(_module);

            if (method?.Body == null)
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

                if (!CheckMethod(calledMethod.DeclaringType, calledMethod))
                    continue;

                InitMethod = calledMethod;
                return;
            }
        }

        private bool CheckMethod(TypeDef declaringType, MethodDef initMethod)
        {
            if (declaringType == null)
                return false;

            MethodDef virtProtect = DotNetUtils.GetPInvokeMethod(declaringType, "kernel32", "VirtualProtect");
            if (virtProtect == null)
                return false;

            if (!DotNetUtils.CallsMethod(initMethod, virtProtect))
                return false;

            if (!DotNetUtils.CallsMethod(initMethod,
                "System.IntPtr System.Runtime.InteropServices.Marshal::GetHINSTANCE(System.Reflection.Module)"))
                return false;

            return true;
        }

        public bool Decrypt(MyPEImage peImage, byte[] fileBytes)
        {
            KeyState keyState = ReadKeyState(InitMethod);
            uint encryptedNameHash = ReadNameHash(InitMethod);
            ImageSectionHeader encryptedSection = null;

            foreach (ImageSectionHeader sectionHeader in peImage.Sections)
            {
                uint nameHash = BitConverter.ToUInt32(sectionHeader.Name, 0) *
                                BitConverter.ToUInt32(sectionHeader.Name, 4);

                if (nameHash == encryptedNameHash)
                {
                    encryptedSection = sectionHeader;
                }
                else if (nameHash != 0)
                {
                    IBinaryReader reader = peImage.Reader;
                    reader.Position = peImage.RvaToOffset((uint) sectionHeader.VirtualAddress);
                    //uint size = sectionHeader.VirtualSize;
                    uint size = sectionHeader.SizeOfRawData;
                    size >>= 2;

                    for (uint i = 0; i < size; i++)
                    {
                        uint data = reader.ReadUInt32();
                        uint tmp = (keyState.Z ^ data) + keyState.X + keyState.C * keyState.V;
                        keyState.Z = keyState.X;
                        keyState.X = keyState.C;
                        keyState.X = keyState.V;
                        keyState.V = tmp;
                    }
                }
            }

            if (encryptedSection == null)
            {
                return false;
            }

            uint[] key = DeriveKey(keyState);
            using (var writer = new BinaryWriter(new MemoryStream(fileBytes)))
            {
                IBinaryReader eReader = peImage.Reader;
                eReader.Position = peImage.RvaToOffset((uint)encryptedSection.VirtualAddress);
                uint eSize = encryptedSection.VirtualSize;
                eSize >>= 2;
                for (uint i = 0; i < eSize; i++)
                {
                    uint offset = (uint) eReader.Position;
                    uint data = eReader.ReadUInt32();
                    data ^= key[i & 0xf];
                    writer.BaseStream.Position = offset;
                    writer.Write(data);
                    key[i & 0xf] = (key[i & 0xf] ^ data) + 0x3dbb2819;
                }
            }

            var module = ModuleDefMD.Load(fileBytes);
            var mainMethod = module.ResolveMethod(4);
            int tdddmp = mainMethod.Body.Instructions.Count;

            return true;
        }

        private static KeyState ReadKeyState(MethodDef initMethod)
        {
            var instructions = initMethod.Body.Instructions;
            for (var i = 0; i < instructions.Count; i++)
            {
                Instruction instruction = instructions[i];

                if (!instruction.IsBr())
                    continue;

                if (instructions[i - 2].OpCode != OpCodes.Ldc_I4_0)
                    continue;

                var keyStack = new Stack<uint>(4);
                for (int j = i - 3; j >= 0 && keyStack.Count < 4; j--)
                {
                    Instruction ldInstruction = instructions[j];
                    if (!ldInstruction.IsLdcI4())
                        continue;

                    keyStack.Push((uint) ldInstruction.GetLdcI4Value());
                }

                if (keyStack.Count == 4)
                {
                    return new KeyState
                    {
                        Z = keyStack.Pop(),
                        X = keyStack.Pop(),
                        C = keyStack.Pop(),
                        V = keyStack.Pop()
                    };
                }
            }

            throw new NotImplementedException();
        }

        private static uint ReadNameHash(MethodDef initMethod)
        {
            var instructions = initMethod.Body.Instructions;
            for (var i = 0; i < instructions.Count; i++)
            {
                Instruction instruction = instructions[i];

                if (!instruction.IsBr())
                    continue;

                if (instructions[i - 2].OpCode != OpCodes.Ldc_I4_0)
                    continue;

                // This is the start of the for-loop
                for (int j = 0; j < instructions.Count; j++)
                {
                    instruction = instructions[j];

                    if (instruction.OpCode != OpCodes.Bne_Un && instruction.OpCode != OpCodes.Bne_Un_S)
                        continue;

                    if (instructions[j - 1].IsLdcI4())
                        return (uint) instructions[j - 1].GetLdcI4Value();
                }
            }

            throw new NotImplementedException();
        }

        private static uint[] DeriveKey(KeyState keyState)
        {
            uint[] dst = new uint[0x10], src = new uint[0x10];
            for (int i = 0; i < 0x10; i++)
            {
                dst[i] = keyState.V;
                src[i] = keyState.X;
                keyState.Z = (keyState.X >> 5) | (keyState.X << 27);
                keyState.X = (keyState.C >> 3) | (keyState.C << 29);
                keyState.C = (keyState.V >> 7) | (keyState.V << 25);
                keyState.V = (keyState.Z >> 11) | (keyState.Z << 21);
            }

            // TODO: Different Deriver
            var ret = new uint[0x10];
            for (int i = 0; i < 0x10; i++)
            {
                switch (i % 3)
                {
                    case 0:
                        ret[i] = dst[i] ^ src[i];
                        break;
                    case 1:
                        ret[i] = dst[i] * src[i];
                        break;
                    case 2:
                        ret[i] = dst[i] + src[i];
                        break;
                }
            }
            return ret;

            //return deriver.DeriveKey(dst, src);
        }

        private class KeyState
        {
            public uint Z;
            public uint X;
            public uint C;
            public uint V;
        }
    }
}