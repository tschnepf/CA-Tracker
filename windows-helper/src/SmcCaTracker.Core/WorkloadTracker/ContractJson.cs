using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>JSON settings for Workload Tracker requests and responses. The DTOs name their own properties.</summary>
public static class ContractJson
{
    /// <summary>The JSON names of CA Tracker's own facts about an item (<see cref="ItemFacts"/>).</summary>
    private static readonly HashSet<string> ItemFactFields = new(StringComparer.Ordinal) { "discipline", "disciplineSource", "folderStatus", "folderPath" };

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = Resolver()
    };

    /// <summary>For files people read, such as exported fixtures.</summary>
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = Resolver()
    };

    private static DefaultJsonTypeInfoResolver Resolver() => new() { Modifiers = { LeaveOutItemFactsNotKnown } };

    /// <summary>
    /// An item's discipline, discipline source, folder status and folder path go as a set. The tracker
    /// keeps its stored values for an item that leaves them out, and clears a value sent as null. An item
    /// this PC compared always has a folder status, so it sends all four (nulls included); an item it
    /// didn't compare has none, and all four are left out rather than sent as nulls that would clear what
    /// another PC sent.
    /// </summary>
    private static void LeaveOutItemFactsNotKnown(JsonTypeInfo info)
    {
        if (info.Type != typeof(CaItemRequest))
        {
            return;
        }

        foreach (var property in info.Properties.Where(property => ItemFactFields.Contains(property.Name)))
        {
            property.ShouldSerialize = (item, _) => ((CaItemRequest)item).FolderStatus is not null;
        }
    }
}
