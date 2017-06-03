using System.Collections.Generic;
using System.Diagnostics;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal static class BlockScopeBuilder
    {
        public static BlockScope ToBlockScope(this Blocks blocks) => Parse(blocks);

        public static BlockScope Parse(Blocks blocks)
        {
            return GetBlockScope(blocks.MethodBlocks);
        }

        private static BlockScope GetBlockScope(ScopeBlock currentScopeBlock)
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
    }
}