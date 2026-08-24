using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        string outputPath = null;
        string summaryPath = null;
        string statePath = null;
        string cancellationPath = null;
        AnalysisState state = null;
        var exitCode = 0;
        try
        {
            runConfig = AnalysisRunConfig.LoadForCurrentProcess();
            var coveragePath = runConfig.ResolvePath(runConfig.coverageOutputPath);
            var osmPath = runConfig.ResolvePath(runConfig.osmInputPath);
            outputPath = runConfig.ResolvePath(runConfig.environmentCostOutputPath);
            summaryPath = runConfig.ResolvePath(runConfig.summaryOutputPath);
            statePath = runConfig.StateOutputPath;
            cancellationPath = runConfig.CancellationRequestPath;
            DeleteIfExists(outputPath + ".partial");
            DeleteIfExists(cancellationPath);
            state = AnalysisState.Start(runConfig.areaId, outputPath, AnalysisHours.Length);
            WriteJsonAtomic(statePath, state, Formatting.Indented);

            var coverage = JsonConvert.DeserializeObject<CoverageReport>(File.ReadAllText(coveragePath))
                ?? throw new InvalidOperationException("Coverage report could not be parsed.");
            if (!File.Exists(osmPath)) throw new FileNotFoundException("OSM input was not found.", osmPath);

            state.phase = "cache-check";
            state.analysisKey = CalculateAnalysisKey(coverage, coveragePath, osmPath);
            state.message = "時刻別キャッシュを確認しています。";
            state.Touch();
            WriteJsonAtomic(statePath, state, Formatting.Indented);
            var cacheStopwatch = Stopwatch.StartNew();
            var cache = runConfig.ForceRecalculate
                ? HourlyCacheBundle.Empty(state.analysisKey, AnalysisHours)
                : LoadHourlyCache(runConfig.CacheDirectoryPath, state.analysisKey, AnalysisHours);
            cacheStopwatch.Stop();
            var missingHours = AnalysisHours.Where(hour => !cache.hourlyByHour.ContainsKey(hour)).ToArray();

            var importReports = new List<DatasetImportReport>();
            var layerCounts = (building: 0, road: 0, other: 0);
            var analysisStopwatch = new Stopwatch();
            List<EdgeResult> baseEdges;
            var osmWayCount = cache.osmWayCount;
            var sourceSegmentCount = cache.sourceSegmentCount;
            var sampleCount = cache.sampleCount;
            var validSampleCount = cache.validSampleCount;
            var noGroundSampleCount = cache.noGroundSampleCount;

            if (missingHours.Length > 0 || cache.baseEdges == null)
            {
                using var centerGeoReference = GeoReference.Create(new PlateauVector3d(0.0, 0.0, 0.0), 1.0f,
                    CoordinateSystem.EUN, CoordinateZoneId);
                var referencePoint = centerGeoReference.Project(new GeoCoordinate(CenterLatitude, CenterLongitude, 0.0));
                using var localGeoReference = GeoReference.Create(referencePoint, 1.0f, CoordinateSystem.EUN, CoordinateZoneId);

                state.phase = "citygml-import";
                state.message = $"CityGMLを読み込んでいます（未キャッシュ {missingHours.Length}/{AnalysisHours.Length} 時刻）。";
                state.Touch();
                WriteJsonAtomic(statePath, state, Formatting.Indented);
                foreach (var dataset in coverage.datasets)
                {
                    ThrowIfCancellationRequested(cancellationPath);
                    var thirdMeshCodes = dataset.gridCodes.Where(code => code.Length == 8).Distinct().ToArray();
                    if (thirdMeshCodes.Length == 0) continue;
                    var localDatasetRoot = FindLocalDatasetRoot(runConfig, dataset.id);
                    importReports.Add(await ImportDataset(runConfig, dataset.id, dataset.title, localDatasetRoot, thirdMeshCodes, referencePoint));
                }

                layerCounts = AssignColliderLayers();
                Physics.SyncTransforms();
                Debug.Log($"ENVIRONMENT_COST_IMPORT_SUMMARY area={runConfig.areaId} buildingColliders={layerCounts.building} roadColliders={layerCounts.road} otherColliders={layerCounts.other}");
                if (layerCounts.building == 0 || layerCounts.road == 0)
                {
                    throw new InvalidOperationException("Building or road colliders were not imported.");
                }

                analysisStopwatch.Start();
                var hoursToCalculate = cache.baseEdges == null ? AnalysisHours : missingHours;
                var calculatedEdges = AnalyzeOsmEdges(osmPath, localGeoReference, hoursToCalculate,
                    (completed, total) => ReportAnalysisProgress(state, statePath, cancellationPath, completed, total,
                        AnalysisHours.Length - missingHours.Length, AnalysisHours.Length),
                    out osmWayCount, out sourceSegmentCount, out sampleCount, out validSampleCount, out noGroundSampleCount);
                analysisStopwatch.Stop();
                baseEdges = calculatedEdges.Select(CloneWithoutHourly).ToList();
                if (cache.baseEdges != null) EnsureSameEdgeSet(cache.baseEdges, baseEdges);
                foreach (var edge in calculatedEdges)
                {
                    foreach (var hourly in edge.hourly) cache.AddHourly(edge.id, hourly);
                }
                var cacheWriteStopwatch = Stopwatch.StartNew();
                SaveHourlyCache(runConfig.CacheDirectoryPath, state.analysisKey, baseEdges, cache,
                    osmWayCount, sourceSegmentCount, sampleCount, validSampleCount, noGroundSampleCount,
                    hoursToCalculate);
                cacheWriteStopwatch.Stop();
                cache.writeSeconds = cacheWriteStopwatch.Elapsed.TotalSeconds;
            }
            else
            {
                baseEdges = cache.baseEdges;
                state.phase = "cache-hit";
                state.message = "全時刻をキャッシュから復元しました。";
                state.completedEdges = baseEdges.Count;
                state.totalEdges = baseEdges.Count;
                state.completedHours = AnalysisHours.Length;
                state.Touch();
                WriteJsonAtomic(statePath, state, Formatting.Indented);
            }

            cache.readSeconds = cacheStopwatch.Elapsed.TotalSeconds;
            var edges = AssembleEdges(baseEdges, cache.hourlyByHour, AnalysisHours);
            ValidateCompleteResult(edges, AnalysisHours);

            var process = Process.GetCurrentProcess();
            var output = new AnalysisOutput
            {
                schemaVersion = "environment-cost-analysis-0.2",
                status = "completed",
                analysisKey = state.analysisKey,
                areaId = runConfig.areaId,
                generatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                center = new[] { CenterLongitude, CenterLatitude },
                radiusMeters = RadiusMeters,
                coordinateZoneId = CoordinateZoneId,
                source = new SourceMetadata
                {
                    plateauDatasetIds = coverage.datasets.Select(report => report.id).ToArray(),
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
            output.resultFingerprintSha256 = ResultFingerprint(output);

            WriteJsonAtomic(outputPath, output, Formatting.None);

            totalStopwatch.Stop();
            var summary = new AnalysisSummary
            {
                schemaVersion = "environment-cost-analysis-summary-0.2",
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
                peakWorkingSetBytes = PeakWorkingSetBytes(process),
                outputBytes = new FileInfo(outputPath).Length,
                outputPath = outputPath,
                analysisKey = output.analysisKey,
                resultFingerprintSha256 = output.resultFingerprintSha256,
                cacheEnabled = !runConfig.ForceRecalculate,
                cacheBaseHit = cache.baseHit,
                cacheHourlyHitCount = AnalysisHours.Length - missingHours.Length,
                cacheHourlyMissCount = missingHours.Length,
                cacheReadSeconds = cache.readSeconds,
                cacheWriteSeconds = cache.writeSeconds,
                importSkipped = missingHours.Length == 0 && cache.baseHit
            };
            WriteJsonAtomic(summaryPath, summary, Formatting.Indented);
            state.Complete(edges.Count, output.resultFingerprintSha256);
            WriteJsonAtomic(statePath, state, Formatting.Indented);
            Debug.Log($"ENVIRONMENT_COST_ANALYSIS_COMPLETE area={runConfig.areaId} edges={edges.Count} samples={sampleCount} valid={validSampleCount} noGround={noGroundSampleCount} cacheHits={summary.cacheHourlyHitCount} cacheMisses={summary.cacheHourlyMissCount} fingerprint={output.resultFingerprintSha256} totalSeconds={summary.totalSeconds:F1} peakBytes={summary.peakWorkingSetBytes} outputBytes={summary.outputBytes}");
        }
        catch (OperationCanceledException exception)
        {
            exitCode = 2;
            WriteTerminalState(statePath, state, "cancelled", exception.Message);
            DeleteIfExists(outputPath + ".partial");
            Debug.LogWarning($"ENVIRONMENT_COST_ANALYSIS_CANCELLED {exception.Message}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            WriteTerminalState(statePath, state, "failed", exception.Message);
            DeleteIfExists(outputPath + ".partial");
            Debug.LogException(exception);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            EditorApplication.Exit(exitCode);
        }
    }

    internal static async Task<DatasetImportReport> ImportDataset(AnalysisRunConfig config, string datasetId, string title,
        string localDatasetRoot, string[] gridCodes, PlateauVector3d referencePoint)
    {
        var stopwatch = Stopwatch.StartNew();
        var collidersBefore = UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length;
        Debug.Log($"ENVIRONMENT_COST_IMPORT_START area={config.areaId} dataset={datasetId} title={title} grids={gridCodes.Length}");

        using var gridCodeList = GridCodeList.CreateFromGridCodesStr(gridCodes);
        Debug.Log($"ENVIRONMENT_COST_LOCAL_SOURCE dataset={datasetId} path={localDatasetRoot}");
        var sourceConfig = new DatasetSourceConfigLocal(localDatasetRoot);
        var areaResult = new AreaSelectResult(new ConfigBeforeAreaSelect(sourceConfig, config.coordinateZoneId), gridCodeList,
            AreaSelectResult.ResultReason.Confirm);
        var importConfig = CityImportConfig.CreateWithAreaSelectResult(areaResult);
        importConfig.ReferencePoint = referencePoint;

        foreach (var packagePair in importConfig.PackageImportConfigDict.ForEachPackagePair)
        {
            var package = packagePair.Key;
            var packageConfig = packagePair.Value;
            var shouldImport = package == PredefinedCityModelPackage.Building || package == PredefinedCityModelPackage.Road;
            packageConfig.ImportPackage = shouldImport;
            if (!shouldImport) continue;

            var targetLod = Math.Min(1, packageConfig.LODRange.AvailableMaxLOD);
            packageConfig.LODRange = new LODRange(targetLod, targetLod, packageConfig.LODRange.AvailableMaxLOD);
            packageConfig.IncludeTexture = false;
            packageConfig.EnableTexturePacking = false;
            packageConfig.DoSetAttrInfo = false;
            packageConfig.DoSetMeshCollider = true;
            packageConfig.MeshGranularity = MeshGranularity.PerCityModelArea;
        }

        await CityImporter.ImportAsync(importConfig, null, null);
        stopwatch.Stop();
        var collidersAfter = UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length;
        var importedColliderCount = collidersAfter - collidersBefore;
        Debug.Log($"ENVIRONMENT_COST_IMPORT_DONE area={config.areaId} dataset={datasetId} seconds={stopwatch.Elapsed.TotalSeconds:F1} newColliders={importedColliderCount}");
        return new DatasetImportReport
        {
            datasetId = datasetId,
            title = title,
            thirdMeshCount = gridCodes.Length,
            importSeconds = stopwatch.Elapsed.TotalSeconds,
            importedColliderCount = importedColliderCount
        };
    }

    internal static string FindLocalDatasetRoot(AnalysisRunConfig config, string datasetId)
    {
        var extractionRoot = config.DatasetRootFor(datasetId);
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

    internal static (int building, int road, int other) AssignColliderLayers() => AssignColliderLayers(null);

    /// <summary>Assigns layers only within a generated inspection Scene when one is supplied.</summary>
    internal static (int building, int road, int other) AssignColliderLayers(UnityEngine.SceneManagement.Scene? targetScene)
    {
        var building = 0;
        var road = 0;
        var other = 0;
        foreach (var collider in UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None))
        {
            if (targetScene.HasValue && collider.gameObject.scene != targetScene.Value) continue;
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

    private static List<EdgeResult> AnalyzeOsmEdges(string osmPath, GeoReference geoReference, int[] analysisHours,
        Action<int, int> progress,
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
        var sunDirections = analysisHours.ToDictionary(hour => hour, hour =>
            HourlyEnvironmentCostRules.CalculateSun(AnalysisDate, hour, CenterLatitude, CenterLongitude, runConfig.timezone));
        var buildingMask = 1 << BuildingLayer;
        var roadMask = 1 << RoadLayer;
        var totalSegments = elements.OfType<JObject>().Where(element =>
        {
            if (!string.Equals((string)element["type"], "way", StringComparison.Ordinal)) return false;
            var elementTags = element["tags"] as JObject;
            return IsWalkable(elementTags, (string)elementTags?["highway"]);
        }).Sum(element => Math.Max(0, ((element["geometry"] as JArray)?.Count ?? 0) - 1));

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
                // Writing and flushing the state file is intentionally coarse-grained. The
                // CityGML/OSM pass can contain hundreds of thousands of segments, and flushing
                // every few hundred segments made state reporting dominate analysis time.
                if (sourceSegmentCount == 1 || sourceSegmentCount % 5000 == 0 || sourceSegmentCount == totalSegments)
                {
                    progress?.Invoke(sourceSegmentCount, totalSegments);
                }
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
                var shadeCounts = analysisHours.ToDictionary(hour => hour, _ => 0);
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
                    foreach (var hour in analysisHours)
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
                var hourly = analysisHours.Select(hour =>
                {
                    var sun = sunDirections[hour];
                    var status = HourlyEnvironmentCostRules.DetermineStatus(inCoverage, valid, noGround,
                        sun.elevationDegrees, out var exclusionReason);
                    var shadeRatio = status == "missing" ? (double?)null : shadeCounts[hour] / (double)valid;
                    return new HourlyCost
                    {
                        hour = hour,
                        timestamp = HourlyEnvironmentCostRules.Timestamp(AnalysisDate, hour, runConfig.timezone),
                        status = status,
                        exclusionReason = exclusionReason,
                        sunElevationDegrees = sun.elevationDegrees,
                        shadeRatio = shadeRatio,
                        solarExposureSeconds = shadeRatio.HasValue
                            ? HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(walkingSeconds, shadeRatio.Value)
                            : null
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

    private static void ReportAnalysisProgress(AnalysisState state, string statePath, string cancellationPath,
        int completedEdges, int totalEdges, int completedHours, int totalHours)
    {
        ThrowIfCancellationRequested(cancellationPath);
        var progress = totalEdges == 0 ? 0.0f : completedEdges / (float)totalEdges;
        if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar("環境コスト解析",
                $"道路区間 {completedEdges:N0}/{totalEdges:N0}", progress))
        {
            throw new OperationCanceledException("Unity Editorから解析がキャンセルされました。");
        }
        state.phase = "hourly-analysis";
        state.message = $"道路区間を解析しています（{completedEdges:N0}/{totalEdges:N0}）。";
        state.completedEdges = completedEdges;
        state.totalEdges = totalEdges;
        state.completedHours = completedHours;
        state.totalHours = totalHours;
        state.Touch();
        WriteJsonAtomic(statePath, state, Formatting.Indented);
    }

    private static void ThrowIfCancellationRequested(string cancellationPath)
    {
        if (!string.IsNullOrWhiteSpace(cancellationPath) && File.Exists(cancellationPath))
        {
            throw new OperationCanceledException($"Cancellation request was detected: {cancellationPath}");
        }
    }

    private static string CalculateAnalysisKey(CoverageReport coverage, string coveragePath, string osmPath)
    {
        var input = new StringBuilder();
        input.AppendLine("environment-cost-analysis-key-0.2");
        input.AppendLine(runConfig.areaId);
        input.AppendLine(string.Join(",", runConfig.center.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
        input.AppendLine(runConfig.radiusMeters.ToString("R", CultureInfo.InvariantCulture));
        input.AppendLine(runConfig.coordinateZoneId.ToString(CultureInfo.InvariantCulture));
        input.AppendLine(runConfig.date);
        input.AppendLine(runConfig.timezone);
        input.AppendLine(string.Join(",", AnalysisHours));
        input.AppendLine(runConfig.sampleSpacingMeters.ToString("R", CultureInfo.InvariantCulture));
        input.AppendLine(runConfig.pedestrianHeightMeters.ToString("R", CultureInfo.InvariantCulture));
        input.AppendLine(runConfig.walkingSpeedMetersPerSecond.ToString("R", CultureInfo.InvariantCulture));
        input.AppendLine(FileSha256(coveragePath));
        input.AppendLine(FileSha256(osmPath));
        foreach (var dataset in coverage.datasets.OrderBy(item => item.id, StringComparer.Ordinal))
        {
            input.AppendLine(dataset.id);
            var root = runConfig.DatasetRootFor(dataset.id);
            if (!Directory.Exists(root))
            {
                input.AppendLine("missing");
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(root, "*.gml", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var info = new FileInfo(path);
                input.Append(Path.GetRelativePath(root, path).Replace('\\', '/')).Append('|')
                    .Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).AppendLine();
            }
        }
        return Sha256(input.ToString());
    }

    private static HourlyCacheBundle LoadHourlyCache(string cacheDirectory, string analysisKey, int[] hours)
    {
        var bundle = HourlyCacheBundle.Empty(analysisKey, hours);
        try
        {
            var basePath = Path.Combine(cacheDirectory, $"{analysisKey}-edges.json");
            if (!File.Exists(basePath)) return bundle;
            var baseCache = JsonConvert.DeserializeObject<BaseEdgeCache>(File.ReadAllText(basePath));
            if (baseCache == null || baseCache.schemaVersion != "environment-cost-base-cache-0.2" ||
                baseCache.analysisKey != analysisKey || baseCache.edges == null)
            {
                return bundle;
            }
            bundle.baseHit = true;
            bundle.baseEdges = baseCache.edges;
            bundle.osmWayCount = baseCache.osmWayCount;
            bundle.sourceSegmentCount = baseCache.sourceSegmentCount;
            bundle.sampleCount = baseCache.sampleCount;
            bundle.validSampleCount = baseCache.validSampleCount;
            bundle.noGroundSampleCount = baseCache.noGroundSampleCount;
            var baseIds = new HashSet<string>(bundle.baseEdges.Select(edge => edge.id), StringComparer.Ordinal);
            foreach (var hour in hours)
            {
                var hourPath = Path.Combine(cacheDirectory, $"{analysisKey}-hour-{hour:00}.json");
                if (!File.Exists(hourPath)) continue;
                var hourCache = JsonConvert.DeserializeObject<HourlySliceCache>(File.ReadAllText(hourPath));
                if (hourCache == null || hourCache.schemaVersion != "environment-cost-hour-cache-0.2" ||
                    hourCache.analysisKey != analysisKey || hourCache.hour != hour || hourCache.edges == null ||
                    hourCache.edges.Count != baseIds.Count || hourCache.edges.Any(edge => !baseIds.Contains(edge.id)))
                {
                    continue;
                }
                var costs = hourCache.edges.ToDictionary(edge => edge.id, edge => edge.value, StringComparer.Ordinal);
                if (costs.Count == baseIds.Count) bundle.hourlyByHour[hour] = costs;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"ENVIRONMENT_COST_CACHE_IGNORED key={analysisKey} reason={exception.Message}");
            return HourlyCacheBundle.Empty(analysisKey, hours);
        }
        return bundle;
    }

    private static void SaveHourlyCache(string cacheDirectory, string analysisKey, List<EdgeResult> baseEdges,
        HourlyCacheBundle bundle, int osmWayCount, int sourceSegmentCount, long sampleCount,
        long validSampleCount, long noGroundSampleCount, int[] calculatedHours)
    {
        Directory.CreateDirectory(cacheDirectory);
        WriteJsonAtomic(Path.Combine(cacheDirectory, $"{analysisKey}-edges.json"), new BaseEdgeCache
        {
            schemaVersion = "environment-cost-base-cache-0.2",
            analysisKey = analysisKey,
            osmWayCount = osmWayCount,
            sourceSegmentCount = sourceSegmentCount,
            sampleCount = sampleCount,
            validSampleCount = validSampleCount,
            noGroundSampleCount = noGroundSampleCount,
            edges = baseEdges
        }, Formatting.None);
        foreach (var hour in calculatedHours)
        {
            if (!bundle.hourlyByHour.TryGetValue(hour, out var costs)) continue;
            var slice = new HourlySliceCache
            {
                schemaVersion = "environment-cost-hour-cache-0.2",
                analysisKey = analysisKey,
                hour = hour,
                edges = baseEdges.Select(edge => new EdgeHourlyCache
                {
                    id = edge.id,
                    value = costs[edge.id]
                }).ToList()
            };
            WriteJsonAtomic(Path.Combine(cacheDirectory, $"{analysisKey}-hour-{hour:00}.json"), slice, Formatting.None);
        }
    }

    private static List<EdgeResult> AssembleEdges(List<EdgeResult> baseEdges,
        Dictionary<int, Dictionary<string, HourlyCost>> hourlyByHour, int[] hours)
    {
        return baseEdges.Select(baseEdge =>
        {
            var edge = CloneWithoutHourly(baseEdge);
            edge.hourly = hours.Select(hour =>
            {
                if (!hourlyByHour.TryGetValue(hour, out var values) || !values.TryGetValue(edge.id, out var value))
                {
                    throw new InvalidOperationException($"Hourly cache is incomplete: edge={edge.id} hour={hour}");
                }
                return value;
            }).ToArray();
            return edge;
        }).ToList();
    }

    private static EdgeResult CloneWithoutHourly(EdgeResult source) => new EdgeResult
    {
        id = source.id,
        osmWayId = source.osmWayId,
        fromNodeId = source.fromNodeId,
        toNodeId = source.toNodeId,
        highway = source.highway,
        coordinates = source.coordinates,
        lengthMeters = source.lengthMeters,
        walkingSeconds = source.walkingSeconds,
        sampleCount = source.sampleCount,
        validSampleCount = source.validSampleCount,
        noGroundSampleCount = source.noGroundSampleCount,
        hourly = Array.Empty<HourlyCost>()
    };

    private static void EnsureSameEdgeSet(List<EdgeResult> cached, List<EdgeResult> calculated)
    {
        var cachedIds = cached.Select(edge => edge.id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var calculatedIds = calculated.Select(edge => edge.id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (!cachedIds.SequenceEqual(calculatedIds))
        {
            throw new InvalidOperationException("Cached and recalculated road edge IDs do not match.");
        }
    }

    private static void ValidateCompleteResult(List<EdgeResult> edges, int[] expectedHours)
    {
        if (edges.Count == 0) throw new InvalidOperationException("No target road edges were generated.");
        if (edges.Select(edge => edge.id).Distinct(StringComparer.Ordinal).Count() != edges.Count)
            throw new InvalidOperationException("Road edge IDs are not unique.");
        foreach (var edge in edges)
        {
            if (edge.sampleCount <= 0 || edge.validSampleCount + edge.noGroundSampleCount != edge.sampleCount)
                throw new InvalidOperationException($"Invalid sample coverage: {edge.id}");
            if (edge.hourly == null || !edge.hourly.Select(value => value.hour).SequenceEqual(expectedHours))
                throw new InvalidOperationException($"Hourly slices are incomplete or unordered: {edge.id}");
            foreach (var hourly in edge.hourly)
            {
                var expectedTimestamp = HourlyEnvironmentCostRules.Timestamp(AnalysisDate, hourly.hour, runConfig.timezone);
                if (hourly.timestamp != expectedTimestamp) throw new InvalidOperationException($"Invalid timestamp: {edge.id} {hourly.hour}");
                if (hourly.status == "missing")
                {
                    if (hourly.shadeRatio.HasValue || hourly.solarExposureSeconds.HasValue || string.IsNullOrWhiteSpace(hourly.exclusionReason))
                        throw new InvalidOperationException($"Missing slice must contain null values and a reason: {edge.id} {hourly.hour}");
                    continue;
                }
                if (hourly.status != "available" && hourly.status != "partial")
                    throw new InvalidOperationException($"Unknown hourly status: {edge.id} {hourly.hour}");
                if (!hourly.shadeRatio.HasValue || hourly.shadeRatio < 0.0 || hourly.shadeRatio > 1.0 ||
                    !hourly.solarExposureSeconds.HasValue)
                    throw new InvalidOperationException($"Calculated hourly values are invalid: {edge.id} {hourly.hour}");
                var expectedExposure = HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(edge.walkingSeconds, hourly.shadeRatio.Value);
                if (Math.Abs(expectedExposure - hourly.solarExposureSeconds.Value) > HourlyEnvironmentCostRules.FormulaToleranceSeconds)
                    throw new InvalidOperationException($"Solar exposure formula mismatch: {edge.id} {hourly.hour}");
            }
        }
    }

    private static string ResultFingerprint(AnalysisOutput output)
    {
        var stable = new
        {
            output.schemaVersion,
            output.areaId,
            output.center,
            output.radiusMeters,
            output.coordinateZoneId,
            output.settings,
            output.edges
        };
        using var sha = SHA256.Create();
        using var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        using var textWriter = new StreamWriter(crypto, new UTF8Encoding(false), 65536, true);
        using var jsonWriter = new JsonTextWriter(textWriter) { Formatting = Formatting.None, CloseOutput = false };
        JsonSerializer.CreateDefault().Serialize(jsonWriter, stable);
        jsonWriter.Flush();
        textWriter.Flush();
        crypto.FlushFinalBlock();
        return BitConverter.ToString(sha.Hash ?? throw new InvalidOperationException("SHA-256 failed."))
            .Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string Sha256(string value)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void WriteJsonAtomic(string path, object value, Formatting formatting)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Output directory is missing: {path}");
        Directory.CreateDirectory(directory);
        var partialPath = path + ".partial";
        using (var stream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        using (var textWriter = new StreamWriter(stream, new UTF8Encoding(false), 65536, true))
        using (var jsonWriter = new JsonTextWriter(textWriter) { Formatting = formatting, CloseOutput = false })
        {
            JsonSerializer.CreateDefault().Serialize(jsonWriter, value);
            jsonWriter.Flush();
            textWriter.Flush();
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(partialPath, path, null);
        else File.Move(partialPath, path);
    }

    private static long PeakWorkingSetBytes(Process process)
    {
        process.Refresh();
        // Some Unity/Mono versions report zero for the native process counters in batch mode.
        // Managed memory is a conservative fallback so the recorded metric is never a false 0.
        return new[]
        {
            process.PeakWorkingSet64,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(false)
        }.Max();
    }

    private static void WriteTerminalState(string statePath, AnalysisState state, string status, string message)
    {
        if (state == null || string.IsNullOrWhiteSpace(statePath)) return;
        try
        {
            state.status = status;
            state.phase = status;
            state.message = message;
            state.Touch();
            WriteJsonAtomic(statePath, state, Formatting.Indented);
        }
        catch (Exception stateException)
        {
            Debug.LogWarning($"ENVIRONMENT_COST_STATE_WRITE_FAILED {stateException.Message}");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
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
    [Serializable] private sealed class SourceMetadata { public string[] plateauDatasetIds; public string plateauSdkVersion; public string unityVersion; public string osmSource; public string osmDownloadedAt; }
    [Serializable] private sealed class AnalysisSettings { public string date; public string timezone; public int[] hours; public double sampleSpacingMeters; public double pedestrianHeightMeters; public double walkingSpeedMetersPerSecond; public string[] obstaclePackages; public string[] groundPackages; }
    [Serializable] private sealed class AnalysisOutput { public string schemaVersion; public string status; public string analysisKey; public string resultFingerprintSha256; public string areaId; public string generatedAt; public double[] center; public double radiusMeters; public int coordinateZoneId; public SourceMetadata source; public AnalysisSettings settings; public List<EdgeResult> edges; }
    [Serializable] private sealed class EdgeResult { public string id; public long osmWayId; public long? fromNodeId; public long? toNodeId; public string highway; public double[][] coordinates; public double lengthMeters; public double walkingSeconds; public int sampleCount; public int validSampleCount; public int noGroundSampleCount; public HourlyCost[] hourly; }
    [Serializable] private sealed class HourlyCost { public int hour; public string timestamp; public string status; public string exclusionReason; public double sunElevationDegrees; public double? shadeRatio; public double? solarExposureSeconds; }
    [Serializable] internal sealed class DatasetImportReport { public string datasetId; public string title; public int thirdMeshCount; public double importSeconds; public int importedColliderCount; }
    [Serializable] private sealed class AnalysisSummary { public string schemaVersion; public string status; public string generatedAt; public string areaId; public double[] center; public double radiusMeters; public List<DatasetImportReport> datasets; public int uniqueThirdMeshes; public int buildingColliderCount; public int roadColliderCount; public int osmWayCount; public int sourceSegmentCount; public int analyzedEdgeCount; public long sampleCount; public long validSampleCount; public long noGroundSampleCount; public double analysisSeconds; public double totalSeconds; public long peakWorkingSetBytes; public long outputBytes; public string outputPath; public string analysisKey; public string resultFingerprintSha256; public bool cacheEnabled; public bool cacheBaseHit; public int cacheHourlyHitCount; public int cacheHourlyMissCount; public double cacheReadSeconds; public double cacheWriteSeconds; public bool importSkipped; }
    [Serializable] private sealed class BaseEdgeCache { public string schemaVersion; public string analysisKey; public int osmWayCount; public int sourceSegmentCount; public long sampleCount; public long validSampleCount; public long noGroundSampleCount; public List<EdgeResult> edges; }
    [Serializable] private sealed class HourlySliceCache { public string schemaVersion; public string analysisKey; public int hour; public List<EdgeHourlyCache> edges; }
    [Serializable] private sealed class EdgeHourlyCache { public string id; public HourlyCost value; }

    private sealed class HourlyCacheBundle
    {
        public string analysisKey;
        public bool baseHit;
        public List<EdgeResult> baseEdges;
        public Dictionary<int, Dictionary<string, HourlyCost>> hourlyByHour;
        public int osmWayCount;
        public int sourceSegmentCount;
        public long sampleCount;
        public long validSampleCount;
        public long noGroundSampleCount;
        public double readSeconds;
        public double writeSeconds;

        public static HourlyCacheBundle Empty(string key, int[] hours) => new HourlyCacheBundle
        {
            analysisKey = key,
            baseHit = false,
            hourlyByHour = new Dictionary<int, Dictionary<string, HourlyCost>>()
        };

        public void AddHourly(string edgeId, HourlyCost hourly)
        {
            if (!hourlyByHour.TryGetValue(hourly.hour, out var values))
            {
                values = new Dictionary<string, HourlyCost>(StringComparer.Ordinal);
                hourlyByHour[hourly.hour] = values;
            }
            values[edgeId] = hourly;
        }
    }

    [Serializable]
    private sealed class AnalysisState
    {
        public string schemaVersion;
        public string status;
        public string phase;
        public string areaId;
        public string analysisKey;
        public string startedAt;
        public string updatedAt;
        public int completedEdges;
        public int totalEdges;
        public int completedHours;
        public int totalHours;
        public string message;
        public string outputPath;
        public string resultFingerprintSha256;

        public static AnalysisState Start(string areaId, string outputPath, int totalHours)
        {
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return new AnalysisState
            {
                schemaVersion = "environment-cost-analysis-state-0.2",
                status = "running",
                phase = "initializing",
                areaId = areaId,
                startedAt = now,
                updatedAt = now,
                totalHours = totalHours,
                message = "解析を初期化しています。",
                outputPath = outputPath
            };
        }

        public void Touch() => updatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        public void Complete(int edgeCount, string fingerprint)
        {
            status = "completed";
            phase = "completed";
            completedEdges = edgeCount;
            totalEdges = edgeCount;
            completedHours = totalHours;
            resultFingerprintSha256 = fingerprint;
            message = "全道路・全時刻の検証と出力が完了しました。";
            Touch();
        }
    }
}
