using dnlib.DotNet;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal interface IAntiDebuggerLocator
    {
        bool CheckMethod(TypeDef type, MethodDef initMethod);
    }
}