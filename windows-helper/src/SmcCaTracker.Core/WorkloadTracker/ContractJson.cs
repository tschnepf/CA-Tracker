using System.Text.Encodings.Web;
using System.Text.Json;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>JSON settings for Workload Tracker requests and responses. The DTOs name their own properties.</summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>For files people read, such as exported fixtures.</summary>
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
