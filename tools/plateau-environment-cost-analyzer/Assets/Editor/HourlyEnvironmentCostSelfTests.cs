using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class HourlyEnvironmentCostSelfTests
{
    public static void Run()
    {
        try
        {
            AssertNear(75.0, HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(100.0, 0.25));
            AssertNear(100.0, HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(100.0, 0.0));
            AssertNear(0.0, HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(100.0, 1.0));
            AssertEqual("available", HourlyEnvironmentCostRules.DetermineStatus(4, 4, 0, 60.0, out var availableReason));
            AssertEqual(null, availableReason);
            AssertEqual("partial", HourlyEnvironmentCostRules.DetermineStatus(4, 3, 1, 60.0, out var partialReason));
            AssertEqual("some-road-samples-not-found", partialReason);
            AssertEqual("missing", HourlyEnvironmentCostRules.DetermineStatus(4, 0, 4, 60.0, out var missingReason));
            AssertEqual("road-surface-not-found", missingReason);
            AssertEqual("missing", HourlyEnvironmentCostRules.DetermineStatus(0, 0, 0, 60.0, out var zeroSampleReason));
            AssertEqual("road-surface-not-found", zeroSampleReason);
            AssertEqual("missing", HourlyEnvironmentCostRules.DetermineStatus(4, 4, 0, -1.0, out var nightReason));
            AssertEqual("sun-below-horizon", nightReason);
            AssertEqual("2025-08-01T08:00:00+09:00",
                HourlyEnvironmentCostRules.Timestamp(new DateTime(2025, 8, 1), 8, "Asia/Tokyo"));
            var noonSun = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 12,
                35.6916, 139.7365, "Asia/Tokyo");
            var nightSun = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 0,
                35.6916, 139.7365, "Asia/Tokyo");
            if (noonSun.elevationDegrees <= 0.0) throw new InvalidOperationException("Expected the noon sun to be above the horizon.");
            if (nightSun.elevationDegrees >= 0.0) throw new InvalidOperationException("Expected the midnight sun to be below the horizon.");
            AssertNear(1.0, noonSun.direction.magnitude);
            AssertNear(1.0, nightSun.direction.magnitude);
            // NOAA's general solar-position equations, using Ichigaya's analysis centre and JST.
            var referenceMorning = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 8.0,
                35.690470, 139.736043, "Asia/Tokyo");
            var referenceNoon = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 12.0,
                35.690470, 139.736043, "Asia/Tokyo");
            AssertNear(37.166051, referenceMorning.elevationDegrees, 0.05);
            AssertNear(93.458541, referenceMorning.azimuthDegrees, 0.05);
            AssertNear(72.317290, referenceNoon.elevationDegrees, 0.05);
            AssertNear(189.768435, referenceNoon.azimuthDegrees, 0.05);
            AssertThrows<ArgumentOutOfRangeException>(() => HourlyEnvironmentCostRules.CalculateSolarExposureSeconds(100.0, 1.1));
            AssertThrows<ArgumentException>(() => HourlyEnvironmentCostRules.DetermineStatus(4, 2, 1, 60.0, out _));
            AssertThrows<ArgumentOutOfRangeException>(() => HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 24,
                35.6916, 139.7365, "Asia/Tokyo"));
            AssertThrows<ArgumentOutOfRangeException>(() => HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 24.0,
                35.6916, 139.7365, "Asia/Tokyo"));
            AssertGridCodes(new[] { "53396530", "53396531" }, MeshCoverageAnalyzer.NormalizeGridCodes(new[] { "533965", "53396530", "53396531" }));
            AssertGridCodes(new[] { "533974", "533975" }, MeshCoverageAnalyzer.NormalizeGridCodes(new[] { "533974", "533975" }));
            AssertGridCodes(new[] { "53396530" }, MeshCoverageAnalyzer.NormalizeGridCodes(new[] { "533965", "invalid", "53396530", "53396530" }));
            AssertGridCodes(new[] { "53396500", "53396501", "53396510", "53396511" }, MeshPartitionPlanner.ExpandToThirdMeshes("533965").ToList());
            AssertGridCodes(new[] { "53396530" }, MeshPartitionPlanner.ExpandToThirdMeshes("53396530").ToList());
            var meshUnit = new MeshPartitionUnit { minLatitude = 35.0, minLongitude = 139.0, maxLatitude = 36.0, maxLongitude = 140.0 };
            AssertEqual(true, MeshPartitionPlanner.Owns(meshUnit, 35.0, 139.0));
            AssertEqual(false, MeshPartitionPlanner.Owns(meshUnit, 36.0, 139.5));
            var scenario = new EnvironmentCostPolicyScenario
            {
                id = "self-test-scenario",
                facilities = new System.Collections.Generic.List<EnvironmentCostPolicyFacility>
                {
                    new EnvironmentCostPolicyFacility { id = "tree-1", type = "tree", latitude = 35.0, longitude = 139.0 }
                }
            };
            scenario.Validate("self-test");
            if (string.IsNullOrWhiteSpace(scenario.Fingerprint())) throw new InvalidOperationException("Expected scenario fingerprint.");
            AssertThrows<InvalidOperationException>(() => new EnvironmentCostPolicyScenario { id = "invalid", recalculationScope = "affected" }.Validate("self-test"));
            var runtimePolicy = new EnvironmentCostRuntimePolicyScenario
            {
                id = "runtime-policy-self-test", areaId = "self-test-city", coordinateZoneId = 9,
                facilities = new System.Collections.Generic.List<EnvironmentCostRuntimePolicyFacility>
                {
                    new EnvironmentCostRuntimePolicyFacility { id = "tree-1", type = "tree", localPosition = new Vector3(10f, 0f, 20f) },
                    new EnvironmentCostRuntimePolicyFacility { id = "obstacle-1", type = "obstacle", localPosition = new Vector3(30f, 0f, 40f), heightMeters = 3.0, widthMeters = 2.0, depthMeters = 2.0 }
                }
            };
            runtimePolicy.Validate("self-test");
            if (string.IsNullOrWhiteSpace(runtimePolicy.Fingerprint())) throw new InvalidOperationException("Expected Runtime policy fingerprint.");
            var runtimePolicyJson = EnvironmentCostRuntimePolicyJson.Serialize(runtimePolicy);
            if (runtimePolicyJson.Contains("normalized")) throw new InvalidOperationException("Runtime policy JSON must serialize Vector3 as x/y/z fields only.");
            var restoredRuntimePolicy = EnvironmentCostRuntimePolicyJson.Deserialize<EnvironmentCostRuntimePolicyScenario>(runtimePolicyJson);
            AssertNear(10.0, restoredRuntimePolicy.facilities[0].localPosition.x);
            AssertNear(20.0, restoredRuntimePolicy.facilities[0].localPosition.z);
            AssertThrows<InvalidOperationException>(() => new EnvironmentCostRuntimePolicyScenario { id = "invalid", areaId = "self-test-city", facilities = new System.Collections.Generic.List<EnvironmentCostRuntimePolicyFacility> { new EnvironmentCostRuntimePolicyFacility { id = "bad", type = "tree", radiusMeters = 0.0 } } }.Validate("self-test"));
            var runtimePackage = new EnvironmentCostRuntimeCityPackageManifest
            {
                schemaVersion = "environment-cost-runtime-city-package-0.1",
                areaId = "self-test-city",
                version = "1.0.0",
                coordinateZoneId = 9,
                center = new[] { 139.0, 35.0 },
                radiusMeters = 100.0,
                inspectionSceneAssetPath = "Assets/Scenes/EnvironmentCostInspection/self-test-city.unity",
                scene = new EnvironmentCostRuntimeCityPackageScene { requiredLayers = new[] { new EnvironmentCostRuntimeCityPackageLayer { name = "Road", layer = 9, role = "walkable-surface" } } },
                files = new[] { new EnvironmentCostRuntimeCityPackageFile { relativePath = "road-network/topology.json", bytes = 1, sha256 = new string('a', 64) } }
            };
            runtimePackage.ValidateStructure();
            AssertEqual(true, EnvironmentCostRuntimeCityPackageManifest.IsSafeRelativePath("road-network/topology.json"));
            AssertEqual(false, EnvironmentCostRuntimeCityPackageManifest.IsSafeRelativePath("../outside.json"));
            var runtimeShadeInput = new EnvironmentCostRuntimeShadeAnalysisInput
            {
                schemaVersion = "environment-cost-runtime-shade-input-0.1", areaId = "self-test-city", center = new[] { 139.0, 35.0 },
                coordinateZoneId = 9, radiusMeters = 100f, analysisDate = "2025-08-01", timezone = "Asia/Tokyo", sampleSpacingMeters = 10f, pedestrianHeightMeters = 1.5f,
                edges = new[]
                {
                    new EnvironmentCostRuntimeShadeInputEdge { id = "edge-1", from = new[] { 0f, 0f }, to = new[] { 10f, 0f }, lengthMeters = 10.0, walkingSeconds = 10.0 },
                    new EnvironmentCostRuntimeShadeInputEdge { id = "edge-far", from = new[] { 80f, 80f }, to = new[] { 90f, 80f }, lengthMeters = 10.0, walkingSeconds = 10.0 }
                }
            };
            var physicalRuntimeInput = new EnvironmentCostRuntimeShadeAnalysisInput
            {
                schemaVersion = "environment-cost-runtime-shade-input-0.3", areaId = "self-test-city", center = new[] { 139.0, 35.0 },
                coordinateZoneId = 9, radiusMeters = 100f, analysisDate = "2025-08-01", timezone = "Asia/Tokyo", sampleSpacingMeters = 10f, pedestrianHeightMeters = 1.5f,
                graphFingerprintSha256 = new string('a', 64), quality = new EnvironmentCostRuntimeShadeInputQuality { qualityContractVersion = "pedestrian-network-safety-1.0", status = "accepted", explicitOrDerivedRatio = 1.0, fallbackRatio = 0.0, sourceSchemaVersion = "0.2", validationFailures = Array.Empty<string>(), validationWarnings = Array.Empty<string>() },
                edges = new[] { new EnvironmentCostRuntimeShadeInputEdge { id = "physical-1", physicalEdgeId = "physical-1", from = new[] { 0f, 0f }, to = new[] { 10f, 0f }, geometry = new[] { new[] { 0f, 0f }, new[] { 10f, 0f } }, lengthMeters = 10.0, walkingSeconds = 10.0 } }
            };
            physicalRuntimeInput.Validate();
            AssertEqual("accepted", EnvironmentCostRuntimeShadeAnalyzer.CreateResult(physicalRuntimeInput,
                new EnvironmentCostRuntimeShadeAnalysisRequest { analysisDate = new DateTime(2025, 8, 1), hours = new[] { 12 } }).provenance.networkQuality.status);
            physicalRuntimeInput.edges[0].physicalEdgeId = "";
            AssertThrows<InvalidOperationException>(() => physicalRuntimeInput.Validate());
            physicalRuntimeInput.edges[0].physicalEdgeId = "different-physical-edge";
            AssertThrows<InvalidOperationException>(() => physicalRuntimeInput.Validate());
            physicalRuntimeInput.edges[0].physicalEdgeId = physicalRuntimeInput.edges[0].id;
            physicalRuntimeInput.edges[0].geometry = null;
            AssertThrows<InvalidOperationException>(() => physicalRuntimeInput.Validate());
            physicalRuntimeInput.edges[0].geometry = new[] { new[] { 0f, 0f }, new[] { 10f, 0f } };
            var affectedEdges = EnvironmentCostRuntimePolicyImpact.FindAffectedEdgeIds(runtimeShadeInput,
                new EnvironmentCostRuntimeShadeAnalysisRequest { analysisDate = new DateTime(2025, 8, 1), hours = new[] { 12 } },
                new[] { new EnvironmentCostRuntimePolicyFacility { id = "changed-tree", type = "tree", localPosition = new Vector3(5f, 0f, 0f) } });
            AssertEqual(true, affectedEdges.Contains("edge-1"));
            AssertEqual(false, affectedEdges.Contains("edge-far"));
            AssertEqual(true, EnvironmentCostRuntimePolicyImpact.HasPotentiallyAffectedEdge(runtimeShadeInput,
                new DateTime(2025, 8, 1), new EnvironmentCostRuntimePolicyFacility { id = "near-output", type = "tree", localPosition = new Vector3(5f, 0f, 0f) }));
            AssertEqual(false, EnvironmentCostRuntimePolicyImpact.HasPotentiallyAffectedEdge(runtimeShadeInput,
                new DateTime(2025, 8, 1), new EnvironmentCostRuntimePolicyFacility { id = "outside-output", type = "tree", localPosition = new Vector3(1000f, 0f, 1000f) }));
            var runtimeEvidence = EnvironmentCostRuntimeShadeAnalyzer.CreateResult(runtimeShadeInput,
                new EnvironmentCostRuntimeShadeAnalysisRequest { analysisDate = new DateTime(2025, 8, 1), hours = new[] { 12 } });
            runtimeEvidence.message = null; // Null and empty text have the same persisted semantic meaning.
            runtimeEvidence.edges[0].hourly[0].shadeRatio = 0.6180339887498949;
            runtimeEvidence.edges[0].hourly[0].solarExposureSeconds = 18.654066884390497;
            runtimeEvidence.provenance.scenarioId = "runtime-policy-self-test";
            runtimeEvidence.provenance.policyFingerprintSha256 = runtimePolicy.Fingerprint();
            runtimeEvidence.provenance.recalculationScope = "局所再計算";
            runtimeEvidence.provenance.totalEdgeCount = runtimeShadeInput.edges.Length;
            runtimeEvidence.provenance.recalculatedEdgeCount = affectedEdges.Count;
            var runtimeEvidenceFingerprint = EnvironmentCostRuntimeShadeResultStore.CalculateSha256(runtimeEvidence);
            if (string.IsNullOrWhiteSpace(runtimeEvidenceFingerprint))
                throw new InvalidOperationException("Expected Runtime shade evidence fingerprint.");
            runtimeEvidence.provenance.resultFingerprintSha256 = runtimeEvidenceFingerprint;
            AssertEqual(runtimeEvidenceFingerprint, EnvironmentCostRuntimeShadeResultStore.CalculateSha256(runtimeEvidence));
            runtimeEvidence.provenance.resultFingerprintAlgorithm = EnvironmentCostRuntimeShadeResultStore.SemanticFingerprintAlgorithm;
            var serializedRuntimeEvidence = EnvironmentCostRuntimePolicyJson.Serialize(runtimeEvidence, Newtonsoft.Json.Formatting.Indented);
            var reloadedRuntimeEvidence = EnvironmentCostRuntimePolicyJson.Deserialize<EnvironmentCostRuntimeShadeAnalysisResult>(serializedRuntimeEvidence);
            AssertEqual(runtimeEvidenceFingerprint, EnvironmentCostRuntimeShadeResultStore.CalculateSha256(reloadedRuntimeEvidence));
            var temporaryScenarioId = "runtime-shade-store-self-test-" + Guid.NewGuid().ToString("N");
            var temporaryDirectory = EnvironmentCostRuntimeShadeResultStore.GetDirectory(runtimeEvidence.areaId, temporaryScenarioId);
            try
            {
                var persistedPath = EnvironmentCostRuntimeShadeResultStore.Save(runtimeEvidence, temporaryScenarioId);
                AssertEqual(true, File.Exists(persistedPath));
                var persistedRuntimeEvidence = EnvironmentCostRuntimeShadeResultStore.LoadForRouteComparison(persistedPath);
                AssertEqual(EnvironmentCostRuntimeShadeResultStore.SemanticFingerprintAlgorithm, persistedRuntimeEvidence.provenance.resultFingerprintAlgorithm);
                AssertEqual(runtimeEvidenceFingerprint, persistedRuntimeEvidence.provenance.resultFingerprintSha256);
                AssertEqual(runtimeEvidenceFingerprint, EnvironmentCostRuntimeShadeResultStore.CalculateSha256(persistedRuntimeEvidence));
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
            }
            reloadedRuntimeEvidence.edges[0].hourly[0].shadeRatio += 0.01;
            AssertEqual(false, runtimeEvidenceFingerprint == EnvironmentCostRuntimeShadeResultStore.CalculateSha256(reloadedRuntimeEvidence));
            var runtimeShadeResult = EnvironmentCostRuntimeShadeAnalyzer.Analyze(runtimeShadeInput,
                new EnvironmentCostRuntimeShadeAnalysisRequest { analysisDate = new DateTime(2025, 8, 1), hours = new[] { 12 } });
            AssertEqual("completed", runtimeShadeResult.status);
            AssertEqual("missing", runtimeShadeResult.edges[0].hourly[0].status);
            AssertEqual("road-surface-not-found", runtimeShadeResult.edges[0].hourly[0].exclusionReason);
            AssertRuntimeShadeRaycasts();
            AssertRuntimeRouteComparison();
            AssertRuntimeRouteComparisonWithLocalCityPackage();
            AssertRuntimeUiKeyboardFocusPolicy();
            AssertRuntimeUiDocumentInputGate();
            Debug.Log("HOURLY_ENVIRONMENT_COST_SELF_TEST_PASSED");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void AssertNear(double expected, double actual)
    {
        AssertNear(expected, actual, HourlyEnvironmentCostRules.FormulaToleranceSeconds);
    }

    private static void AssertNear(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }

    private static void AssertEqual(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected {expected ?? "<null>"}, actual {actual ?? "<null>"}.");
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
    }

    private static void AssertEqual(bool expected, bool actual)
    {
        if (expected != actual) throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }

    private static void AssertEqual(int expected, int actual)
    {
        if (expected != actual) throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }

    private static void AssertGridCodes(string[] expected, System.Collections.Generic.List<string> actual)
    {
        if (expected.Length != actual.Count || !expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected normalized grid codes: {string.Join(",", actual)}.");
        }
    }

    private static void AssertRuntimeShadeRaycasts()
    {
        var road = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            road.layer = 9;
            road.transform.position = new Vector3(5f, -0.5f, 0f);
            road.transform.localScale = new Vector3(30f, 1f, 6f);
            obstruction.layer = 8;
            var sun = HourlyEnvironmentCostRules.CalculateSun(new DateTime(2025, 8, 1), 12, 35.0, 139.0, "Asia/Tokyo");
            obstruction.transform.position = new Vector3(5f, 1.5f, 0f) + sun.direction * 5f;
            obstruction.transform.localScale = Vector3.one * 10f;
            Physics.SyncTransforms();
            var input = new EnvironmentCostRuntimeShadeAnalysisInput
            {
                schemaVersion = "environment-cost-runtime-shade-input-0.1", areaId = "self-test-city", center = new[] { 139.0, 35.0 },
                coordinateZoneId = 9, radiusMeters = 100f, analysisDate = "2025-08-01", timezone = "Asia/Tokyo", sampleSpacingMeters = 10f, pedestrianHeightMeters = 1.5f,
                edges = new[] { new EnvironmentCostRuntimeShadeInputEdge { id = "edge-rays", from = new[] { 0f, 0f }, to = new[] { 10f, 0f }, lengthMeters = 10.0, walkingSeconds = 10.0 } }
            };
            var result = EnvironmentCostRuntimeShadeAnalyzer.Analyze(input,
                new EnvironmentCostRuntimeShadeAnalysisRequest { analysisDate = new DateTime(2025, 8, 1), hours = new[] { 12 } });
            AssertEqual("available", result.edges[0].hourly[0].status);
            AssertNear(0.5, result.edges[0].hourly[0].shadeRatio);
            var physicalInput = new EnvironmentCostRuntimeShadeAnalysisInput
            {
                schemaVersion = "environment-cost-runtime-shade-input-0.3", areaId = "self-test-city", center = new[] { 139.0, 35.0 },
                coordinateZoneId = 9, radiusMeters = 100f, analysisDate = "2025-08-01", timezone = "Asia/Tokyo", sampleSpacingMeters = 10f, pedestrianHeightMeters = 1.5f,
                graphFingerprintSha256 = new string('a', 64), quality = new EnvironmentCostRuntimeShadeInputQuality { qualityContractVersion = "pedestrian-network-safety-1.0", status = "accepted", explicitOrDerivedRatio = 1.0, fallbackRatio = 0.0, sourceSchemaVersion = "0.2", validationFailures = Array.Empty<string>(), validationWarnings = Array.Empty<string>() },
                edges = new[] { new EnvironmentCostRuntimeShadeInputEdge { id = "physical-polyline", physicalEdgeId = "physical-polyline", from = new[] { 0f, 0f }, to = new[] { 10f, 10f }, geometry = new[] { new[] { 0f, 0f }, new[] { 10f, 0f }, new[] { 10f, 10f } }, lengthMeters = 20.0, walkingSeconds = 20.0 } }
            };
            road.transform.localScale = new Vector3(30f, 1f, 30f);
            Physics.SyncTransforms();
            var polylineResult = EnvironmentCostRuntimeShadeAnalyzer.Analyze(physicalInput,
                new EnvironmentCostRuntimeShadeAnalysisRequest { analysisDate = new DateTime(2025, 8, 1), hours = new[] { 12 } });
            AssertEqual(3, polylineResult.edges[0].hourly[0].sampleCount);
            AssertEqual(3, polylineResult.edges[0].hourly[0].validSampleCount);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(road);
            UnityEngine.Object.DestroyImmediate(obstruction);
        }
    }

    private static void AssertRuntimeRouteComparison()
    {
        var repositoryRoot = Directory.GetParent(Directory.GetParent(Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty)?.FullName ?? string.Empty)?.FullName;
        if (string.IsNullOrWhiteSpace(repositoryRoot)) throw new InvalidOperationException("Repository root could not be resolved for route fixture.");
        var fixtureRoot = Path.Combine(repositoryRoot, "data", "fixtures", "route-server-bundle-v1");
        var packageRoot = Path.Combine(Path.GetTempPath(), "environment-cost-runtime-route-self-test-" + Guid.NewGuid().ToString("N"));
        var roadRoot = Path.Combine(packageRoot, "road-network");
        Directory.CreateDirectory(roadRoot);
        try
        {
            foreach (var name in new[] { "manifest.json", "topology.json", "cost-12.json" })
                File.Copy(Path.Combine(fixtureRoot, name), Path.Combine(roadRoot, name));
            File.WriteAllText(Path.Combine(packageRoot, "manifest.json"),
                "{\"schemaVersion\":\"environment-cost-runtime-city-package-0.1\",\"areaId\":\"route-server-fixture\",\"version\":\"self-test\"}");

            var core = EnvironmentCostRuntimeRouteComparison.Load(packageRoot);
            AssertEqual("route-server-fixture", core.AreaId);
            var result = core.Compare(new EnvironmentCostRuntimeRouteComparisonRequest
            {
                areaId = "route-server-fixture",
                timestamp = "2025-08-01T12:00:00+09:00",
                start = new EnvironmentCostRuntimeRouteCoordinate { longitude = 139.735, latitude = 35.69, nodeIndex = -1 },
                end = new EnvironmentCostRuntimeRouteCoordinate { longitude = 139.736, latitude = 35.69, nodeIndex = -1 }
            }, null);
            AssertEqual(EnvironmentCostRuntimeRouteComparison.ResultSchema, result.schemaVersion);
            if (result.baseline.routes.Count != 3) throw new InvalidOperationException("Expected three Runtime route profiles.");
            AssertGridCodes(new[] { "osm-way-201-0:forward", "osm-way-202-0:forward" }, result.baseline.routes[0].edgeIds);
            AssertGridCodes(new[] { "osm-way-203-0:forward", "osm-way-204-0:forward" }, result.baseline.routes[1].edgeIds);
            AssertGridCodes(new[] { "osm-way-205-0:forward", "osm-way-206-0:forward" }, result.baseline.routes[2].edgeIds);
            AssertNear(200.0, result.baseline.routes[0].walkingSeconds);
            AssertNear(180.0, result.baseline.routes[0].solarExposureSeconds);
            AssertNear(230.0, result.baseline.routes[1].walkingSeconds);
            AssertNear(115.0, result.baseline.routes[1].solarExposureSeconds);
            AssertNear(300.0, result.baseline.routes[2].walkingSeconds);
            AssertNear(15.0, result.baseline.routes[2].solarExposureSeconds);
            AssertEqual(result.comparisonFingerprintSha256, EnvironmentCostRuntimeRouteComparison.CalculateComparisonFingerprint(result));

            var policyA = CreateFixtureRuntimeRouteResult(packageRoot, "policy-a", 1.0);
            var policyB = CreateFixtureRuntimeRouteResult(packageRoot, "policy-b", 0.0);
            var compared = core.Compare(result.conditions, null, policyA, policyB);
            if (compared.policies.Count != 2) throw new InvalidOperationException("Expected two Runtime policy comparisons.");
            AssertNear(0.0, compared.policies[0].routes[2].solarExposureSeconds);
            AssertNear(compared.policies[1].routes[2].walkingSeconds, compared.policies[1].routes[2].solarExposureSeconds);
            foreach (var policy in compared.policies)
            {
                if (policy.start.nodeIndex != compared.baseline.start.nodeIndex || policy.end.nodeIndex != compared.baseline.end.nodeIndex)
                    throw new InvalidOperationException("Runtime comparison must share snapped start/end nodes.");
            }
            AssertEqual(policyA.provenance.resultFingerprintSha256, compared.policies[0].scenario.resultFingerprintSha256);
            var roadHeatmap = core.CompareRoadHeatmap(new EnvironmentCostRuntimeRoadHeatmapComparisonRequest
            {
                areaId = compared.areaId, timestamp = compared.timestamp, metric = "shadeRatio"
            }, compared, policyA);
            AssertEqual(EnvironmentCostRuntimeRoadHeatmapComparison.ResultSchema, roadHeatmap.schemaVersion);
            AssertEqual(true, roadHeatmap.edges.Count > 0);
            AssertEqual(true, roadHeatmap.edges.All(edge => edge.status == "improved" || edge.status == "degraded" || edge.status == "unchanged" || edge.status == "partial" || edge.status == "missing"));
            AssertEqual(roadHeatmap.comparisonFingerprintSha256, EnvironmentCostRuntimeRoadHeatmapComparison.CalculateFingerprint(roadHeatmap));
            var environmentCostHeatmap = core.CompareRoadHeatmap(new EnvironmentCostRuntimeRoadHeatmapComparisonRequest
            {
                areaId = compared.areaId, timestamp = compared.timestamp, metric = "environmentCostSeconds", profileId = "shade", solarAvoidanceFactor = 2.0
            }, compared, policyA);
            AssertEqual("environmentCostSeconds", environmentCostHeatmap.metric);
            AssertNear(2.0, environmentCostHeatmap.solarAvoidanceFactor);
            AssertEqual(true, environmentCostHeatmap.edges.All(edge => edge.baselineValue < 0 || edge.baselineValue >= edge.walkingSeconds));
            AssertThrows<InvalidOperationException>(() => core.CompareRoadHeatmap(new EnvironmentCostRuntimeRoadHeatmapComparisonRequest
            {
                areaId = compared.areaId, timestamp = "2025-08-01T13:00:00+09:00", metric = "shadeRatio"
            }, compared, policyA));
            var evidence = new EnvironmentCostRuntimeRouteComparisonEvidence
            {
                generatedAtUtc = "2025-08-01T00:00:00Z", comparison = compared,
                policyScenarios = new System.Collections.Generic.List<EnvironmentCostRuntimePolicyScenario>
                {
                    new EnvironmentCostRuntimePolicyScenario { id = "policy-a", areaId = "route-server-fixture", coordinateZoneId = 9 },
                    new EnvironmentCostRuntimePolicyScenario { id = "policy-b", areaId = "route-server-fixture", coordinateZoneId = 9 }
                }
            };
            evidence.comparisonFingerprintSha256 = evidence.CalculateFingerprint();
            AssertEqual(evidence.comparisonFingerprintSha256, evidence.CalculateFingerprint());
            policyB.provenance.timezone = "UTC";
            AssertThrows<InvalidOperationException>(() => core.Compare(result.conditions, null, policyA, policyB));
        }
        finally
        {
            if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, true);
        }
    }

    private static void AssertRuntimeRouteComparisonWithLocalCityPackage()
    {
        var packageRoot = Path.Combine(Application.streamingAssetsPath, "EnvironmentCostCities", "ichigaya-venue");
        if (!Directory.Exists(packageRoot))
        {
            Debug.Log("ENVIRONMENT_COST_RUNTIME_ROUTE_CITY_PACKAGE_SKIPPED reason=package-not-found");
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var core = EnvironmentCostRuntimeRouteComparison.Load(packageRoot);
        var result = core.Compare(new EnvironmentCostRuntimeRouteComparisonRequest
        {
            areaId = "ichigaya-venue",
            timestamp = "2025-08-01T12:00:00+09:00",
            start = new EnvironmentCostRuntimeRouteCoordinate { longitude = 139.736043, latitude = 35.69047, nodeIndex = -1 },
            end = new EnvironmentCostRuntimeRouteCoordinate { longitude = 139.700556, latitude = 35.689606, nodeIndex = -1 }
        }, null);
        stopwatch.Stop();

        AssertEqual(true, result.baseline.start.nodeIndex == 43591);
        AssertEqual(true, result.baseline.end.nodeIndex == 76412);
        AssertEqual(true, result.baseline.routes.Count == 3);
        AssertEqual(true, result.baseline.routes[0].edgeIds.Count == 173);
        AssertEqual(true, result.baseline.routes[1].edgeIds.Count == 208);
        AssertEqual(true, result.baseline.routes[2].edgeIds.Count == 197);
        AssertNear(2593.5727713118204, result.baseline.routes[0].walkingSeconds, 0.000001);
        AssertNear(2697.583815912474, result.baseline.routes[1].walkingSeconds, 0.000001);
        AssertNear(2707.5896082504987, result.baseline.routes[2].walkingSeconds, 0.000001);
        AssertNear(0.34608912421242477, result.baseline.routes[0].observedShadeRatio, 0.000001);
        AssertNear(0.7210275379560118, result.baseline.routes[1].observedShadeRatio, 0.000001);
        AssertNear(0.7247973927581977, result.baseline.routes[2].observedShadeRatio, 0.000001);
        Debug.Log($"ENVIRONMENT_COST_RUNTIME_ROUTE_CITY_PACKAGE_PASSED elapsedMilliseconds={stopwatch.Elapsed.TotalMilliseconds:F1}");
    }

    private static EnvironmentCostRuntimeShadeAnalysisResult CreateFixtureRuntimeRouteResult(string packageRoot, string scenarioId, double shadeRatio)
    {
        var result = new EnvironmentCostRuntimeShadeAnalysisResult
        {
            schemaVersion = "environment-cost-runtime-shade-result-0.1",
            status = "completed",
            areaId = "route-server-fixture",
            generatedAtUtc = "2025-08-01T03:00:00Z",
            provenance = new EnvironmentCostRuntimeShadeAnalysisProvenance
            {
                areaId = "route-server-fixture", analysisDate = "2025-08-01", timezone = "Asia/Tokyo", hours = new[] { 12 },
                scenarioId = scenarioId, policyFingerprintSha256 = new string(scenarioId == "policy-a" ? 'a' : 'b', 64),
                cityPackageVersion = "self-test",
                cityPackageManifestSha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(Path.Combine(packageRoot, "manifest.json"))
            },
            edges = new System.Collections.Generic.List<EnvironmentCostRuntimeShadeEdgeResult>()
        };
        foreach (var suffix in new[] { "201", "202", "203", "204", "205", "206" })
        {
            result.edges.Add(new EnvironmentCostRuntimeShadeEdgeResult
            {
                id = $"osm-way-{suffix}-0",
                hourly = new[]
                {
                    new EnvironmentCostRuntimeShadeHourlyResult
                    {
                        hour = 12, timestamp = "2025-08-01T12:00:00+09:00", status = "available",
                        shadeRatio = shadeRatio, solarExposureSeconds = 1.0 - shadeRatio,
                        sampleCount = 1, validSampleCount = 1, noGroundSampleCount = 0
                    }
                }
            });
        }
        result.provenance.resultFingerprintSha256 = EnvironmentCostRuntimeShadeResultStore.CalculateSha256(result);
        return result;
    }

    private static void AssertRuntimeUiKeyboardFocusPolicy()
    {
        var root = new VisualElement();
        var button = new Button();
        var slider = new Slider();
        var sliderInt = new SliderInt();
        var toggle = new Toggle();
        var text = new TextField();
        var number = new FloatField();
        root.Add(button);
        root.Add(slider);
        root.Add(sliderInt);
        root.Add(toggle);
        root.Add(text);
        root.Add(number);

        EnvironmentCostRuntimeUiInputGate.DisableNonEditableKeyboardFocus(root);

        AssertEqual(false, button.focusable);
        AssertEqual(false, slider.focusable);
        AssertEqual(false, sliderInt.focusable);
        AssertEqual(false, toggle.focusable);
        AssertEqual(true, text.focusable);
        AssertEqual(true, number.focusable);
        AssertEqual(true, text.tabIndex < 0);
        AssertEqual(true, number.tabIndex < 0);
    }

    private static void AssertRuntimeUiDocumentInputGate()
    {
        var uiObject = new GameObject("Runtime UI input gate self-test");
        try
        {
            var document = uiObject.AddComponent<UIDocument>();
            document.panelSettings = Resources.Load<PanelSettings>("EnvironmentCostRuntimePanelSettings");
            if (document.panelSettings == null) throw new InvalidOperationException("Runtime PanelSettings is missing.");

            var documentRoot = document.rootVisualElement;
            var uiSurface = new VisualElement();
            var text = new TextField { value = "baseline" };
            documentRoot.Add(uiSurface);
            uiSurface.Add(text);
            EnvironmentCostRuntimeUiInputGate.TrackDocument(documentRoot, uiSurface);
            EnvironmentCostRuntimeUiInputGate.DisableNonEditableKeyboardFocus(documentRoot);

            // Simulate the panel restoring a field without an explicit click. Camera keys must
            // still be intercepted at the UIDocument root and must not edit the field.
            text.Focus();
            using (var key = KeyDownEvent.GetPooled('w', KeyCode.W, EventModifiers.None))
            {
                var target = documentRoot.panel?.focusController?.focusedElement as VisualElement ?? text;
                key.target = target;
                target.SendEvent(key);
                AssertEqual(true, key.isDefaultPrevented);
            }
            AssertEqual("baseline", text.value);

            // A direct pointer selection arms the field for ordinary text entry.
            using (var pointer = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0 }))
            {
                pointer.target = text;
                text.SendEvent(pointer);
            }
            text.Focus();
            AssertEqual(true, EnvironmentCostRuntimeUiInputGate.IsTextInputFocused);
            using (var key = KeyDownEvent.GetPooled('w', KeyCode.W, EventModifiers.None))
            {
                var target = documentRoot.panel?.focusController?.focusedElement as VisualElement ?? text;
                key.target = target;
                target.SendEvent(key);
                AssertEqual(false, key.isDefaultPrevented);
            }

            // Clicking a non-editable UI surface must immediately release any live text focus.
            using (var pointer = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0 }))
            {
                pointer.target = uiSurface;
                uiSurface.SendEvent(pointer);
            }
            AssertEqual(false, EnvironmentCostRuntimeUiInputGate.IsTextInputFocused);
            using (var key = KeyDownEvent.GetPooled('w', KeyCode.W, EventModifiers.None))
            {
                key.target = documentRoot;
                documentRoot.SendEvent(key);
                AssertEqual(true, key.isDefaultPrevented);
            }

            // The Player's coordinate fallback reports a click outside the visible UI as null.
            // This is the exact app-start -> world-click -> hold-W regression path.
            using (var pointer = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0 }))
            {
                pointer.target = text;
                text.SendEvent(pointer);
            }
            text.Focus();
            AssertEqual(true, EnvironmentCostRuntimeUiInputGate.IsTextInputFocused);
            EnvironmentCostRuntimeUiInputGate.HandlePointerSelection(null, false);
            AssertEqual(false, EnvironmentCostRuntimeUiInputGate.IsPointerOverUi);
            AssertEqual(false, EnvironmentCostRuntimeUiInputGate.IsTextInputFocused);
            using (var key = KeyDownEvent.GetPooled('w', KeyCode.W, EventModifiers.None))
            {
                key.target = documentRoot;
                documentRoot.SendEvent(key);
                AssertEqual(true, key.isDefaultPrevented);
            }
        }
        finally
        {
            var document = uiObject.GetComponent<UIDocument>();
            if (document != null) EnvironmentCostRuntimeUiInputGate.StopTracking(document.rootVisualElement);
            UnityEngine.Object.DestroyImmediate(uiObject);
        }
    }
}
