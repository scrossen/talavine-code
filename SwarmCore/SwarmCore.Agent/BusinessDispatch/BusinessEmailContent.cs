using System.Text;

namespace SwarmCore.Agent.BusinessDispatch;

public enum BusinessEmailNodeKind { Text, Inline, Block, Break, Link }
public sealed record BusinessEmailNode(BusinessEmailNodeKind Kind, string Text = "", string? Destination = null,
    IReadOnlyList<BusinessEmailNode>? Children = null);

/// <summary>Render an inert structural document to the exact plain message reviewed and sent. Hosts parse markup.</summary>
public static class BusinessEmailContent
{
    public static string Render(IReadOnlyList<BusinessEmailNode> nodes)
    {
        var output = new StringBuilder();
        foreach (var node in nodes) Append(output, node);
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
    private static void Append(StringBuilder output, BusinessEmailNode node)
    {
        if (node.Kind == BusinessEmailNodeKind.Text) { output.Append(node.Text); return; }
        if (node.Kind == BusinessEmailNodeKind.Break) { output.Append('\n'); return; }
        if (node.Kind == BusinessEmailNodeKind.Block && output.Length > 0 && output[^1] != '\n') output.Append('\n');
        var start = output.Length;
        foreach (var child in node.Children ?? []) Append(output, child);
        if (node.Kind == BusinessEmailNodeKind.Link && !string.IsNullOrWhiteSpace(node.Destination))
        {
            if (!Uri.TryCreate(node.Destination, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto")
                || uri.UserInfo.Length != 0) throw new ArgumentException("An exact absolute public HTTP(S) or email link is required before plain-message approval.");
            if (!output.ToString(start, output.Length - start).Trim().Equals(node.Destination, StringComparison.Ordinal))
                output.Append(" (").Append(node.Destination).Append(')');
        }
        if (node.Kind == BusinessEmailNodeKind.Block && output.Length > 0 && output[^1] != '\n') output.Append('\n');
    }
}
