using System.Collections;
using System.Reflection;
using TransitLab.Services;
using TransitLab.ViewModels;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static FitsHeaderService.FitsHeader Header(params (string Key, string Value)[] cards) =>
    new(cards.ToDictionary(card => card.Key, card => card.Value));

Check(PsfService.EstimateSaturation(Header(("SATURATE", "12000"))) == 12000, "Read detector saturation from SATURATE");
Check(PsfService.EstimateSaturation(Header(("TELESCOP", " Cecilia "), ("BITPIX", "16"))) == 4096, "Recognize Cecilia's 12-bit detector");
Check(PsfService.EstimateSaturation(Header(("TELESCOP", "cecilia"), ("SATURATE", "65535"))) == 4096, "Match EXOTIC's Cecilia convention");
Check(PsfService.EstimateSaturation(Header(("TELESCOP", "Cecilia"), ("EXOBIN", "2x2"), ("SATURATE", "16384"))) == 16384, "Preserve EXOTIC's summed-pixel saturation");
Check(PsfService.EstimateSaturation(Header(("BITPIX", "16"))) == 32767, "Keep storage estimate for an unknown detector");
Check(PsfService.GetGain(Header(("TELESCOP", "Cecilia"))) == 53.6, "Use EXOTIC's Cecilia gain default");
Check(PsfService.GetGain(Header(("TELESCOP", "Cecilia"), ("EGAIN", "60"))) == 60, "Preserve a valid header gain above the old arbitrary ceiling");
var image = new float[80, 80];
for (int y = 0; y < 80; y++)
    for (int x = 0; x < 80; x++)
        image[y, x] = (float)(10 + 3000 * Math.Exp(-((x - 40) * (x - 40) + (y - 40) * (y - 40)) / 8.0));
image[40, 40] = 4095;
Check(PsfService.Measure(image, 40, 40, 4096, 1).Saturated, "Reject clipped 4095 ADU pixels");
image[40, 40] = 3600;
Check(!PsfService.Measure(image, 40, 40, 4096, 1).Saturated, "Allow pixels below EXOTIC's existing 90% threshold");

// Exercise the actual selection method with catalogue/network work omitted.
var candidateType = typeof(GaiaCompService).GetNestedType("GaiaCandidate", BindingFlags.NonPublic)!;
var listType = typeof(List<>).MakeGenericType(candidateType);
IList NewList(params object[] values)
{
    var list = (IList)Activator.CreateInstance(listType)!;
    foreach (var value in values) list.Add(value);
    return list;
}
object Candidate(int x, int y, string source = "Gaia")
{
    var candidate = Activator.CreateInstance(candidateType, nonPublic: true)!;
    candidateType.GetProperty("XPixel")!.SetValue(candidate, x);
    candidateType.GetProperty("YPixel")!.SetValue(candidate, y);
    candidateType.GetProperty("Source")!.SetValue(candidate, source);
    return candidate;
}
var replace = typeof(GaiaCompService).GetMethod("ReplaceRejectedComparisons", BindingFlags.NonPublic | BindingFlags.Static)!;
var rejected = Candidate(100, 100, "VSP");
var survivor = Candidate(300, 300);
var gaia = Candidate(200, 200);
var vsp = Candidate(101, 101, "VSP");
var selected = NewList(rejected, survivor);
var pool = NewList(gaia, vsp, rejected);
replace.Invoke(null, [selected, pool, NewList(rejected), true]);
Check(selected.Contains(vsp) && !selected.Contains(rejected) && !pool.Contains(rejected), "Prefer VSP, remove rejects permanently, and free rejected coordinates");
replace.Invoke(null, [selected, pool, NewList(vsp), true]);
Check(selected.Contains(gaia) && !selected.Contains(vsp), "Test and replace the next failed candidate");
replace.Invoke(null, [selected, pool, NewList(gaia), true]);
Check(selected.Count == 1 && selected.Contains(survivor) && pool.Count == 0, "Exhaustion must not resurrect any reject");
var lastCandidate = Candidate(500, 500);
pool.Add(lastCandidate);
replace.Invoke(null, [selected, pool, NewList(survivor), false]);
Check(selected.Count == 1 && selected.Contains(lastCandidate), "Allow the last candidate in the replacement pool");
if (args.Length > 0)
{
    var header = FitsHeaderService.Read(args[0]);
    var pixels = PsfService.ReadFitsPixels(args[0])!;
    double saturation = PsfService.EstimateSaturation(header);
    double gain = PsfService.GetGain(header);
    Check(saturation == 4096 && gain == 53.6, "Recognize the supplied Cecilia science frame");
    Check(PsfService.Measure(pixels, 391, 345, saturation, gain).Saturated, "Reject the dataset's clipped comparison 1");
    Check(!PsfService.Measure(pixels, 431, 346, saturation, gain).Saturated, "Keep the dataset's unsaturated comparison 3");
    Console.WriteLine($"HAT-P-32 frame verified: detector saturation={saturation}, gain={gain}, saturated comp 1 rejected; unsaturated comp 3 permitted.");
}
var resultDir = Path.Combine(Path.GetTempPath(), "codex-ensemble-results-" + Guid.NewGuid());
Directory.CreateDirectory(resultDir);
File.WriteAllText(Path.Combine(resultDir, "ComparisonEnsemble.json"),
    """{"partial":true,"warning":"Partial comparison ensemble: using 2 of 3; omitted Comp 1 (overexposed in every frame)."}""");
