using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
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

            /*var initialInstructions2 = initialInstructions
                .Select(l => l.First(i => i.IsLdcI4()))
                .Select(i => new List<Instr>(1){i})
                .ToList();*/

            var tracer = new SwitchTracer(_blocks, switchHeader.Select(i => i.Instruction).ToList(), switchBlock);

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
            BlockScope rootScope = GetBlockScope(_blocks.MethodBlocks);
            string graphTest = GenerateGraphviz(rootScope);
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

            private readonly IList<Instruction> _switchHeaderInstructions;
            private readonly InstructionEmulator _instructionEmulator;
            private readonly Local _methodLocal;

            public SwitchTracer(Blocks blocks, IList<Instruction> switchHeaderInstructions, Block switchBlock)
            {
                _switchHeaderInstructions = switchHeaderInstructions;
                SwitchBlock = switchBlock;

                _instructionEmulator = new InstructionEmulator(blocks.Method);
                _methodLocal = blocks.Method.Body.Variables.Last();

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

                    if (_processedBlocks.Contains(branchABlock) && _processedBlocks.Contains(branchBBlock))
                    {
                        return;
                    }

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

        private static string GenerateGraphviz(BlockScope rootScope)
        {
            var builder = new StringBuilder();
            builder.AppendLine("digraph blockscopes {");
            builder.AppendLine("node [shape=box]");

            GenerateGraphviz_Subgraph(rootScope, builder);
            GenerateGraphviz_Transitions(rootScope, builder);

            builder.AppendLine("}");

            return builder.ToString();
        }

        private static void GenerateGraphviz_Subgraph(BlockScope currentScope, StringBuilder builder)
        {
            var instructionBuilder = new StringBuilder();

            foreach (var scope in currentScope.Children)
            {
                string scopeId = "scope_" + scope.GetHashCode();
                builder.AppendLine($"subgraph {scopeId} {{");

                if (currentScope.Children.Count > 0)
                {
                    GenerateGraphviz_Subgraph(scope, builder);
                }

                builder.AppendLine();

                foreach (Block block in scope.Blocks)
                {
                    string blockId = "block_" + block.GetHashCode();

                    foreach (Instr instr in block.Instructions)
                    {
                        string instructionString = instr.ToString();
                        if (instructionString.Length > 30)
                            instructionString = instructionString.Substring(0, 30) + "...";

                        EscapeString(instructionBuilder, instructionString);
                        instructionBuilder.Append("\\l");
                    }

                    builder.AppendLine($"{blockId} [label=\"{instructionBuilder}\"]");
                    instructionBuilder.Clear();
                }

                /*if (scope.Blocks.Count > 0)
                {
                    IEnumerable<string> nodes = scope.Blocks.Select(b => "block_" + b.GetHashCode());
                    builder.AppendLine($"{string.Join(";", nodes)};");
                }*/

                builder.AppendLine("}");
            }
        }

        static void EscapeString(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }

            foreach (var c in s)
            {
                if ((int)c < 0x20)
                {
                    switch (c)
                    {
                        case '\a': sb.Append(@"\a"); break;
                        case '\b': sb.Append(@"\b"); break;
                        case '\f': sb.Append(@"\f"); break;
                        case '\n': sb.Append(@"\n"); break;
                        case '\r': sb.Append(@"\r"); break;
                        case '\t': sb.Append(@"\t"); break;
                        case '\v': sb.Append(@"\v"); break;
                        default:
                            sb.Append(string.Format(@"\u{0:X4}", (int)c));
                            break;
                    }
                }
                else if (c == '\\' || c == '"')
                {
                    sb.Append('\\');
                    sb.Append(c);
                }
                else if (c > 8200)
                {
                    sb.Append(string.Format(@"\u{0:X4}", (int)c));
                }
                else
                    sb.Append(c);
            }
        }

        private static void GenerateGraphviz_Transitions(BlockScope currentScope, StringBuilder builder)
        {
            foreach (BlockScope scopeChild in currentScope.Children)
            {
                GenerateGraphviz_Transitions(scopeChild, builder);
            }

            foreach (Block block in currentScope.Blocks)
            {
                if (block.FallThrough != null)
                {
                    builder.AppendLine($"block_{block.GetHashCode()} -> block_{block.FallThrough.GetHashCode()};");
                }

                if (block.Targets != null)
                    foreach (Block target in block.Targets)
                    {
                        string lineColor = "orangered";
                        if (block.LastInstr.OpCode == OpCodes.Switch)
                            lineColor = "blueviolet";

                        builder.AppendLine($"block_{block.GetHashCode()} -> block_{target.GetHashCode()} [color={lineColor}];");
                    }
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
    }
}