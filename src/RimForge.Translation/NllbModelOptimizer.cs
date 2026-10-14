using Google.Protobuf;
using RimForge.Translation.Onnx;

namespace RimForge.Translation;

/// <summary>
/// One-time rework of the downloaded decoder so it runs fast and small. The model keeps its 256 000-word
/// vocabulary table in 8 bits, but scores every next word by first unpacking the whole table into a fresh
/// 1 GB float table — at every generated word. Here the table is stored the other way round and multiplied
/// in 8 bits directly (like every other layer of the model already is), and the word lookup at the input
/// reads the same table, so no second copy is kept.
/// </summary>
public static class NllbModelOptimizer
{
    private const int Float = 1, UInt8 = 2;          // TensorProto data types
    private const int AttrInt = 2, AttrInts = 7;     // AttributeProto types

    /// <summary>Reads <paramref name="source"/> and writes the reworked decoder to <paramref name="target"/>.</summary>
    public static void Optimize(string source, string target)
    {
        ModelProto model;
        using (var input = File.OpenRead(source))
            model = ModelProto.Parser.ParseFrom(new CodedInputStream(input));
        var graph = model.Graph;

        var branches = graph.Node.Where(n => n.OpType == "If")
            .SelectMany(n => n.Attribute.Where(a => a.G is not null).Select(a => a.G)).ToList();
        var scopes = branches.Count > 0 ? branches : [graph];

        // The table: the input of the "Transpose" that feeds the scoring.
        var weightName = scopes.SelectMany(s => s.Node).First(n => n.OpType == "Transpose" && graph.Initializer.Any(t => t.Name == n.Input[0])).Input[0];
        var weight = graph.Initializer.Single(t => t.Name == weightName);
        if (weight.DataType != UInt8 || weight.Dims.Count != 2) throw new InvalidDataException("Unexpected vocabulary table.");
        var rows = (int)weight.Dims[0];
        var cols = (int)weight.Dims[1];

        var transposedName = "rimforge.vocabulary_t";
        var transposed = new TensorProto { Name = transposedName, DataType = UInt8, RawData = UnsafeByteOperations.UnsafeWrap(Transpose(weight.RawData.Span, rows, cols)) };
        transposed.Dims.Add(cols);
        transposed.Dims.Add(rows);
        graph.Initializer.Remove(weight);
        graph.Initializer.Add(transposed);

        var scoring = 0;
        foreach (var scope in scopes)
        {
            var prefix = $"rimforge_{scope.Name}_";
            var nodes = scope.Node;
            var transpose = nodes.Single(n => n.OpType == "Transpose" && n.Input[0] == weightName);
            var dequantize = nodes.Single(n => n.OpType == "DequantizeLinear" && n.Input[0] == transpose.Output[0]);
            var matMul = nodes.Single(n => n.OpType == "MatMul" && n.Input[1] == dequantize.Output[0]);
            var scale = dequantize.Input[1];
            var zeroPoint = dequantize.Input[2];

            // Scoring: quantize the hidden state, multiply in 8 bits, scale back to float.
            var at = nodes.IndexOf(matMul);
            nodes.RemoveAt(at);
            nodes.Insert(at, Node("DynamicQuantizeLinear", prefix + "q", [matMul.Input[0]], [prefix + "xq", prefix + "xs", prefix + "xz"]));
            nodes.Insert(at + 1, Node("MatMulInteger", prefix + "mm", [prefix + "xq", transposedName, prefix + "xz", zeroPoint], [prefix + "yi"]));
            nodes.Insert(at + 2, Node("Cast", prefix + "cast", [prefix + "yi"], [prefix + "yf"], Int("to", Float)));
            nodes.Insert(at + 3, Node("Mul", prefix + "s", [prefix + "xs", scale], [prefix + "scale"]));
            nodes.Insert(at + 4, Node("Mul", prefix + "y", [prefix + "yf", prefix + "scale"], [matMul.Output[0]]));
            nodes.Remove(dequantize);
            nodes.Remove(transpose);

            // Word lookup: columns of the turned table, then back to [batch, words, features].
            foreach (var gather in nodes.Where(n => n.OpType == "Gather" && n.Input[0] == weightName).ToList())
            {
                var i = nodes.IndexOf(gather);
                var output = gather.Output[0];
                nodes.RemoveAt(i);
                nodes.Insert(i, Node("Gather", prefix + "emb", [transposedName, gather.Input[1]], [prefix + "emb_t"], Int("axis", 1)));
                nodes.Insert(i + 1, Node("Transpose", prefix + "emb_back", [prefix + "emb_t"], [output], Ints("perm", 1, 2, 0)));
            }
            if (scope.Node.Concat(graph.Node).Any(n => n.Input.Contains(weightName))) throw new InvalidDataException("The vocabulary table has other uses.");
            scoring++;
        }
        if (scoring == 0) throw new InvalidDataException("Nothing to optimize.");

        var temp = target + ".tmp";
        using (var output = File.Create(temp))
            model.WriteTo(output);
        File.Move(temp, target, overwrite: true);
    }

    private static byte[] Transpose(ReadOnlySpan<byte> data, int rows, int cols)
    {
        var result = new byte[data.Length];
        const int Tile = 64;
        for (var r0 = 0; r0 < rows; r0 += Tile)
        for (var c0 = 0; c0 < cols; c0 += Tile)
        {
            var rEnd = Math.Min(rows, r0 + Tile);
            var cEnd = Math.Min(cols, c0 + Tile);
            for (var r = r0; r < rEnd; r++)
            for (var c = c0; c < cEnd; c++)
                result[(long)c * rows + r] = data[r * cols + c];
        }
        return result;
    }

    private static NodeProto Node(string op, string name, string[] inputs, string[] outputs, params AttributeProto[] attributes)
    {
        var node = new NodeProto { OpType = op, Name = name };
        node.Input.AddRange(inputs);
        node.Output.AddRange(outputs);
        node.Attribute.AddRange(attributes);
        return node;
    }

    private static AttributeProto Int(string name, long value) => new() { Name = name, Type = AttrInt, I = value };

    private static AttributeProto Ints(string name, params long[] values)
    {
        var attribute = new AttributeProto { Name = name, Type = AttrInts };
        attribute.Ints.AddRange(values);
        return attribute;
    }
}
