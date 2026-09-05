using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using PLATEAU.Dataset;
using PLATEAU.Geometries;
using PLATEAU.Native;

/// <summary>Extracts original CityGML labels and normalizes their declared coordinates with the PLATEAU SDK.</summary>
public static class EnvironmentCostCityGmlPlaceLabelExtractor
{
    public static EnvironmentCostPlaceLabels Extract(AnalysisRunConfig analysis, RuntimeCityPackageConfig config, out EnvironmentCostPlaceLabelReport report)
    {
        var labels = new List<EnvironmentCostPlaceLabel>();
        var errors = new List<string>();
        var epsgCodes = new HashSet<int>();
        var acquisitionSources = LoadAcquisitionSources(analysis, config, errors);
        var files = FindCityGmlFiles(analysis, config, errors).ToArray();
        var parsed = 0;
        using var reference = GeoReference.Create(new PlateauVector3d(0, 0, 0), 1f, CoordinateSystem.EUN, analysis.coordinateZoneId);
        foreach (var input in files)
        {
            try
            {
                var gml = GmlFile.Create(input.path);
                int sourceEpsg;
                try
                {
                    sourceEpsg = gml.Epsg;
                }
                finally
                {
                    // GmlFile exposes Dispose(), but does not implement IDisposable in the
                    // current PLATEAU SDK. Release its native handle explicitly for every file.
                    gml.Dispose();
                }
                epsgCodes.Add(sourceEpsg);
                ExtractFile(XDocument.Load(input.path), input, analysis, config.placeLabelCoordinateAxis, sourceEpsg, reference, labels);
                parsed++;
            }
            catch (Exception exception) { errors.Add($"{Path.GetFileName(input.path)}: {exception.Message}"); }
        }
        labels = MergeNearbySameNameLabels(labels);
        var reasons = new List<string>();
        if (files.Length == 0) reasons.Add("citygml-source-not-found");
        if (errors.Any(error => error.StartsWith("citygml-acquisition-manifest-missing", StringComparison.Ordinal))) reasons.Add("citygml-acquisition-manifest-missing");
        if (errors.Count > 0) reasons.Add("citygml-parse-errors");
        if (labels.Count == 0) reasons.Add("no-place-labels-extracted");
        report = new EnvironmentCostPlaceLabelReport
        {
            schemaVersion = "environment-cost-place-label-report-0.1", areaId = analysis.areaId, coordinateZoneId = analysis.coordinateZoneId,
            sourceFileCount = files.Length, parsedFileCount = parsed, labelCount = labels.Count, reasonCodes = reasons.ToArray(), parseErrors = errors.ToArray(),
            sourceEpsgCodes = epsgCodes.OrderBy(value => value).ToArray(), sourceCoordinateAxis = config.placeLabelCoordinateAxis,
            classifications = labels.GroupBy(label => label.classification, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new EnvironmentCostPlaceLabelClassification { name = group.Key, count = group.Count() }).ToArray(),
            examples = labels.Take(10).Select(label => label.text).ToArray(), sourceVersion = config.placeLabelSourceVersion,
            sourceAcquiredAtUtc = config.placeLabelSourceAcquiredAtUtc,
            sourceDatasetIds = (config.placeLabelDatasetIds == null || config.placeLabelDatasetIds.Length == 0 ? analysis.datasetRoots?.Keys?.ToArray() : config.placeLabelDatasetIds)?.OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
            acquisitionSources = acquisitionSources.ToArray()
        };
        return new EnvironmentCostPlaceLabels { schemaVersion = "environment-cost-place-labels-0.1", areaId = analysis.areaId, coordinateZoneId = analysis.coordinateZoneId, labels = labels.ToArray() };
    }

