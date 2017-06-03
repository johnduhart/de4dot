using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    class SwitchTracer
    {
        private readonly Queue<BranchState> _branchesToProcess = new Queue<BranchState>();
        private readonly HashSet<Block> _processedBlocks = new HashSet<Block>();
        private readonly HashSet<Block> _tamperedBlocks = new HashSet<Block>();

        private readonly IList<Instruction> _switchHeaderInstructions;
        private readonly IList<Block> _blocksInScope;
        private readonly InstructionEmulator _instructionEmulator;
        private readonly Local _methodLocal;
        private readonly BranchTargetTracer _branchTargetTracer;

        public SwitchTracer(MethodDef method, IList<Instruction> switchHeaderInstructions, Block switchBlock, IList<Block> blocksInScope)
        {
            _switchHeaderInstructions = switchHeaderInstructions;
            _blocksInScope = blocksInScope;
            SwitchBlock = switchBlock;

            _instructionEmulator = new InstructionEmulator(method);
            _methodLocal = method.Body.Variables.Last();

            // TODO: HACK: Passing the processed blocks directly to the tracer
            _branchTargetTracer = new BranchTargetTracer(blocksInScope, switchBlock, _processedBlocks);

            Debug.Assert(_methodLocal.Type.ElementType == ElementType.U4);
        }

        private Block SwitchBlock { get; }

        //
        public void Trace(IList<Block> initialBlocks)
        {
            /*var initialBlocks = new List<Block>();

                // emulate the header
                foreach (IList<Instr> initialInstruction in initialInstructions)
                {
                    _instructionEmulator.Emulate(initialInstruction);
                    EmulateSwitchHeader();
                    Block block = NextSwitchBlock();
                    EnqueueBranch(block);
                    initialBlocks.Add(block);
                }*/

            foreach (Block initialBlock in initialBlocks)
            {
                EnqueueBranch(initialBlock);
            }

            /*_instructionEmulator.Emulate(_switchHeaderInstructions.Take(_switchHeaderInstructions.Count - 1).Select(i => new Instr(i)));
                Block initialBlock = NextSwitchBlock();
                EnqueueBranch(initialBlock);*/

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

            //return initialBlocks;
        }

        private void ProcessBlock(Block currentBlock)
        {
            int index = currentBlock.Instructions.Count - 1;
            if (HasXorMulTypeAFingerprint(currentBlock))
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


                _tamperedBlocks.Add(currentBlock);

                EnqueueBranch(targetBlock);
                return;
            }

            if (currentBlock.IsConditionalBranch())
            {
                if (ProcessConditionalBlock(currentBlock))
                    return;
            }

            if (currentBlock.IsConditionalBranch())
            {
                Debug.Assert(currentBlock.CountTargets() == 2);

                // Navigate both branches to find a common root
                Block branchABlock = currentBlock.FallThrough;
                Block branchBBlock = currentBlock.Targets[0];

                if (_processedBlocks.Contains(branchABlock) && _processedBlocks.Contains(branchBBlock))
                {
                    return;
                }

                if (!_blocksInScope.Contains(branchABlock) && !_blocksInScope.Contains(branchBBlock))
                {
                    return;
                }

                if (branchABlock.LastInstr.IsScopeExit() && branchBBlock.LastInstr.IsScopeExit())
                {
                    return;
                }

                if (branchABlock.GetOnlyTarget() != branchBBlock.GetOnlyTarget())
                {
                    // Missing condition
                    //Debugger.Break();

                    // This is likely a result of a jump outside the switch, make sure
                    // branchA remains inside of the switch

                    if (branchABlock.FallThrough == SwitchBlock)
                    {
                        EnqueueBranch(branchABlock);
                        return;
                    }

                    // http://i.jhd5.net/2017/05/2017-05-28_21-10-17.png
                    if (branchBBlock == branchABlock.FallThrough
                        && branchBBlock.GetOnlyTarget() == SwitchBlock)
                    {
                        Debug.Assert(branchBBlock.LastInstr.IsLdcI4());
                        EnqueueBranch(branchBBlock);
                        return;
                    }

                    // FUCK http://i.jhd5.net/2017/05/2017-05-28_21-18-05.png
                    if (branchBBlock == branchABlock.FallThrough
                        && branchBBlock.IsConditionalBranch())
                    {
                        EnqueueBranch(branchBBlock);
                        return;
                    }

                    // FUCKx2 http://i.jhd5.net/2017/05/2017-05-28_21-25-54.png
                    if (branchBBlock == branchABlock.FallThrough
                        && branchBBlock.LastInstr.IsScopeExit())
                    {
                        return;
                    }

                    // I give up
                    if (_blocksInScope.Contains(branchBBlock))
                        EnqueueBranch(branchBBlock);
                    if (_blocksInScope.Contains(branchABlock))
                        EnqueueBranch(branchABlock);

                    return;

                    //Debug.Assert(false, "Conditional branch target hell");
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

                // http://i.jhd5.net/2017/05/chrome_2017-05-28_22-53-41.png
                if (rootBlock == null && branchABlock.LastInstr.IsScopeExit())
                {
                    EnqueueBranch(branchBBlock);
                    return;
                }

                Debug.Assert(rootBlock != null, "root block was null");
                if (rootBlock.FallThrough != SwitchBlock && rootBlock.IsConditionalBranch())
                {
                    // This is another conditional branch, add to processing
                    EnqueueBranch(rootBlock);
                    return;
                }

                // http://i.jhd5.net/2017/05/2017-05-28_22-49-55.png
                if (rootBlock == SwitchBlock)
                {
                    EnqueueBranch(branchABlock);
                    EnqueueBranch(branchBBlock);
                    return;
                }

                // http://i.jhd5.net/2017/06/2017-06-01_22-23-39.png
                if (rootBlock.LastInstr.IsScopeExit())
                {
                    // Nothing to do here.
                    return;
                }

                // http://i.jhd5.net/2017/06/2017-06-01_22-26-33.png
                if (rootBlock.FallThrough != null && !_blocksInScope.Contains(rootBlock.FallThrough))
                {
                    // Nothing to do here
                    return;
                }

                Debug.Assert(rootBlock.FallThrough == SwitchBlock, "Conditional branch target hell, switch block edition");

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
                _tamperedBlocks.Add(currentBlock);

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
                _tamperedBlocks.Add(currentBlock);

                EnqueueBranch(targetBlock);

                return;
            }

            // Method return statement
            if (currentBlock.CountTargets() == 0
                && (currentBlock.LastInstr.OpCode == OpCodes.Ret
                    || currentBlock.LastInstr.OpCode == OpCodes.Throw))
            {
                // Oh cool, return statement. We're good, nothing to do here
                return;
            }

            if (currentBlock.IsNopBlock() && currentBlock.IsOnlySource(SwitchBlock))
            {
                // Do nothing
                return;
            }

            if (currentBlock.IsNopBlock())
            {
                EnqueueBranch(currentBlock.FallThrough);
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

            if (currentBlock.LastInstr.OpCode == OpCodes.Switch)
            {
                // fuck.
                foreach (Block blockTarget in currentBlock.GetTargets())
                {
                    EnqueueBranch(blockTarget);
                }
                return;
            }

            // http://i.jhd5.net/2017/05/2017-05-28_22-07-00.png
            if (currentBlock.IsFallThrough())
            {
                EnqueueBranch(currentBlock.FallThrough);
                return;
            }

            Debug.Assert(false, "Unhandled state");
        }

        private bool ProcessConditionalBlock(Block currentBlock)
        {
            var targetBlocks = currentBlock.GetTargets().ToArray();
            Debug.Assert(targetBlocks.All(b => b != SwitchBlock), "CoditionalBlock: One target is the switch block");

            var traces = _branchTargetTracer.TraceFrom(currentBlock);

            if (traces.Count == 0)
                return true;

            // Case A: CondBranch --> ldc.i4, dup --> pop, br to switch
            if (traces.Count == 2
                && traces[0].Last() == traces[1].Last()
                && traces[0].Count == 3 && traces[1].Count == 3)
            {
                var lastBlock = traces[0].Last();
                bool lastPop = lastBlock.Instructions?[0].OpCode == OpCodes.Pop;
                Block middleA = traces[0][1];
                Block middleB = traces[1][1];

                if (middleA.Instructions.Any(i => i.IsLdcI4())
                    && middleB.Instructions.Any(i => i.IsLdcI4()))
                {
                    void ProcessBranch(Block branchBlock)
                    {
                        // Capture the current state
                        Value previousLocalValue = _instructionEmulator.GetLocal(_methodLocal);

                        _instructionEmulator.Emulate(branchBlock.Instructions);
                        _instructionEmulator.Emulate(lastBlock.Instructions);
                        EmulateSwitchHeader();
                        var nextBlock = NextSwitchBlock();
                        branchBlock.ReplaceLastInstrsWithBranch(branchBlock.Instructions.Count, nextBlock);
                        _tamperedBlocks.Add(branchBlock);

                        EnqueueBranch(nextBlock);

                        // Restore local value
                        _instructionEmulator.SetLocal(_methodLocal, previousLocalValue);
                    }

                    ProcessBranch(middleA);
                    ProcessBranch(middleB);

                    return true;
                }
            }

            //return false;

            bool handledBranch = false;

            foreach (IList<Block> trace in traces)
            {
                var lastBlock = trace.Last();

                // ldc.i4 then br
                if (lastBlock.LastInstr.IsLdcI4())
                {
                    EnqueueBranch(lastBlock);
                    handledBranch |= true;
                    continue;
                }

                // ldloc
                // ldc.i4
                // mul
                // ldc.i4
                // xor
                if (HasXorMulTypeAFingerprint(lastBlock))
                {
                    EnqueueBranch(lastBlock);
                    handledBranch |= true;
                    continue;
                }

                bool ProcessTwoBlock()
                {
                    var penultimateBlock = trace[trace.Count - 2];
                    if (penultimateBlock.Instructions.Any(i => i.IsLdcI4()))
                    {
                        // TODO: This is a copy-paste of ProcessBlock above

                        // Capture the current state
                        Value previousLocalValue = _instructionEmulator.GetLocal(_methodLocal);

                        _instructionEmulator.Emulate(penultimateBlock.Instructions);
                        _instructionEmulator.Emulate(lastBlock.Instructions);
                        EmulateSwitchHeader();
                        var nextBlock = NextSwitchBlock();

                        penultimateBlock.ReplaceLastInstrsWithBranch(penultimateBlock.Instructions.Count, nextBlock);
                        _tamperedBlocks.Add(penultimateBlock);

                        EnqueueBranch(nextBlock);

                        // Restore local value
                        _instructionEmulator.SetLocal(_methodLocal, previousLocalValue);

                        handledBranch |= true;
                        return true;
                    }

                    if (penultimateBlock.IsNopBlock() && _tamperedBlocks.Contains(penultimateBlock))
                    {
                        // It's possible this block was already touched, so let's forget about this
                        return true;
                    }

                    return false;
                }

                // ldc.i4
                // dup
                // ---
                // pop
                // ldloc
                // ldc.i4
                // mul
                // xor
                if (trace.Count > 2 && HasXorMulTypeBFingerprint(lastBlock))
                {
                    if (ProcessTwoBlock())
                        continue;
                }

                // ldc.i4
                // dup
                // ---
                // pop
                if (trace.Count > 2 && lastBlock.LastInstr.OpCode == OpCodes.Pop && lastBlock.Instructions.Count == 1)
                {
                    if (ProcessTwoBlock())
                        continue;
                }

                Debugger.Break();
            }

            return handledBranch;
        }

        private static bool HasXorMulTypeAFingerprint(Block block)
        {
            int index = block.Instructions.Count - 1;
            return block.Instructions.Count >= 5
                   && block.Instructions[index].OpCode == OpCodes.Xor
                   && block.Instructions[index - 1].IsLdcI4()
                   && block.Instructions[index - 2].OpCode == OpCodes.Mul
                   && block.Instructions[index - 3].IsLdcI4()
                   && block.Instructions[index - 4].IsLdloc();
        }

        private static bool HasXorMulTypeBFingerprint(Block block)
        {
            int index = block.Instructions.Count - 1;
            return block.Instructions.Count >= 4
                   && block.Instructions[index].OpCode == OpCodes.Xor
                   && block.Instructions[index - 1].OpCode == OpCodes.Mul
                   && block.Instructions[index - 2].IsLdcI4()
                   && block.Instructions[index - 3].IsLdloc();
        }

        private void EmulateSwitchHeader()
        {
            _instructionEmulator.Emulate(_switchHeaderInstructions
                .Take(_switchHeaderInstructions.Count - 1)
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

            if (!_blocksInScope.Contains(targetBlock))
            {
                // This block is outside of our scope and should be left alone
                return;
            }

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
}