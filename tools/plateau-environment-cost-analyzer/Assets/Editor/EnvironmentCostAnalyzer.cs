using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PLATEAU.CityImport.AreaSelector;
using PLATEAU.CityImport.Config;
using PLATEAU.CityImport.Config.PackageImportConfigs;
using PLATEAU.CityImport.Import;
using PLATEAU.Dataset;
using PLATEAU.Geometries;
using PLATEAU.Native;
using PLATEAU.PolygonMesh;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class EnvironmentCostAnalyzer
{
    private const int BuildingLayer = 8;
    private const int RoadLayer = 9;
    private static AnalysisRunConfig runConfig;
    private static double CenterLatitude => runConfig.CenterLatitude;
    private static double CenterLongitude => runConfig.CenterLongitude;
    private static double RadiusMeters => runConfig.radiusMeters;
    private static int CoordinateZoneId => runConfig.coordinateZoneId;
    private static double SampleSpacingMeters => runConfig.sampleSpacingMeters;
    private static double WalkingSpeedMetersPerSecond => runConfig.walkingSpeedMetersPerSecond;
    private static DateTime AnalysisDate => runConfig.AnalysisDate;
    private static int[] AnalysisHours => runConfig.hours;

    public static async void Run()
    {
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            runConfig = AnalysisRunConfig.LoadForCurrentProcess();
            var coveragePath = runConfig.ResolvePath(runConfig.coverageOutputPath);
            var osmPath = runConfig.ResolvePath(runConfig.osmInputPath);
            var outputPath = runConfig.ResolvePath(runConfig.environmentCostOutputPath);
            var summaryPath = runConfig.ResolvePath(runConfig.summaryOutputPath);

            var coverage = JsonConvert.DeserializeObject<CoverageReport>(File.ReadAllText(coveragePath))
                ?? throw new InvalidOperationException("Coverage report could not be parsed.");
            if (!File.Exists(osmPath)) throw new FileNotFoundException("OSM input was not found.", osmPath);

            using var centerGeoReference = GeoReference.Create(new PlateauVector3d(0.0, 0.0, 0.0), 1.0f,
                CoordinateSystem.EUN, CoordinateZoneId);
            var referencePoint = centerGeoReference.Project(new GeoCoordinate(CenterLatitude, CenterLongitude, 0.0));
            using var localGeoReference = GeoReference.Create(referencePoint, 1.0f, CoordinateSystem.EUN, CoordinateZoneId);

            var importReports = new List<DatasetImportReport>();
            foreach (var dataset in coverage.datasets)
            {
                var thirdMeshCodes = dataset.gridCodes.Where(code => code.Length == 8).Distinct().ToArray();
                if (thirdMeshCodes.Length == 0) continue;
                var localDatasetRoot = FindLocalDatasetRoot(dataset.id);
                importReports.Add(await ImportDataset(dataset.id, dataset.title, localDatasetRoot, thirdMeshCodes, referencePoint));
            }

            var layerCounts = AssignColliderLayers();
            Physics.SyncTransforms();
            Debug.Log($"ENVIRONMENT_COST_IMPORT_SUMMARY area={runConfig.areaId} buildingColliders={layerCounts.building} roadColliders={layerCounts.road} otherColliders={layerCounts.other}");
            if (layerCounts.building == 0 || layerCounts.road == 0)
            {
                throw new InvalidOperationException("Building or road colliders were not imported.");
            }

            var analysisStopwatch = Stopwatch.StartNew();
            var edges = AnalyzeOsmEdges(osmPath, localGeoReference, out var osmWayCount, out var sourceSegmentCount,
                out var sampleCount, out var validSampleCount, out var noGroundSampleCount);
            analysisStopwatch.Stop();

            var process = Process.GetCurrentProcess();
            var output = new AnalysisOutput
            {
                schemaVersion = "environment-cost-0.1",
                areaId = runConfig.areaId,
                generatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                center = new[] { CenterLongitude, CenterLatitude },
                radiusMeters = RadiusMeters,
                coordinateZoneId = CoordinateZoneId,
                source = new SourceMetadata
                {
                    plateauDatasetIds = importReports.Select(report => report.datasetId).ToArray(),
                    plateauSdkVersion = "4.3.0",
                    unityVersion = Application.unityVersion,
                    osmSource = "OpenStreetMap via Overpass API",
                    osmDownloadedAt = File.GetLastWriteTimeUtc(osmPath).ToString("O", CultureInfo.InvariantCulture)
                },
                settings = new AnalysisSettings
                {
                    date = AnalysisDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    timezone = runConfig.timezone,
                    hours = AnalysisHours,
                    sampleSpacingMeters = SampleSpacingMeters,
                    pedestrianHeightMeters = runConfig.pedestrianHeightMeters,
                    walkingSpeedMetersPerSecond = WalkingSpeedMetersPerSecond,
                    obstaclePackages = new[] { "bldg" },
                    groundPackages = new[] { "tran" }
                },
                edges = edges
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException());
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(output, Formatting.None));

            totalStopwatch.Stop();
            var summary = new AnalysisSummary
            {
                status = "completed",
                generatedAt = output.generatedAt,
                areaId = output.areaId,
                center = output.center,
                radiusMeters = RadiusMeters,
                datasets = importReports,
                uniqueThirdMeshes = coverage.datasets.SelectMany(item => item.gridCodes)
                    .Where(code => code.Length == 8).Distinct().Count(),
                buildingColliderCount = layerCounts.building,
                roadColliderCount = layerCounts.road,
                osmWayCount = osmWayCount,
                sourceSegmentCount = sourceSegmentCount,
                analyzedEdgeCount = edges.Count,
                sampleCount = sampleCount,
                validSampleCount = validSampleCount,
                noGroundSampleCount = noGroundSampleCount,
                analysisSeconds = analysisStopwatch.Elapsed.TotalSeconds,
                totalSeconds = totalStopwatch.Elapsed.TotalSeconds,
                peakWorkingSetBytes = process.PeakWorkingSet64,
                outputBytes = new FileInfo(outputPath).Length,
                outputPath = outputPath
            };
            File.WriteAllText(summaryPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
            Debug.Log($"ENVIRONMENT_COST_ANALYSIS_COMPLETE area={runConfig.areaId} edges={edges.Count} samples={sampleCount} valid={validSampleCount} noGround={noGroundSampleCount} totalSeconds={summary.totalSeconds:F1} peakBytes={summary.peakWorkingSetBytes} outputBytes={summary.outputBytes}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
            return;
        }

        EditorApplication.Exit(0);
    }

    private static async Task<DatasetImportReport> ImportDataset(string datasetId, string title,
        string localDatasetRoot, string[] gridCodes, PlateauVector3d referencePoint)
    {
        var stopwatch = Stopwatch.StartNew();
        var collidersBefore = UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length;
        Debug.Log($"ENVIRONMENT_COST_IMPORT_START area={runConfig.areaId} dataset={datasetId} title={title} grids={gridCodes.Length}");

        using var gridCodeList = GridCodeList.CreateFromGridCodesStr(gridCodes);
        Debug.Log($"ENVIRONMENT_COST_LOCAL_SOURCE dataset={datasetId} path={localDatasetRoot}");
        var sourceConfig = new DatasetSourceConfigLocal(localDatasetRoot);
        var areaResult = new AreaSelectResult(new ConfigBeforeAreaSelect(sourceConfig, CoordinateZoneId), gridCodeList,
            AreaSelectResult.ResultReason.Confirm);
        var importConfig = CityImportConfig.CreateWithAreaSelectResult(areaResult);
        importConfig.ReferencePoint = referencePoint;

        foreach (var packagePair in importConfig.PackageImportConfigDict.ForEachPackagePair)
        {
            var package = packagePair.Key;
            var config = packagePair.Value;
            var shouldImport = package == PredefinedCityModelPackage.Building || package == PredefinedCityModelPackage.Road;
            config.ImportPackage = shouldImport;
            if (!shouldImport) continue;

            var targetLod = Math.Min(1, config.LODRange.AvailableMaxLOD);
            config.LODRange = new LODRange(targetLod, targetLod, config.LODRange.AvailableMaxLOD);
            config.IncludeTexture = false;
            config.EnableTexturePacking = false;
            config.DoSetAttrInfo = false;
            config.DoSetMeshCollider = true;
            config.MeshGranularity = MeshGranularity.PerCityModelArea;
        }

        await CityImporter.ImportAsync(importConfig, null, null);
        stopwatch.Stop();
        var collidersAfter = UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length;
        var importedColliderCount = collidersAfter - collidersBefore;
        Debug.Log($"ENVIRONMENT_COST_IMPORT_DONE area={runConfig.areaId} dataset={datasetId} seconds={stopwatch.Elapsed.TotalSeconds:F1} newColliders={importedColliderCount}");
        return new DatasetImportReport
        {
            datasetId = datasetId,
            title = title,
            thirdMeshCount = gridCodes.Length,
            importSeconds = stopwatch.Elapsed.TotalSeconds,
            importedColliderCount = importedColliderCount
        };
    }

    private static string FindLocalDatasetRoot(string datasetId)
    {
        var extractionRoot = runConfig.DatasetRootFor(datasetId);
        if (!Directory.Exists(extractionRoot))
        {
            throw new DirectoryNotFoundException($"Extracted PLATEAU dataset was not found: {extractionRoot}");
        }

        var udxDirectory = Directory.EnumerateDirectories(extractionRoot, "udx", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (udxDirectory == null)
        {
            throw new DirectoryNotFoundException($"PLATEAU udx directory was not found below: {extractionRoot}");
        }

        return Directory.GetParent(udxDirectory)?.FullName
            ?? throw new InvalidOperationException($"PLATEAU dataset root could not be resolved from: {udxDirectory}");
    }

    private static (int building, int road, int other) AssignColliderLayers()
    {
        var building = 0;
        var road = 0;
        var other = 0;
        foreach (var collider in UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None))
        {
            var package = FindPackageFromHierarchy(collider.transform);
            if (package == "bldg")
            {
                collider.gameObject.layer = BuildingLayer;
                building++;
            }
            else if (package == "tran")
            {
                collider.gameObject.layer = RoadLayer;
                road++;
            }
            else
            {
                other++;
            }
        }
        return (building, road, other);
    }

    private static string FindPackageFromHierarchy(Transform transform)
    {
        for (var current = transform; current != null; current = current.parent)
        {
            var name = current.name.ToLowerInvariant();
            if (name.Contains("_bldg_")) return "bldg";
            if (name.Contains("_tran_")) return "tran";
        }
        return string.Empty;
    }

    private static List<EdgeResult> AnalyzeOsmEdges(string osmPath, GeoReference geoReference,
        out int osmWayCount, out int sourceSegmentCount, out long sampleCount, out long validSampleCount,
        out long noGroundSampleCount)
    {
        var root = JObject.Parse(File.ReadAllText(osmPath));
        var elements = root["elements"] as JArray ?? throw new InvalidOperationException("OSM elements are missing.");
        var results = new List<EdgeResult>();
        osmWayCount = 0;
        sourceSegmentCount = 0;
        sampleCount = 0;
        validSampleCount = 0;
        noGroundSampleCount = 0;
        var sunDirections = AnalysisHours.ToDictionary(hour => hour, hour => CalculateSun(hour));
        var buildingMask = 1 << BuildingLayer;
        var roadMask = 1 << RoadLayer;

        foreach (var element in elements.OfType<JObject>())
        {
            if (!string.Equals((string)element["type"], "way", StringComparison.Ordinal)) continue;
            var tags = element["tags"] as JObject;
            var highway = (string)tags?["highway"];
            if (!IsWalkable(tags, highway)) continue;
            var geometry = element["geometry"] as JArray;
            var nodes = element["nodes"] as JArray;
            if (geometry == null || geometry.Count < 2) continue;
            osmWayCount++;
            var wayId = (long?)element["id"] ?? 0L;

            for (var segmentIndex = 0; segmentIndex < geometry.Count - 1; segmentIndex++)
            {
                sourceSegmentCount++;
                var from = geometry[segmentIndex] as JObject;
                var to = geometry[segmentIndex + 1] as JObject;
                if (from == null || to == null) continue;
                var fromLatitude = (double?)from["lat"];
                var fromLongitude = (double?)from["lon"];
                var toLatitude = (double?)to["lat"];
                var toLongitude = (double?)to["lon"];
                if (!fromLatitude.HasValue || !fromLongitude.HasValue || !toLatitude.HasValue || !toLongitude.HasValue) continue;

                var lengthMeters = DistanceMeters(fromLatitude.Value, fromLongitude.Value, toLatitude.Value, toLongitude.Value);
                if (lengthMeters <= 0.01) continue;
                var subdivisions = Math.Max(1, (int)Math.Ceiling(lengthMeters / SampleSpacingMeters));
                var shadeCounts = AnalysisHours.ToDictionary(hour => hour, _ => 0);
                var valid = 0;
                var noGround = 0;
                var inCoverage = 0;

                for (var sampleIndex = 0; sampleIndex <= subdivisions; sampleIndex++)
                {
                    var ratio = sampleIndex / (double)subdivisions;
                    var latitude = Lerp(fromLatitude.Value, toLatitude.Value, ratio);
                    var longitude = Lerp(fromLongitude.Value, toLongitude.Value, ratio);
                    if (DistanceMeters(CenterLatitude, CenterLongitude, latitude, longitude) > RadiusMeters) continue;
                    inCoverage++;
                    sampleCount++;

                    var projected = geoReference.Project(new GeoCoordinate(latitude, longitude, 0.0));
                    var rayOrigin = new Vector3((float)projected.X, 500.0f, (float)projected.Z);
                    if (!Physics.Raycast(rayOrigin, Vector3.down, out var groundHit, 1000.0f, roadMask,
                            QueryTriggerInteraction.Ignore))
                    {
                        noGround++;
                        noGroundSampleCount++;
                        continue;
                    }

                    valid++;
                    validSampleCount++;
                    var pedestrianPoint = groundHit.point + Vector3.up * (float)runConfig.pedestrianHeightMeters;
                    foreach (var hour in AnalysisHours)
                    {
                        var sun = sunDirections[hour];
                        if (sun.elevationDegrees <= 0.0) continue;
                        if (Physics.Raycast(pedestrianPoint, sun.direction, 10000.0f, buildingMask,
                                QueryTriggerInteraction.Ignore))
                        {
                            shadeCounts[hour]++;
                        }
                    }
                }

                if (inCoverage == 0) continue;
                var walkingSeconds = lengthMeters / WalkingSpeedMetersPerSecond;
                var hourly = AnalysisHours.Select(hour =>
                {
                    var shadeRatio = valid > 0 ? shadeCounts[hour] / (double)valid : (double?)null;
                    return new HourlyCost
                    {
                        hour = hour,
                        sunElevationDegrees = sunDirections[hour].elevationDegrees,
                        shadeRatio = shadeRatio,
                        solarExposureSeconds = shadeRatio.HasValue ? walkingSeconds * (1.0 - shadeRatio.Value) : null
                    };
                }).ToArray();

                results.Add(new EdgeResult
                {
                    id = $"osm-way-{wayId}-{segmentIndex}",
                    osmWayId = wayId,
                    fromNodeId = nodes != null && segmentIndex < nodes.Count ? (long?)nodes[segmentIndex] : null,
                    toNodeId = nodes != null && segmentIndex + 1 < nodes.Count ? (long?)nodes[segmentIndex + 1] : null,
                    highway = highway,
                    coordinates = new[]
                    {
                        new[] { fromLongitude.Value, fromLatitude.Value },
                        new[] { toLongitude.Value, toLatitude.Value }
                    },
                    lengthMeters = lengthMeters,
                    walkingSeconds = walkingSeconds,
                    sampleCount = inCoverage,
                    validSampleCount = valid,
                    noGroundSampleCount = noGround,
                    hourly = hourly
                });
            }
        }
        return results;
    }

    private static bool IsWalkable(JObject tags, string highway)
    {
        if (string.IsNullOrWhiteSpace(highway)) return false;
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "motorway", "motorway_link", "trunk", "trunk_link", "construction", "proposed", "raceway"
        };
        if (excluded.Contains(highway)) return false;
        var area = (string)tags?["area"];
        var access = (string)tags?["access"];
        var foot = (string)tags?["foot"];
        if (string.Equals(area, "yes", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(access, "private", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(access, "no", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(foot, "no", StringComparison.OrdinalIgnoreCase);
    }

    private static SunPosition CalculateSun(int localHour)
    {
        const double timezoneHours = 9.0;
        var dayOfYear = AnalysisDate.DayOfYear;
        var fractionalYear = 2.0 * Math.PI / 365.0 * (dayOfYear - 1 + (localHour - 12.0) / 24.0);
        var equationOfTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(fractionalYear)
            - 0.032077 * Math.Sin(fractionalYear) - 0.014615 * Math.Cos(2 * fractionalYear)
            - 0.040849 * Math.Sin(2 * fractionalYear));
        var declination = 0.006918 - 0.399912 * Math.Cos(fractionalYear)
            + 0.070257 * Math.Sin(fractionalYear) - 0.006758 * Math.Cos(2 * fractionalYear)
            + 0.000907 * Math.Sin(2 * fractionalYear) - 0.002697 * Math.Cos(3 * fractionalYear)
            + 0.00148 * Math.Sin(3 * fractionalYear);
        var timeOffset = equationOfTime + 4.0 * CenterLongitude - 60.0 * timezoneHours;
        var trueSolarMinutes = localHour * 60.0 + timeOffset;
        var hourAngleDegrees = trueSolarMinutes / 4.0 - 180.0;
        var hourAngle = hourAngleDegrees * Math.PI / 180.0;
        var latitude = CenterLatitude * Math.PI / 180.0;
        var cosineZenith = Math.Sin(latitude) * Math.Sin(declination)
            + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle);
        cosineZenith = Math.Max(-1.0, Math.Min(1.0, cosineZenith));
        var zenith = Math.Acos(cosineZenith);
        var elevationDegrees = 90.0 - zenith * 180.0 / Math.PI;
        var azimuthDegrees = (Math.Atan2(Math.Sin(hourAngle),
            Math.Cos(hourAngle) * Math.Sin(latitude) - Math.Tan(declination) * Math.Cos(latitude))
            * 180.0 / Math.PI + 180.0) % 360.0;
        var elevation = elevationDegrees * Math.PI / 180.0;
        var azimuth = azimuthDegrees * Math.PI / 180.0;
        var direction = new Vector3((float)(Math.Sin(azimuth) * Math.Cos(elevation)),
            (float)Math.Sin(elevation), (float)(Math.Cos(azimuth) * Math.Cos(elevation))).normalized;
        return new SunPosition { elevationDegrees = elevationDegrees, azimuthDegrees = azimuthDegrees, direction = direction };
    }

    private static double Lerp(double from, double to, double ratio) => from + (to - from) * ratio;

    private static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadiusMeters = 6371008.8;
        var latitudeARadians = latitudeA * Math.PI / 180.0;
        var latitudeBRadians = latitudeB * Math.PI / 180.0;
        var latitudeDelta = (latitudeB - latitudeA) * Math.PI / 180.0;
        var longitudeDelta = (longitudeB - longitudeA) * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitudeDelta / 2.0);
        var sinLongitude = Math.Sin(longitudeDelta / 2.0);
        var haversine = sinLatitude * sinLatitude + Math.Cos(latitudeARadians) * Math.Cos(latitudeBRadians)
            * sinLongitude * sinLongitude;
        return earthRadiusMeters * 2.0 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1.0 - haversine));
    }

    [Serializable] private sealed class CoverageReport { public List<DatasetCoverage> datasets; }
    [Serializable] private sealed class DatasetCoverage { public string id; public string title; public List<string> gridCodes; }
    [Serializable] private sealed class SunPosition { public double elevationDegrees; public double azimuthDegrees; [JsonIgnore] public Vector3 direction; }
    [Serializable] private sealed class SourceMetadata { public string[] plateauDatasetIds; public string plateauSdkVersion; public string unityVersion; public string osmSource; public string osmDownloadedAt; }
    [Serializable] private sealed class AnalysisSettings { public string date; public string timezone; public int[] hours; public double sampleSpacingMeters; public double pedestrianHeightMeters; public double walkingSpeedMetersPerSecond; public string[] obstaclePackages; public string[] groundPackages; }
    [Serializable] private sealed class AnalysisOutput { public string schemaVersion; public string areaId; public string generatedAt; public double[] center; public double radiusMeters; public int coordinateZoneId; public SourceMetadata source; public AnalysisSettings settings; public List<EdgeResult> edges; }
    [Serializable] private sealed class EdgeResult { public string id; public long osmWayId; public long? fromNodeId; public long? toNodeId; public string highway; public double[][] coordinates; public double lengthMeters; public double walkingSeconds; public int sampleCount; public int validSampleCount; public int noGroundSampleCount; public HourlyCost[] hourly; }
    [Serializable] private sealed class HourlyCost { public int hour; public double sunElevationDegrees; public double? shadeRatio; public double? solarExposureSeconds; }
    [Serializable] private sealed class DatasetImportReport { public string datasetId; public string title; public int thirdMeshCount; public double importSeconds; public int importedColliderCount; }
    [Serializable] private sealed class AnalysisSummary { public string status; public string generatedAt; public string areaId; public double[] center; public double radiusMeters; public List<DatasetImportReport> datasets; public int uniqueThirdMeshes; public int buildingColliderCount; public int roadColliderCount; public int osmWayCount; public int sourceSegmentCount; public int analyzedEdgeCount; public long sampleCount; public long validSampleCount; public long noGroundSampleCount; public double analysisSeconds; public double totalSeconds; public long peakWorkingSetBytes; public long outputBytes; public string outputPath; }
}