    private static IEnumerable<CityGmlInput> FindCityGmlFiles(AnalysisRunConfig analysis, RuntimeCityPackageConfig config, List<string> errors)
    {
        IEnumerable<string> roots = config.placeLabelDatasetIds == null || config.placeLabelDatasetIds.Length == 0 ? (analysis.datasetRoots?.Keys ?? Enumerable.Empty<string>()) : config.placeLabelDatasetIds;
        foreach (var id in roots.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (analysis.datasetRoots == null || !analysis.datasetRoots.ContainsKey(id)) continue;
            var root = analysis.DatasetRootFor(id);
            if (!Directory.Exists(root)) continue;
            string[] files;
            try { files = Directory.EnumerateFiles(root, "*.gml", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories)).OrderBy(path => path, StringComparer.Ordinal).ToArray(); }
            catch (Exception exception) { errors.Add($"{id}: {exception.Message}"); continue; }
            foreach (var file in files) yield return new CityGmlInput { path = file, datasetId = id, relativePath = RelativePath(root, file) };
        }
    }

    internal static void ExtractFile(XDocument document, CityGmlInput input, AnalysisRunConfig analysis, string coordinateAxis, int sourceEpsg, GeoReference reference, List<EnvironmentCostPlaceLabel> labels)
    {
        foreach (var member in document.Descendants().Where(element => element.Name.LocalName == "cityObjectMember"))
        {
            var feature = member.Elements().FirstOrDefault();
            if (feature == null) continue;
            var name = feature.Descendants().FirstOrDefault(element => element.Name.LocalName == "name")?.Value?.Trim();
            var position = feature.Descendants().FirstOrDefault(element => element.Name.LocalName == "pos")?.Value
                           ?? feature.Descendants().FirstOrDefault(element => element.Name.LocalName == "posList")?.Value;
            if (string.IsNullOrWhiteSpace(name) || !TryReadCoordinate(position, coordinateAxis, reference, sourceEpsg, out var coordinate)) continue;
            name = name.Normalize(NormalizationForm.FormKC);
            if (DistanceMeters(analysis.CenterLatitude, analysis.CenterLongitude, coordinate[1], coordinate[0]) > analysis.radiusMeters) continue;
            labels.Add(new EnvironmentCostPlaceLabel
            {
                id = $"{input.datasetId}:{input.relativePath}:{feature.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value ?? "unnamed"}",
                text = name, coordinate = coordinate, sourceFile = input.relativePath, sourceElement = feature.Name.LocalName,
                sourceKind = "citygml-gml-name", classification = feature.Name.LocalName, sourceEpsg = sourceEpsg, priority = PriorityFor(feature.Name.LocalName)
            });
        }
    }

    private static int PriorityFor(string classification) => classification switch
    {
        "CityObjectGroup" => 100,
        "LandUse" => 90,
        "GenericCityObject" => 80,
        _ => 60
    };

    private static List<EnvironmentCostPlaceLabelAcquisitionSource> LoadAcquisitionSources(AnalysisRunConfig analysis, RuntimeCityPackageConfig config, List<string> errors)
    {
        var relative = string.IsNullOrWhiteSpace(config.placeLabelAcquisitionManifestPath) ? $"data/plateau-citygml-manifests/{analysis.areaId}.json" : config.placeLabelAcquisitionManifestPath;
        var path = config.ResolvePath(relative);
        if (!File.Exists(path)) { errors.Add("citygml-acquisition-manifest-missing"); return new List<EnvironmentCostPlaceLabelAcquisitionSource>(); }
        try
        {
            var plan = JObject.Parse(File.ReadAllText(path));
            var sha = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(path);
            return (plan["datasets"] as JArray ?? new JArray()).OfType<JObject>().Select(dataset => new EnvironmentCostPlaceLabelAcquisitionSource
            {
                datasetId = (string)dataset["id"], provider = "PLATEAU", year = (int?)dataset["year"] ?? 0, url = (string)dataset["url"],
                acquiredAtUtc = (string)dataset["acquiredAtUtc"] ?? "unknown", acquisitionPlanPath = relative.Replace('\\', '/'), acquisitionPlanSha256 = sha
            }).ToList();
        }
        catch (Exception exception) { errors.Add($"citygml-acquisition-manifest-invalid: {exception.Message}"); return new List<EnvironmentCostPlaceLabelAcquisitionSource>(); }
    }

    internal sealed class CityGmlInput { public string path; public string datasetId; public string relativePath; }

    private static string RelativePath(string root, string path)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length).Replace('\\', '/') : Path.GetFileName(path);
    }

    internal static bool TryReadCoordinate(string text, string coordinateAxis, GeoReference reference, int sourceEpsg, out double[] coordinate)
    {
        coordinate = null;
        var values = (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var stride = values.Length >= 3 && values.Length % 3 == 0 ? 3 : 2;
        if (values.Length < stride) return false;
        var first = 0.0; var second = 0.0; var height = 0.0; var count = 0;
        for (var index = 0; index + stride <= values.Length; index += stride)
        {
            if (!double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFirst) || !double.TryParse(values[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSecond)) return false;
            var parsedHeight = 0.0;
            if (stride == 3 && !double.TryParse(values[index + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedHeight)) return false;
            if (double.IsNaN(parsedFirst) || double.IsInfinity(parsedFirst) || double.IsNaN(parsedSecond) || double.IsInfinity(parsedSecond) || (stride == 3 && (double.IsNaN(parsedHeight) || double.IsInfinity(parsedHeight)))) return false;
            first += parsedFirst; second += parsedSecond; if (stride == 3) height += parsedHeight; count++;
        }
        first /= count; second /= count; height /= count;
        GeoCoordinate geographic;
        if (coordinateAxis == "latitude-longitude")
        {
            // CityGML URF labels use latitude/longitude. Project/unproject through the same SDK GeoReference contract as Scene import.
            geographic = reference.Unproject(reference.Project(new GeoCoordinate(first, second, height)));
        }
        else if (coordinateAxis == "northing-easting-up")
        {
            geographic = reference.Unproject(reference.Convert(new PlateauVector3d(first, second, height), true, sourceEpsg));
        }
        else return false;
        coordinate = new[] { geographic.Longitude, geographic.Latitude };
        return true;
    }

    internal static List<EnvironmentCostPlaceLabel> MergeNearbySameNameLabels(IEnumerable<EnvironmentCostPlaceLabel> labels)
    {
        const double mergeDistanceMeters = 30.0;
        var merged = new List<EnvironmentCostPlaceLabel>();
        foreach (var label in labels.OrderBy(label => label.text, StringComparer.Ordinal).ThenByDescending(label => label.priority))
        {
            var existing = merged.FirstOrDefault(candidate => string.Equals(candidate.text, label.text, StringComparison.Ordinal) &&
                DistanceMeters(candidate.coordinate[1], candidate.coordinate[0], label.coordinate[1], label.coordinate[0]) <= mergeDistanceMeters);
            if (existing == null) merged.Add(label);
        }
        return merged;
    }

    private static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadiusMeters = 6371008.8;
        var latitudeDelta = (latitudeB - latitudeA) * Math.PI / 180.0;
        var longitudeDelta = (longitudeB - longitudeA) * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitudeDelta / 2.0);
        var sinLongitude = Math.Sin(longitudeDelta / 2.0);
        var a = sinLatitude * sinLatitude + Math.Cos(latitudeA * Math.PI / 180.0) * Math.Cos(latitudeB * Math.PI / 180.0) * sinLongitude * sinLongitude;
        return earthRadiusMeters * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
    }
}
