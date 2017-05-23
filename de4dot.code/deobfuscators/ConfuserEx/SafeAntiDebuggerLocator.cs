using de4dot.blocks;
using de4dot.code.deobfuscators.Confuser;
using dnlib.DotNet;

namespace de4dot.code.deobfuscators.ConfuserEx
{
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