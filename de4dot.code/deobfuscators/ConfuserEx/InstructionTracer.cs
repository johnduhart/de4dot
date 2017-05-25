using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using de4dot.blocks;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public class InstructionTracer
    {
        private readonly IList<IList<Instr>> _traces = new List<IList<Instr>>();
        private readonly IList<Block> _blocks;
        private readonly Block _switchBlock;

        public InstructionTracer(IList<Block> blocks, Block switchBlock)
        {
            _blocks = blocks;
            _switchBlock = switchBlock;
        }

        public IEnumerable<IList<Instr>> Trace()
        {
            TraceInner(new Stack<Instr>(), _blocks[0]);

            return _traces;
        }

        private void TraceInner(Stack<Instr> currentStack, Block currentBlock)
        {
            while (true)
            {
                if (currentBlock == _switchBlock)
                {
                    _traces.Add(new List<Instr>(currentStack));
                    return;
                }

                // Add the instructions from the block to the stack
                foreach (Instr instr in currentBlock.Instructions)
                {
                    currentStack.Push(instr);
                }

                if (currentBlock.IsFallThrough())
                {

                    currentBlock = currentBlock.FallThrough;
                    continue;
                }

                if (currentBlock.IsConditionalBranch())
                {
                    // Trace the target branch seperately
                    TraceInner(new Stack<Instr>(currentStack), currentBlock.Targets[0]);
                    currentBlock = currentBlock.FallThrough;
                    continue;
                }

                Debug.Assert(false, "Reached bottom of while loop inside TraceInner");
            }
        }

        /*private void TraceInner(Stack<Instr> currentStack, int currentIndex)
        {
            while (currentIndex < _blocks.Count)
            {
                Instr current = _blocks[currentIndex];

                if (current == _switchBlock.FirstInstr)
                {
                    _traces.Add(new List<Instr>(currentIndex));
                    return;
                }

                if (current.IsBr())
                {
                    // Silently jump, don't add an instruction
                    var target = (Instruction)current.Operand;
                    Instr instr = _blocks.SingleOrDefault(i => i.Instruction == target);
                    Debug.Assert(instr != null, "Couldn't find br target");
                    currentIndex = _blocks.IndexOf(instr);
                    continue;
                }

                if (current.IsConditionalBranch())
                {

                }

                currentStack.Push(current);
            }
        }*/
    }
}