using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet.Emit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal class BlocksDeobfuscator : IBlocksDeobfuscator
    {
        private Blocks _blocks;

        public bool ExecuteIfNotModified { get; }

        public void DeobfuscateBegin(Blocks blocks)
        {
            _blocks = blocks;
        }

        private bool ProcessScope(BlockScope scope)
        {
            bool modified = false;
            foreach (BlockScope child in scope.Children)
            {
                modified |= ProcessScope(child);
            }

            if (scope.Blocks.Count > 0)
                modified |= ProcessScopeBlocks(scope.Blocks);

            return modified;
        }

        private IList<IList<Block>> TraceInstructions(IList<Block> blocks, Block switchBlock)
        {
            return new InstructionTracer(blocks, switchBlock).Trace();
        }

        private void FindSuitableSwitchInScope(IList<Block> blocks)
        {
            // Process the blocks in this scope
            var instructions = blocks.GetInstructions();

            // Find all switch statements within blocks
            foreach (Instr instr in instructions.Where(i => i.OpCode == OpCodes.Switch))
            {
                int switchIndex = instructions.IndexOf(instr);

                // The switch statement has known instructions before it
                if (switchIndex < 5)
                    return false;
            }


            // Find the first switch statement
            Instr switchInstruction = instructions.FirstOrDefault(i => i.OpCode == OpCodes.Switch);
            if (switchInstruction == null)
                return false;
        }

        private bool ProcessScopeBlocks(IList<Block> blocks)
        {
            // Process the blocks in this scope
            var instructions = blocks.GetInstructions();

            // Find the first switch statement
            Instr switchInstruction = instructions.FirstOrDefault(i => i.OpCode == OpCodes.Switch);
            if (switchInstruction == null)
                return false;

            int switchIndex = instructions.IndexOf(switchInstruction);

            // The switch statement has known instructions before it
            if (switchIndex < 5)
                return false;

            if (instructions[switchIndex - 1].OpCode != OpCodes.Rem_Un
                || !instructions[switchIndex - 2].IsLdcI4()
                || !instructions[switchIndex - 3].IsStloc()
                || instructions[switchIndex - 4].OpCode != OpCodes.Dup)
                return false;

            // Find the block containing the switch instruction
            Block switchBlock = blocks.FirstOrDefault(b => b.Instructions.Any(i => i == switchInstruction));

            // TODO: Fix this
            if (switchBlock == null)
                return false;

            // Trace from the beginning of the method to find the switch header
            IList<IList<Block>> initialInstructions = TraceInstructions(blocks, switchBlock);

            var switchHeaderStack = new Stack<Instr>();
            foreach (Instr instruction in switchBlock.Instructions)
            {
                switchHeaderStack.Push(instruction);
            }

           /* int currentInstructionIndex = 0;
            do
            {
                Instr current = instructions[currentInstructionIndex];
                switchHeaderStack.Push(current);

                if (current == switchInstruction)
                    break;

                if (current.IsBr())
                {
                    switchHeaderStack.Pop();
                    var target = (Instruction)current.Operand;
                    Instr instr = instructions.SingleOrDefault(i => i.Instruction == target);
                    Debug.Assert(instr != null, "Couldn't find br target");
                    currentInstructionIndex = instructions.IndexOf(instr);
                    continue;
                }

                currentInstructionIndex++;
            } while (true);*/

            // Figure out what predicate is in use
            ConfuserPredicate predicate = ConfuserPredicate.None;
            var switchHeader = new LinkedList<Instr>();

            // Start by unwinding the first four instructions
            switchHeader.AddFirst(switchHeaderStack.SafePop()); // switch
            switchHeader.AddFirst(switchHeaderStack.SafePop()); // rem.un
            switchHeader.AddFirst(switchHeaderStack.SafePop()); // ldc.i4
            switchHeader.AddFirst(switchHeaderStack.SafePop()); // stloc
            switchHeader.AddFirst(switchHeaderStack.SafePop()); // dup

            // No predicate (debugging)
            if (switchHeaderStack.Count == 0)
            {
                predicate = ConfuserPredicate.None;
                //switchHeader.AddFirst(switchHeaderStack.SafePop()); // LDC.i4
            }
            else if (switchHeaderStack.Peek().OpCode == OpCodes.Xor)
            {
                predicate = ConfuserPredicate.Normal;
                switchHeader.AddFirst(switchHeaderStack.SafePop()); // XOR
                switchHeader.AddFirst(switchHeaderStack.SafePop()); // LDC.i4
                //switchHeader.AddFirst(switchHeaderStack.SafePop()); // LDC.i4
            }
            else
            {
                Debug.Assert(false, "Unkown switch header fingerprint");
            }


            // Figure out what instructions are relevent
            var initialBlocks = new List<Block>();

            if (predicate == ConfuserPredicate.Normal)
            {
                // Does the switch header contain the first instruction? http://i.jhd5.net/2017/05/2017-05-28_19-32-09.png
                if (switchBlock.Instructions.Count == 8 && switchBlock.FirstInstr.IsLdcI4())
                {
                    // Okay, let's simulate this to find the first block. ugh.
                    var emulator = new InstructionEmulator(_blocks.Method);
                    emulator.Emulate(switchBlock.FirstInstr.Instruction);
                    emulator.Emulate(switchHeader.TakeWhile(i => i.OpCode != OpCodes.Switch));
                    var switchValue = emulator.Pop() as Int32Value;
                    Debug.Assert(switchValue != null, "Next switch statement isn't available");

                    initialBlocks.Add(switchBlock.Targets[switchValue.Value]);
                }
                else
                {
                    foreach (IList<Block> initialBlockChain in initialInstructions)
                    {
                        foreach (Block block in initialBlockChain.Reverse())
                        {
                            bool containsLdc = block.Instructions.Any(i => i.IsLdcI4());
                            if (containsLdc)
                            {
                                initialBlocks.Add(block);

                                // Hack for branches that dup, br, pop
                                if (block.LastInstr.OpCode == OpCodes.Dup && block.FallThrough.FirstInstr.OpCode == OpCodes.Pop)
                                    block.Instructions.Remove(block.LastInstr);

                                if (block.FallThrough != switchBlock)
                                    block.SetNewFallThrough(switchBlock);

                                break;
                            }
                        }
                    }
                }
            }

            //Debug.Assert(initialBlocks.Count > 0, "There are no initial blocks for tracing");
            if (initialBlocks.Count == 0)
            {
                Logger.w("No initial blocks found...");
                return false;
            }

            /*var initialInstructions2 = initialInstructions
                .Select(l => l.First(i => i.IsLdcI4()))
                .Select(i => new List<Instr>(1){i})
                .ToList();*/

            var tracer = new SwitchTracer(_blocks.Method, switchHeader.Select(i => i.Instruction).ToList(), switchBlock,
                blocks);

            tracer.Trace(initialBlocks);

            foreach (Block switchBlockTarget in switchBlock.Targets)
            {
                switchBlockTarget.Sources.Remove(switchBlock);
            }
            switchBlock.Targets.Clear();
            switchBlock.Remove(0, switchBlock.Instructions.Count);
            //CleanupDeadSwitch(switchBlock);
            /*if (initialBlocks.Count == 1)
            {
                blocks[0].SetNewFallThrough(initialBlocks[0]);
            }*/

            Logger.vv("ayyyyyy");


            return true;
        }

        public bool Deobfuscate(List<Block> allBlocks)
        {
            CilBody methodBody = _blocks.Method.Body;

            // fucking hack to troubleshoot issue
            if (_blocks.Method.Name == "ProcessDataMessage")
                return false;

            // Look for identifying marks that indiciate that the switch confuser
            // was used.

            // A managled method has a stack of at least two, and a uint local
            if (methodBody.MaxStack < 2
                || methodBody.Variables.All(v => v.Type != _blocks.Method.Module.CorLibTypes.UInt32))
                return false;

            IList<Instr> instructions = allBlocks.GetInstructions();

            // The method must contain at least one switch statement
            if (!instructions.Any(i => i.OpCode == OpCodes.Switch))
                return false;

            // ----------------------------


            // Determine the method scope
            BlockScope rootScope = BlockScopeBuilder.Parse(_blocks);
            string graphTest = BlockScopeGraphviz.Graph(rootScope);
            return ProcessScope(rootScope);
        }

        /// <summary>
        /// Type of predicate in use for the switch statement
        /// </summary>
        enum ConfuserPredicate
        {
            Unkown,
            None,
            Normal
        }
    }
}