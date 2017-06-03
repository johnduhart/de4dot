using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    /// <summary>
    /// Represents a discreete section of code within a method
    /// </summary>
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

        public IList<BlockScope> Children => _children;

        public IList<Block> Blocks { get; }

        public void AddChild(BlockScope childScope)
        {
            childScope.Parent = this;
            _children.Add(childScope);
        }
    }
}