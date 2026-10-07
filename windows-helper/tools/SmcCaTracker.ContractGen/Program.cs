using SmcCaTracker.ContractGen;

const string Usage = """
    Usage, from anywhere in the repository:
      dotnet run --project windows-helper/tools/SmcCaTracker.ContractGen -- pin <workload-tracker>/backend/openapi.json <commit>
          Pins the CA Tracker part of the tracker's schema to contract/openapi.json and regenerates the DTOs.
      dotnet run --project windows-helper/tools/SmcCaTracker.ContractGen -- generate
          Regenerates the DTOs from contract/openapi.json.
      dotnet run --project windows-helper/tools/SmcCaTracker.ContractGen -- check
          Fails when the committed DTOs differ from what contract/openapi.json generates.
    """;

var root = ContractFiles.FindWindowsHelper(Directory.GetCurrentDirectory())
    ?? ContractFiles.FindWindowsHelper(AppContext.BaseDirectory);
if (root is null)
{
    Console.Error.WriteLine("Could not find windows-helper (the folder with SmcCaTracker.slnx).");
    return 2;
}

var pinnedFile = ContractFiles.PinnedSchema(root);
var dtoFile = ContractFiles.GeneratedDtos(root);

switch (args)
{
    case ["pin", var trackerSchema, var commit]:
        File.WriteAllText(pinnedFile, ContractGenerator.Pin(File.ReadAllText(trackerSchema), commit));
        File.WriteAllText(dtoFile, ContractGenerator.GenerateCSharp(File.ReadAllText(pinnedFile)));
        Console.WriteLine($"Pinned {pinnedFile} from commit {commit} and regenerated {dtoFile}.");
        return 0;

    case ["generate"]:
        File.WriteAllText(dtoFile, ContractGenerator.GenerateCSharp(File.ReadAllText(pinnedFile)));
        Console.WriteLine($"Regenerated {dtoFile}.");
        return 0;

    case ["check"]:
        var expected = ContractGenerator.GenerateCSharp(File.ReadAllText(pinnedFile));
        var actual = File.Exists(dtoFile) ? File.ReadAllText(dtoFile).ReplaceLineEndings("\n") : "";
        if (expected != actual)
        {
            Console.Error.WriteLine($"{dtoFile} does not match contract/openapi.json. Run the generate command and commit the result.");
            return 1;
        }

        Console.WriteLine("The generated DTOs match the pinned Workload Tracker schema.");
        return 0;

    default:
        Console.Error.WriteLine(Usage);
        return 2;
}