var results = new ResultsViewModel();
results.ScanOutputFiles(resultDir);
Check(results.HasEnsembleWarning && results.EnsembleWarning.Contains("omitted Comp 1"), "Display the saved omission in Results");
results.ClearOutputImages();
Check(!results.HasEnsembleWarning, "Clear the old run's warning when starting a new run");
Console.WriteLine("Detector saturation and comparison replacement regressions passed.");


// Main-branch compatibility: exercise the actual initialization builder without
// running the desktop constructor, which starts unrelated app services.
var vmType = typeof(MainWindowViewModel);
var mainVm = (MainWindowViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(vmType);
const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
vmType.GetField("<Observation>k__BackingField", fields)!.SetValue(mainVm, new ObservationViewModel());
vmType.GetField("<EquipmentTarget>k__BackingField", fields)!.SetValue(mainVm, new EquipmentTargetViewModel());
var config = new AppConfig { UseEnsemblePhotometry = true, UseExactlyTheCompsProvided = true, AllowPartialComparisonEnsemble = true };
vmType.GetField("_cfg", fields)!.SetValue(mainVm, config);
var versionField = vmType.GetField("_detectedExoticVersion", fields)!;
var buildInits = vmType.GetMethod("BuildInits", fields)!;
foreach (var version in new string?[] { "4.3.1", null, "4.3.2.dev199+g4ec846c61.d20261104" })
{
    versionField.SetValue(mainVm, version);
    var init = (System.Text.Json.Nodes.JsonObject)buildInits.Invoke(mainVm, new object?[] { null })!;
    var optional = init["optional_info"]!.AsObject();
    bool prerelease = version?.StartsWith("4.3.2.dev") == true;
    foreach (var key in new[] { "use_ensemble_photometry_rather_than_single_comp", "use_exactly_the_comps_provided", "allow_partial_comparison_ensemble" })
        Check(optional.ContainsKey(key) == prerelease, $"Version {version ?? "unknown"}: gate {key} correctly");
    if (prerelease) Check(optional["allow_partial_comparison_ensemble"]!.GetValue<bool>(), "Send partial ensemble opt-in to updated prerelease");
    Console.WriteLine($"Main checkout initialization verified for EXOTIC {version ?? "unknown version"}: prerelease ensemble options {(prerelease ? "included" : "omitted")}.");
}
config.AllowPartialComparisonEnsemble = false;
var strictInit = (System.Text.Json.Nodes.JsonObject)buildInits.Invoke(mainVm, new object?[] { null })!;
Check(!strictInit["optional_info"]!["allow_partial_comparison_ensemble"]!.GetValue<bool>(), "Preserve strict ensemble opt-out");
Console.WriteLine("Stable/pre-release compatibility checks passed.");
