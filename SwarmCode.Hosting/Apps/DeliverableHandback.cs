using System.Security.Cryptography;
using System.Text;

namespace SwarmCode.Hosting.Apps;

/// <summary>
/// Hand-back of a finished deliverable into the person's connected apps. The id is name-based, so a retried call
/// carries the same <c>operation_id</c> and the app deduplicates instead of creating a second task or note.
/// </summary>
public static class DeliverableHandback
{
    private static readonly Guid Namespace = new("5b0f3c52-8f4e-4d0a-9a53-2c1d7e6b94a1");

    /// <summary>A stable id for one effect of one deliverable in one app (name-based, SHA-256 truncated to 128 bits, version 5 layout).</summary>
    public static Guid OperationId(string deliverableId, string app, string effect)
    {
        var name = string.Join('\n', Normalize(deliverableId, nameof(deliverableId)), Normalize(app, nameof(app)), Normalize(effect, nameof(effect)));
        var ns = Namespace.ToByteArray();
        SwapGuidByteOrder(ns);
        var data = Encoding.UTF8.GetBytes(name);
        var buffer = new byte[ns.Length + data.Length];
        ns.CopyTo(buffer, 0);
        data.CopyTo(buffer, ns.Length);
        var hash = SHA256.HashData(buffer);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        SwapGuidByteOrder(bytes);
        return new Guid(bytes);
    }

    private static string Normalize(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", parameter);
        return value.Trim().ToLowerInvariant();
    }

    // Guid's byte layout is little-endian for the first three fields; RFC 4122 hashing uses network order.
    private static void SwapGuidByteOrder(byte[] b)
    {
        (b[0], b[3]) = (b[3], b[0]);
        (b[1], b[2]) = (b[2], b[1]);
        (b[4], b[5]) = (b[5], b[4]);
        (b[6], b[7]) = (b[7], b[6]);
    }

    public const string Guidance =
        "Summarize a finished deliverable (a file, document, change or report the person asked for): what changed and where "
        + "(paths, PR link). Hand it back into the person's apps only when they asked for it (for example \"send it to Talavine\", "
        + "\"hand it to the CRM\", \"track this in Talavine\"), and only to apps that apps.list shows connected; never unprompted. "
        + "When asked, call the matching tool once and pass operation_id from apps.operation_id (deliverable, app, effect): in "
        + "Talavine, tasks.create titled \"Review: <deliverable>\"; and only when the person named a client or case, a CRM note: "
        + "find the exact tool ids with apps.find_tools (for example assistant.preview_crm_mutation, then assistant.apply_crm_mutation "
        + "with the plan it returns). Never call a tool that prepares "
        + "or performs a send (email, message, invoice, payment) from a hand-back. If a call's result is uncertain, retry with the "
        + "same operation id, never a new one, so nothing is duplicated.\n"
        + "When asked to automate a business process: express it as a schedule (schedules.create) plus prepare-only connected-app "
        + "calls. State the inferred plan before saving it. Use user.ask once, for what cannot be inferred; never ask for Review "
        + "versus Autonomous, sender accounts or recipients, which are set in the app.";
}
