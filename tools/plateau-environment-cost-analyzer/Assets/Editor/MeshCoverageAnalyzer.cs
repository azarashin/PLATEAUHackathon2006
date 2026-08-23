using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using PLATEAU.Dataset;
using PLATEAU.Network;
using UnityEditor;
using UnityEngine;

public static class MeshCoverageAnalyzer
{
    private static AnalysisRunConfig runConfig;

    public static void Run()
    {
        try
        {
            runConfig = AnalysisRunConfig.LoadForCurrentProcess();
            var candidateDatasetIds = new HashSet<string>(runConfig.candidateDatasetIds ?? Array.Empty<string>());
            if (candidateDatasetIds.Count == 0)
            {
                throw new InvalidOperationException("candidateDatasetIds must contain at least one dataset ID.");
            }

            var report = new CoverageReport
            {
                generatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                center = new[] { runConfig.CenterLongitude, runConfig.CenterLatitude },
                radiusMeters = runConfig.radiusMeters,
                datasets = new List<DatasetCoverage>()
            };

            var client = Client.Create(string.Empty, string.Empty);
            using var groups = client.GetDatasetMetadataGroup();
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var datasets = groups.At(groupIndex).Datasets;
                for (var datasetIndex = 0; datasetIndex < datasets.Length; datasetIndex++)
                {
                    var dataset = datasets.At(datasetIndex);
                    if (!candidateDatasetIds.Contains(dataset.ID)) continue;

                    var selectedCodes = FindIntersectingGridCodes(dataset.ID);
                    if (selectedCodes.Count == 0) continue;
                    report.datasets.Add(new DatasetCoverage
                    {
                        id = dataset.ID,
                        title = dataset.Title,
                        gridCodes = selectedCodes
                    });
                    Debug.Log($"ENVIRONMENT_COST_COVERAGE area={runConfig.areaId} dataset={dataset.ID} grids={string.Join(",", selectedCodes)}");
                }
            }
            client.Dispose();

            var outputPath = runConfig.ResolvePath(runConfig.coverageOutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException());
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Debug.Log($"ENVIRONMENT_COST_COVERAGE_COMPLETE area={runConfig.areaId} datasets={report.datasets.Count} grids={CountGridCodes(report.datasets)} output={outputPath}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
            return;
        }

        EditorApplication.Exit(0);
    }

    private static List<string> FindIntersectingGridCodes(string datasetId)
    {
        var selected = new List<string>();
        var sourceConfig = new DatasetSourceConfigRemote(datasetId, string.Empty, string.Empty);
        using var source = DatasetSource.Create(sourceConfig);
        using var accessor = source.Accessor;
        using var gridCodes = accessor.GridCodes;
        for (var index = 0; index < gridCodes.Length; index++)
        {
            var gridCode = gridCodes.At(index);
            if (IntersectsCircle(gridCode.Extent)) selected.Add(gridCode.StringCode);
        }
        selected.Sort(StringComparer.Ordinal);
        return selected;
    }

    private static bool IntersectsCircle(PLATEAU.Native.Extent extent)
    {
        var closestLatitude = Math.Max(extent.Min.Latitude, Math.Min(runConfig.CenterLatitude, extent.Max.Latitude));
        var closestLongitude = Math.Max(extent.Min.Longitude, Math.Min(runConfig.CenterLongitude, extent.Max.Longitude));
        return DistanceMeters(runConfig.CenterLatitude, runConfig.CenterLongitude, closestLatitude, closestLongitude) <= runConfig.radiusMeters;
    }

    private static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadiusMeters = 6371008.8;
        var latitudeARadians = latitudeA * Math.PI / 180.0;
        var latitudeBRadians = latitudeB * Math.PI / 180.0;
        var latitudeDelta = (latitudeB - latitudeA) * Math.PI / 180.0;
        var longitudeDelta = (longitudeB - longitudeA) * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitudeDelta / 2.0);
        var sinLongitude = Math.Sin(longitudeDelta / 2.0);
        var haversine = sinLatitude * sinLatitude + Math.Cos(latitudeARadians) * Math.Cos(latitudeBRadians) * sinLongitude * sinLongitude;
        return earthRadiusMeters * 2.0 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1.0 - haversine));
    }

    private static int CountGridCodes(IEnumerable<DatasetCoverage> datasets)
    {
        var count = 0;
        foreach (var dataset in datasets) count += dataset.gridCodes.Count;
        return count;
    }

    [Serializable] private sealed class CoverageReport { public string generatedAt; public double[] center; public double radiusMeters; public List<DatasetCoverage> datasets; }
    [Serializable] private sealed class DatasetCoverage { public string id; public string title; public List<string> gridCodes; }
}
