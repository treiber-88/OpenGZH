using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace OpenGZH.Core.Rules;

/// <summary>Reads and writes <see cref="RuleNode"/> trees as YAML.</summary>
public static class RuleYaml
{
    public static RuleNode Parse(string text, string sourceName)
    {
        var parser = new Parser(new StringReader(text));
        parser.Consume<StreamStart>();

        if (parser.Accept<StreamEnd>(out _))
            return RuleNode.Mapping();

        parser.Consume<DocumentStart>();
        var root = ReadNode(parser, sourceName);
        parser.Consume<DocumentEnd>();
        return root;
    }

    static RuleNode ReadNode(IParser parser, string sourceName)
    {
        string Location(ParsingEvent e) => $"{sourceName}:{e.Start.Line}";

        if (parser.TryConsume<Scalar>(out var scalar))
        {
            bool isNull = scalar.Style == ScalarStyle.Plain && scalar.Value is "~" or "null" or "";
            var node = RuleNode.Scalar(isNull ? null : scalar.Value);
            node.Source = Location(scalar);
            return node;
        }

        if (parser.TryConsume<MappingStart>(out var mapStart))
        {
            var mapping = RuleNode.Mapping();
            mapping.Source = Location(mapStart);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var keyEvent = parser.Consume<Scalar>();
                var value = ReadNode(parser, sourceName);
                mapping.Set(keyEvent.Value, value);
            }
            return mapping;
        }

        if (parser.TryConsume<SequenceStart>(out var seqStart))
        {
            var sequence = RuleNode.Sequence();
            sequence.Source = Location(seqStart);
            while (!parser.TryConsume<SequenceEnd>(out _))
                sequence.Add(ReadNode(parser, sourceName));
            return sequence;
        }

        if (parser.TryConsume<AnchorAlias>(out var alias))
            throw new YamlException(alias.Start, alias.End, "YAML aliases are not supported in rules files.");

        throw new YamlException($"Unexpected YAML content in {sourceName}.");
    }

    // ---- Writing ----

    /// <summary>Writes a node as YAML. <paramref name="header"/> lines are emitted as comments at the top.</summary>
    public static string Write(RuleNode root, string? header = null)
    {
        var writer = new StringWriter();
        if (header != null)
            foreach (string line in header.Split('\n'))
                writer.WriteLine(line.Length > 0 ? "# " + line.TrimEnd('\r') : "#");

        var emitter = new Emitter(writer, bestIndent: 2, bestWidth: int.MaxValue);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, isImplicit: true));
        WriteNode(emitter, root, topLevel: true);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        return writer.ToString();
    }

    static void WriteNode(IEmitter emitter, RuleNode node, bool topLevel = false)
    {
        switch (node.Kind)
        {
            case RuleNode.NodeKind.Scalar:
                emitter.Emit(ScalarEvent(node.Value));
                break;

            case RuleNode.NodeKind.Mapping:
                emitter.Emit(new MappingStart(null, null, true, MappingStyle.Block));
                foreach (var (key, value) in node.Entries)
                {
                    emitter.Emit(ScalarEvent(key));
                    WriteNode(emitter, value);
                }
                emitter.Emit(new MappingEnd());
                break;

            case RuleNode.NodeKind.Sequence:
                // Short lists of plain words read best on one line: [A, B, C].
                bool flow = node.Items.All(i => i.IsScalar) && node.Items.Sum(i => (i.Value ?? "").Length) < 80;
                emitter.Emit(new SequenceStart(null, null, true, flow ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in node.Items)
                    WriteNode(emitter, item);
                emitter.Emit(new SequenceEnd());
                break;
        }
    }

    static Scalar ScalarEvent(string? value)
    {
        if (value == null)
            return new Scalar(null, null, "~", ScalarStyle.Plain, true, false);
        // Let the emitter choose plain style when it is safe, otherwise it quotes automatically.
        return new Scalar(null, null, value, ScalarStyle.Any, true, true);
    }
}
