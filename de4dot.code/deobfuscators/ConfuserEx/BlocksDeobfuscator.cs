using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

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
            var switchHeaderStack = new Stack<Instr>();
            int currentInstructionIndex = 0;
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
            } while (true);

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
            if (switchHeaderStack.Peek().IsLdcI4())
            {
                predicate = ConfuserPredicate.None;
                switchHeader.AddFirst(switchHeaderStack.SafePop()); // LDC.i4
            }
            else if (switchHeaderStack.Peek().OpCode == OpCodes.Xor)
            {
                predicate = ConfuserPredicate.Normal;
                switchHeader.AddFirst(switchHeaderStack.SafePop()); // XOR
                switchHeader.AddFirst(switchHeaderStack.SafePop()); // LDC.i4
                switchHeader.AddFirst(switchHeaderStack.SafePop()); // LDC.i4
            }
            else
            {
                Debug.Assert(false, "Unkown switch header fingerprint");
            }

            var tracer = new SwitchTracer(_blocks, blocks, predicate, switchHeader.Select(i => i.Instruction).ToList())
            {
                SwitchInstruction = switchInstruction.Instruction,
                SwitchBlock = switchBlock
            };
            Block initialBlock;
            tracer.Trace(out initialBlock);

            foreach (Block switchBlockTarget in switchBlock.Targets)
            {
                switchBlockTarget.Sources.Remove(switchBlock);
            }
            switchBlock.Targets.Clear();
            switchBlock.Remove(0, switchBlock.Instructions.Count);
            //CleanupDeadSwitch(switchBlock);
            blocks.First().SetNewFallThrough(initialBlock);

            Logger.vv("ayyyyyy");


            return true;
        }

        public bool Deobfuscate(List<Block> allBlocks)
        {
            CilBody methodBody = _blocks.Method.Body;

            // Look for identifying marks that indiciate that the switch confuser
            // was used.

            // A managled method has a stack of at least two, and a uint local
            if (methodBody.MaxStack < 2
                || methodBody.Variables.All(v => v.Type != _blocks.Method.Module.CorLibTypes.UInt32))
                return false;

            IList<Instruction> instructions = methodBody.Instructions;

            // The method must contain at least one switch statement
            if (!instructions.Any(i => i.OpCode == OpCodes.Switch))
                return false;

            // ----------------------------


            // Determine the method scope
            BlockScope rootScope = GetBlockScope(_blocks.MethodBlocks);
            return ProcessScope(rootScope);
        }

        private BlockScope GetBlockScope(ScopeBlock currentScopeBlock)
        {
            var blockScope = new BlockScope();
            List<BaseBlock> baseBlocks = currentScopeBlock.BaseBlocks;

            var blockList = new List<Block>(baseBlocks.Count);

            void CreateBlockScope()
            {
                var scope = new BlockScope(blockList);
                blockScope.AddChild(scope);
                blockList.Clear();
            }

            foreach (BaseBlock baseBlock in baseBlocks)
            {
                var block = baseBlock as Block;
                if (block != null)
                {
                    blockList.Add(block);
                    continue;
                }

                // Found a non-block, empty the current blocks into a new scope
                CreateBlockScope();

                var scopeBlock = baseBlock as ScopeBlock;
                Debug.Assert(scopeBlock != null, "scopeBlock != null");

                var child = GetBlockScope(scopeBlock);
                blockScope.AddChild(child);
            }

            if (blockList.Count > 0)
                CreateBlockScope();

            return blockScope;
        }

        private void CleanupDeadSwitch(Block currentBlock, Stack<Block> currentPath = null)
        {
            if (currentPath == null)
                currentPath = new Stack<Block>();

            currentPath.Push(currentBlock);

            foreach (Block source in currentBlock.Sources.ToList())
            {
                if (currentPath.Contains(source))
                    continue;

                if (source.Sources.Count > 0)
                    CleanupDeadSwitch(source, currentPath);

                source.RemoveDeadBlock();
            }

            currentPath.Pop();
        }

        class SwitchTracer
        {
            private readonly Queue<BranchState> _branchesToProcess = new Queue<BranchState>();
            private readonly HashSet<Block> _processedBlocks = new HashSet<Block>();

            private readonly Blocks _blocks;
            private readonly IList<Block> _allBlocks;
            private readonly ConfuserPredicate _predicate;
            private readonly IList<Instruction> _switchHeaderInstructions;
            private readonly InstructionEmulator _instructionEmulator;
            private readonly Local _methodLocal;

            public SwitchTracer(Blocks blocks, IList<Block> allBlocks, ConfuserPredicate predicate, IList<Instruction> switchHeaderInstructions)
            {
                _blocks = blocks;
                _allBlocks = allBlocks;
                _predicate = predicate;
                _switchHeaderInstructions = switchHeaderInstructions;
                _instructionEmulator = new InstructionEmulator(_blocks.Method);
                _methodLocal = blocks.Method.Body.Variables.Last();

                Debug.Assert(_methodLocal.Type.ElementType == ElementType.U4);
            }

            public Instruction SwitchInstruction { get; set; }
            public Block SwitchBlock { get; set; }

            //
            public void Trace(out Block initialBlock)
            {
                // emulate the header
                _instructionEmulator.Emulate(_switchHeaderInstructions.Take(_switchHeaderInstructions.Count - 1).Select(i => new Instr(i)));
                initialBlock = NextSwitchBlock();
                EnqueueBranch(initialBlock);

                while (_branchesToProcess.Count > 0)
                {
                    BranchState branchState = _branchesToProcess.Dequeue();
                    Block currentBlock = branchState.NextBlock;

                    if (_processedBlocks.Contains(currentBlock))
                    {
                        //Debugger.Break();
                        continue;
                    }

                    _instructionEmulator.SetLocal(_methodLocal, branchState.LocalValue);
                    ProcessBlock(currentBlock);

                    _processedBlocks.Add(currentBlock);
                }
            }

            private void ProcessBlock(Block currentBlock)
            {
                int index = currentBlock.Instructions.Count - 1;
                if (currentBlock.Instructions[index].OpCode == OpCodes.Xor
                    && currentBlock.Instructions[index - 1].IsLdcI4()
                    && currentBlock.Instructions[index - 2].OpCode == OpCodes.Mul
                    && currentBlock.Instructions[index - 3].IsLdcI4()
                    && currentBlock.Instructions[index - 4].IsLdloc())
                {
                    Debug.Assert(currentBlock.FallThrough == SwitchBlock);

                    // This is a simple jump case..
                    // Play these final 5 instructions, and then the header
                    _instructionEmulator.Emulate(currentBlock.Instructions, index - 4, index + 1);
                    EmulateSwitchHeader();
                    var targetBlock = NextSwitchBlock();

                    bool insertBranch = true;

                    // Empty block
                    if (currentBlock.Instructions.Count == 5
                        && currentBlock.Sources.Count == 1)
                    {
                        Block previousBlock = currentBlock.Sources.Single();
                        if (previousBlock.FallThrough == currentBlock)
                        {
                            previousBlock.SetNewFallThrough(targetBlock);
                        }
                        else
                        {
                            // probably ok for now..
                            //Debugger.Break();
                        }

                        insertBranch = false;
                        currentBlock.Remove(0, 5);
                    }

                    if (currentBlock.IsOnlySource(SwitchBlock))
                    {
                        // At this point we should just mark this one for deletion
                        //currentBlock.Re
                    }

                    if (insertBranch)
                        currentBlock.ReplaceLastInstrsWithBranch(5, targetBlock);

                    EnqueueBranch(targetBlock);
                    return;
                }

                if (currentBlock.IsConditionalBranch())
                {
                    Debug.Assert(currentBlock.CountTargets() == 2);

                    // Navigate both branches to find a common root
                    Block branchABlock = currentBlock.FallThrough;
                    Block branchBBlock = currentBlock.Targets[0];

                    if (branchABlock.GetOnlyTarget() != branchBBlock.GetOnlyTarget())
                    {
                        // Missing condition
                        //Debugger.Break();

                        // This is likely a result of a jump outside the switch, make sure
                        // branchA remains inside of the switch
                        Debug.Assert(branchABlock.FallThrough == SwitchBlock);
                        EnqueueBranch(branchABlock);
                        return;
                    }

                    #region old crap

                    /*for (int depth = 0; depth < 20; depth++)
                    {
                        bool branchAMaxDepth = false, branchBMaxDepth = false;

                        // Check to see if it fallthroughs the switch
                        if (branchABlock.FallThrough == SwitchBlock)
                        {
                            branchAMaxDepth = true;
                        }
                        else
                        {
                            if (branchABlock.IsFallThrough())
                            {
                                branchABlock = branchABlock.FallThrough;
                            }
                        }

                        // Check to see if it fallthroughs the switch
                        if (branchBBlock.FallThrough == SwitchBlock)
                        {
                            branchBMaxDepth = true;
                        }
                        else
                        {
                            if (branchBBlock.IsFallThrough())
                            {
                                branchBBlock = branchBBlock.FallThrough;
                            }
                        }

                        if (branchBMaxDepth && branchAMaxDepth)
                            break;
                    }*/

                    #endregion

                    Block rootBlock = branchABlock.GetOnlyTarget();
                    if (rootBlock.FallThrough != SwitchBlock && rootBlock.IsConditionalBranch())
                    {
                        // This is another conditional branch, add to processing
                        EnqueueBranch(rootBlock);
                        return;
                    }

                    Debug.Assert(rootBlock.FallThrough == SwitchBlock);

                    void ProcessBranch(Block branchBlock)
                    {
                        // Capture the current state
                        Value previousLocalValue = _instructionEmulator.GetLocal(_methodLocal);

                        _instructionEmulator.Emulate(branchBlock.Instructions);
                        _instructionEmulator.Emulate(rootBlock.Instructions);
                        EmulateSwitchHeader();
                        var nextBlock = NextSwitchBlock();
                        branchBlock.ReplaceLastInstrsWithBranch(branchBlock.Instructions.Count, nextBlock);

                        EnqueueBranch(nextBlock);

                        // Restore local value
                        _instructionEmulator.SetLocal(_methodLocal, previousLocalValue);
                    }

                    ProcessBranch(branchABlock);
                    ProcessBranch(branchBBlock);
                    return;

                    #region more crap

                    /*if (branchABlock == branchBBlock)
                    {
                        Debug.Assert(branchABlock.Sources.Count == 2);

                        // Common roots
                        //Block rootBlock = branchABlock;
                        branchABlock = rootBlock.Sources[0];
                        branchBBlock = rootBlock.Sources[1];

                        Debug.Assert(rootBlock.Instructions.Count == 1);
                        Debug.Assert(rootBlock.Instructions[0].IsPop());

                        void ProcessBranchX(Block branchBlock)
                        {
                            if (branchBlock.Instructions.Count == 2)
                            {
                                if (branchBlock.Instructions[0].IsLdcI4()
                                    && (branchBlock.Instructions[1].IsLdcI4()
                                        || branchBlock.Instructions[1].OpCode == OpCodes.Dup))
                                {
                                    _instructionEmulator.Emulate(branchBlock.Instructions[0].Instruction);
                                    EmulateSwitchHeader();
                                    var nextBlock = NextSwitchBlock();
                                    branchBlock.ReplaceLastInstrsWithBranch(2, nextBlock);

                                    EnqueueBranch(nextBlock);
                                }
                            }
                            else
                            {
                                Debug.Assert(false);
                            }
                        }

                        ProcessBranchX(branchABlock);
                        ProcessBranchX(branchBBlock);

                        return;
                    }*/

                    #endregion
                }

                // Single ldc.i4 instruction
                if (currentBlock.Instructions.Count == 1
                    && currentBlock.Instructions[0].IsLdcI4()
                    && currentBlock.FallThrough == SwitchBlock)
                {
                    _instructionEmulator.Emulate(currentBlock.Instructions[0].Instruction);
                    EmulateSwitchHeader();
                    Block targetBlock = NextSwitchBlock();

                    currentBlock.ReplaceLastInstrsWithBranch(1, targetBlock);

                    // Optimize the non switch block sources to directly target
                    // our target
                    foreach (Block block in currentBlock.Sources.ToList().Where(s => s != SwitchBlock))
                    {
                        if (block.FallThrough == currentBlock)
                            block.SetNewFallThrough(targetBlock);
                    }

                    EnqueueBranch(targetBlock);

                    return;
                }

                // Operation with single ldc.i4 for switch
                if (currentBlock.LastInstr.IsLdcI4()
                    && currentBlock.GetOnlyTarget() == SwitchBlock)
                {
                    _instructionEmulator.Emulate(currentBlock.LastInstr.Instruction);
                    EmulateSwitchHeader();
                    Block targetBlock = NextSwitchBlock();

                    currentBlock.ReplaceLastInstrsWithBranch(1, targetBlock);

                    EnqueueBranch(targetBlock);

                    return;
                }

                // Method return statement
                if (currentBlock.CountTargets() == 0
                    && currentBlock.LastInstr.OpCode == OpCodes.Ret)
                {
                    // Oh cool, return statement. We're good, nothing to do here
                    return;
                }

                if (currentBlock.IsNopBlock() && currentBlock.IsOnlySource(SwitchBlock))
                {
                    // Do nothing
                    return;
                }

                // Try blocks have leave statements
                if (currentBlock.LastInstr.IsLeave() && currentBlock.Parent is TryBlock)
                {
                    return;
                }

                // Catch blocks can rethrow
                if (currentBlock.LastInstr.OpCode == OpCodes.Rethrow && currentBlock.Parent is HandlerBlock)
                {
                    return;
                }

                // Just a fallthrough?
                if (currentBlock.Targets != null
                    && currentBlock.Targets.Any(b => b != SwitchBlock)
                    && currentBlock.IsFallThrough())
                {
                    EnqueueBranch(currentBlock.FallThrough);
                    return;
                }

                // Switch fall-through, do nothing
                if (SwitchBlock.FallThrough?.FallThrough == currentBlock)
                {
                    return;
                }

                Debug.Assert(false, "Unhandled state");
            }

            private void EmulateSwitchHeader()
            {
                _instructionEmulator.Emulate(_switchHeaderInstructions.Skip(1)
                    .Take(_switchHeaderInstructions.Count - 2)
                    .Select(i => new Instr(i)));
            }

            private Block NextSwitchBlock()
            {
                var switchValue = _instructionEmulator.Pop() as Int32Value;

                Debug.Assert(switchValue != null, "Next switch statement isn't available");

                /*var switchTarget = ((Instruction[])SwitchInstruction.Operand)[switchValue.Value];
                Block targetBlock = SwitchBlock.Targets.SingleOrDefault(b => b.FirstInstr.Instruction == switchTarget);

                if (targetBlock == null)
                {
                    Debugger.Break();
                }*/

                return SwitchBlock.Targets[switchValue.Value];
            }

            void EnqueueBranch(Block targetBlock)
            {
                if (_instructionEmulator.StackSize() > 0)
                {
                    // This might not be right
                    Debugger.Break();
                }
                Debug.Assert(targetBlock != SwitchBlock, "You can't enqueue the switch block that makes zero sense");

                // Capture the current state
                Value currentLocalValue = _instructionEmulator.GetLocal(_methodLocal);

                _branchesToProcess.Enqueue(new BranchState(targetBlock, currentLocalValue));
            }

            private struct BranchState
            {
                public readonly Block NextBlock;
                public readonly Value LocalValue;

                public BranchState(Block nextBlock, Value localValue)
                {
                    NextBlock = nextBlock;
                    LocalValue = localValue;
                }
            }
        }

        enum ConfuserPredicate
        {
            Unkown,
            None,
            Normal
        }

        class BlockScope
        {
            private readonly IList<BlockScope> _children = new List<BlockScope>();

            public BlockScope()
                : this(Enumerable.Empty<Block>())
            {
            }

            public BlockScope(IEnumerable<Block> blocks)
            {
                Blocks = new List<Block>(blocks);
            }

            public BlockScope Parent { get; private set; }

            public IList<BlockScope> Children
            {
                get { return _children; }
            }

            public IList<Block> Blocks { get; set; }

            public void AddChild(BlockScope childScope)
            {
                childScope.Parent = this;
                _children.Add(childScope);
            }
        }
    }

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

        public static IList<Instr> GetInstructions(this IEnumerable<Block> blocks)
        {
            var list = new List<Instr>(blocks.SelectMany(b => b.Instructions));

            return list;
        }
    }
}