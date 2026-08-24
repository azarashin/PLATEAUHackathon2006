using System;
using System.Collections.Generic;
using PLATEAU.Network;
using UnityEditor;
using UnityEngine;

public static class DatasetCatalogProbe
{
    public static void Run()
    {
        try
        {
            var config = AnalysisRunConfig.LoadForCurrentProcess();
            var candidateDatasetIds = new HashSet<string>(config.candidateDatasetIds ?? Array.Empty<string>());
            var found = 0;
            var client = Client.Create(string.Empty, string.Empty);
            using var groups = client.GetDatasetMetadataGroup();
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var datasets = groups.At(groupIndex).Datasets;
                for (var datasetIndex = 0; datasetIndex < datasets.Length; datasetIndex++)
                {
                    var dataset = datasets.At(datasetIndex);
                    if (!candidateDatasetIds.Contains(dataset.ID)) continue;
                    found++;
                    Debug.Log($"ENVIRONMENT_COST_DATASET area={config.areaId} id={dataset.ID} title={dataset.Title} features={string.Join(",", dataset.FeatureTypes)}");
                }
            }
            client.Dispose();
            Debug.Log($"ENVIRONMENT_COST_DATASET_SUMMARY area={config.areaId} found={found} requested={candidateDatasetIds.Count}");
            if (found != candidateDatasetIds.Count)
            {
                throw new InvalidOperationException($"PLATEAU dataset catalog did not resolve every requested dataset: found={found}, requested={candidateDatasetIds.Count}.");
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
            return;
        }

        EditorApplication.Exit(0);
    }
}
