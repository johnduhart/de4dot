using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal static class ConfuserExtensions
    {
        public static Instruction SafePop(this Stack<Instruction> stack)
        {
            var instr = stack.Pop();
            if (instr.OpCode == OpCodes.Pop && stack.Peek().OpCode == OpCodes.Dup)
            {
                stack.Pop();

                return SafePop(stack);
            }

            return instr;
        }

        public static Instr SafePop(this Stack<Instr> stack)
        {
            var instr = stack.Pop();
            if (instr.OpCode == OpCodes.Pop && stack.Peek().OpCode == OpCodes.Dup)
            {
                stack.Pop();

                return SafePop(stack);
            }

            return instr;
        }

        /// <summary>
        /// Dangerous, see remark.
        /// Gets the instructions contained in the blocks
        /// </summary>
        /// <param name="blocks">The blocks</param>
        /// <returns>Instructions</returns>
        /// <remarks>
        /// DANGER!! This will be missing instructions
        /// that are stripped when converting to block
        /// </remarks>
        public static IList<Instr> GetInstructions(this IEnumerable<Block> blocks)
        {
            var list = new List<Instr>(blocks.SelectMany(b => b.Instructions));

            return list;
        }

        public static bool IsScopeExit(this Instr instr)
        {
            return instr.Instruction.IsScopeExit();
        }

        public static bool IsScopeExit(this Instruction instruction)
        {
            return instruction.OpCode == OpCodes.Leave
                   || instruction.OpCode == OpCodes.Leave_S
                   || instruction.OpCode == OpCodes.Ret
                   || instruction.OpCode == OpCodes.Throw
                   || instruction.OpCode == OpCodes.Endfinally;
        }
    }
}