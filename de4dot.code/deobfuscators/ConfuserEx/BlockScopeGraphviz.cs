using System.Text;
using de4dot.blocks;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal static class BlockScopeGraphviz
    {
        public static string ToGraph(this BlockScope scope) => Graph(scope);

        internal static string Graph(BlockScope rootScope)
        {
            var builder = new StringBuilder();
            builder.AppendLine("digraph blockscopes {");
            builder.AppendLine("node [shape=box]");

            Subgraph(rootScope, builder);
            Transitions(rootScope, builder);

            builder.AppendLine("}");

            return builder.ToString();
        }

        private static void Subgraph(BlockScope currentScope, StringBuilder builder)
        {
            var instructionBuilder = new StringBuilder();

            foreach (var scope in currentScope.Children)
            {
                string scopeId = "cluster_" + scope.GetHashCode();
                builder.AppendLine($"subgraph {scopeId} {{");
                builder.AppendLine("color=blue;");

                if (currentScope.Children.Count > 0)
                {
                    Subgraph(scope, builder);
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

                    builder.AppendLine($"{blockId} [label=\"{instructionBuilder}\"];");
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

        private static void Transitions(BlockScope currentScope, StringBuilder builder)
        {
            foreach (BlockScope scopeChild in currentScope.Children)
            {
                Transitions(scopeChild, builder);
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
    }
}