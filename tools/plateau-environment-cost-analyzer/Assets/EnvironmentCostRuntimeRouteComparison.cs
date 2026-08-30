using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Runtime-only route comparison core.  It deliberately has no UnityEngine or Editor dependency so that
/// controllers can load a city package, apply a runtime shade result, and export its DTOs with JsonUtility.
/// </summary>
public sealed class EnvironmentCostRuntimeRouteComparison
{
    public const string TopologySchema = "environment-cost-server-topology-1.0";
    public const string BundleSchema = "environment-cost-server-bundle-1.0";
    public const string ResultSchema = "environment-cost-runtime-route-comparison-0.1";
    private const double Tolerance = 1e-9;
    private const double FormulaToleranceSeconds = 1e-6;
    private const double EarthRadiusMeters = 6371008.8;

    private readonly Package package;
    private readonly List<int>[] outgoing;
    private readonly object baselineCostCacheLock = new object();

    private EnvironmentCostRuntimeRouteComparison(Package package)
    {
        this.package = package;
        outgoing = new List<int>[package.nodes.Length];
        for (var index = 0; index < outgoing.Length; index++) outgoing[index] = new List<int>();
        for (var index = 0; index < package.directedEdges.Length; index++) outgoing[package.directedEdges[index].fromNodeIndex].Add(index);
    }

    /// <summary>Loads manifest.json, road-network/manifest.json, topology and its baseline cost slice.</summary>
    public static EnvironmentCostRuntimeRouteComparison Load(string cityPackageRoot)
    {
        if (string.IsNullOrWhiteSpace(cityPackageRoot)) throw new ArgumentException("A city package root is required.", nameof(cityPackageRoot));
        var cityManifestPath = Path.Combine(cityPackageRoot, "manifest.json");
        var cityManifest = ReadObject(cityManifestPath);
        var roadRoot = Path.Combine(cityPackageRoot, "road-network");
        var manifest = ReadObject(Path.Combine(roadRoot, "manifest.json"));
        if (!string.Equals((string)manifest["schemaVersion"], BundleSchema, StringComparison.Ordinal) ||
            !string.Equals((string)manifest["status"], "completed", StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime route package has an unsupported or incomplete road-network manifest.");
        var topologyReference = manifest["topology"] as JObject ?? throw new InvalidOperationException("Road-network topology reference is missing.");
        var topologyFile = RequiredString(topologyReference, "file");
        var topology = ReadObject(SafePath(roadRoot, topologyFile));
        var parsed = ParseTopology(topology);
        var area = manifest["area"] as JObject ?? throw new InvalidOperationException("Road-network area is missing.");
        var cityArea = (string)cityManifest["areaId"];
        if (!string.Equals(parsed.areaId, RequiredString(area, "areaId"), StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(cityArea) && !string.Equals(cityArea, parsed.areaId, StringComparison.Ordinal)))
            throw new InvalidOperationException("City package and road-network area IDs do not match.");
        var costFiles = new Dictionary<string, string>();
        var references = manifest["costSlices"] as JArray ?? throw new InvalidOperationException("Road-network cost slices are missing.");
        foreach (var token in references.OfType<JObject>())
        {
            var timestamp = RequiredString(token, "timestamp");
            costFiles.Add(timestamp, SafePath(roadRoot, RequiredString(token, "file")));
        }
        if (costFiles.Count == 0) throw new InvalidOperationException("Road-network contains no cost slice.");
        return new EnvironmentCostRuntimeRouteComparison(new Package
        {
            areaId = parsed.areaId, cityPackageVersion = (string)cityManifest["version"],
            cityPackageManifestSha256 = EnvironmentCostRuntimeCityPackageManifest.CalculateSha256(cityManifestPath),
            topologyFingerprintSha256 = RequiredString(topology, "contentFingerprintSha256"), graphFingerprintSha256 = RequiredString(topology, "graphFingerprintSha256"),
            bundleFingerprintSha256 = (string)manifest["bundleFingerprintSha256"],
            center = ReadCoordinate(area["center"] as JArray, "area.center"), radiusMeters = RequiredNumber(area, "radiusMeters"),
            referenceDate = (string)((manifest["scenario"] as JObject)?["referenceDate"]), timezone = (string)((manifest["scenario"] as JObject)?["timezone"]),
            nodes = parsed.nodes, physicalEdges = parsed.physicalEdges, directedEdges = parsed.directedEdges,
            physicalWalkingSeconds = parsed.physicalWalkingSeconds,
            baselineCostFiles = costFiles, baselineCosts = new Dictionary<string, Cost[]>(StringComparer.Ordinal)
        });
    }

    public string AreaId => package.areaId;
    public string TopologyFingerprintSha256 => package.topologyFingerprintSha256;
    public string[] AvailableTimestamps => new List<string>(package.baselineCostFiles.Keys).ToArray();

    public EnvironmentCostRuntimeRouteComparisonResult Compare(EnvironmentCostRuntimeRouteComparisonRequest request,
        EnvironmentCostRuntimeShadeAnalysisResult baseline, params EnvironmentCostRuntimeShadeAnalysisResult[] policies)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        ValidateRequest(request);
        if (policies != null && policies.Length > 2) throw new ArgumentException("At most two policy results can be compared.", nameof(policies));
        var baselineSource = CreateCostSource(baseline, request.timestamp, "baseline");
        var start = Snap(request.start);
        var end = Snap(request.end);
        var result = new EnvironmentCostRuntimeRouteComparisonResult
        {
            schemaVersion = ResultSchema, areaId = package.areaId, timestamp = request.timestamp,
            conditions = request, topologyFingerprintSha256 = package.topologyFingerprintSha256,
            graphFingerprintSha256 = package.graphFingerprintSha256, bundleFingerprintSha256 = package.bundleFingerprintSha256,
            cityPackageVersion = package.cityPackageVersion, cityPackageManifestSha256 = package.cityPackageManifestSha256,
            baseline = EvaluateScenario(baselineSource, request, start, end)
        };
        if (policies != null)
            foreach (var policy in policies) if (policy != null) result.policies.Add(EvaluateScenario(CreateCostSource(policy, request.timestamp, null), request, start, end));
        result.comparisonFingerprintSha256 = CalculateComparisonFingerprint(result);
        return result;
    }

    /// <summary>Routes one cost source; callers normally use Compare for a fixed baseline/policy condition set.</summary>
    public EnvironmentCostRuntimeRouteScenarioResult Route(EnvironmentCostRuntimeRouteComparisonRequest request,
        EnvironmentCostRuntimeShadeAnalysisResult scenario)
    {
        ValidateRequest(request);
        return EvaluateScenario(CreateCostSource(scenario, request.timestamp, null), request, Snap(request.start), Snap(request.end));
    }

    private EnvironmentCostRuntimeRouteScenarioResult EvaluateScenario(CostSource source, EnvironmentCostRuntimeRouteComparisonRequest request,
        EnvironmentCostRuntimeRouteSnap start, EnvironmentCostRuntimeRouteSnap end)
    {
        var output = new EnvironmentCostRuntimeRouteScenarioResult { scenario = source.provenance, start = start, end = end };
        foreach (var profile in request.Profiles()) output.routes.Add(RouteInternal(start.nodeIndex, end.nodeIndex, profile, source));
        return output;
    }

    public static string CalculateComparisonFingerprint(EnvironmentCostRuntimeRouteComparisonResult result)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        var previous = result.comparisonFingerprintSha256;
        result.comparisonFingerprintSha256 = null;
        try
        {
            using var sha = SHA256.Create();
            var json = JsonConvert.SerializeObject(result, Formatting.None);
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", string.Empty).ToLowerInvariant();
        }
        finally { result.comparisonFingerprintSha256 = previous; }
    }

    private CostSource CreateCostSource(EnvironmentCostRuntimeShadeAnalysisResult runtimeResult, string timestamp, string forcedId)
    {
        var baseline = GetBaselineCosts(timestamp);
        var source = new CostSource { costs = Clone(baseline), provenance = new EnvironmentCostRuntimeRouteScenarioProvenance { id = forcedId ?? "baseline", label = forcedId == "baseline" ? "現状" : "施策", kind = runtimeResult == null ? "package-baseline" : "runtime-shade" } };
        if (runtimeResult == null) return source;
        ValidateRuntimeResult(runtimeResult, timestamp);
        source.provenance.id = string.IsNullOrWhiteSpace(forcedId) ? runtimeResult.provenance.scenarioId : forcedId;
        source.provenance.policyFingerprintSha256 = runtimeResult.provenance.policyFingerprintSha256;
        source.provenance.resultFingerprintSha256 = runtimeResult.provenance.resultFingerprintSha256;
        source.provenance.generatedAtUtc = runtimeResult.generatedAtUtc;
        var bySourceId = new Dictionary<string, EnvironmentCostRuntimeShadeHourlyResult>(StringComparer.Ordinal);
        foreach (var edge in runtimeResult.edges ?? new List<EnvironmentCostRuntimeShadeEdgeResult>())
            foreach (var hourly in edge.hourly ?? Array.Empty<EnvironmentCostRuntimeShadeHourlyResult>())
                if (hourly.hour == HourFromTimestamp(timestamp)) bySourceId[edge.id] = hourly;
        for (var physicalIndex = 0; physicalIndex < package.physicalEdges.Length; physicalIndex++)
        {
            var physical = package.physicalEdges[physicalIndex];
            var statuses = new List<EnvironmentCostRuntimeShadeHourlyResult>();
            foreach (var sourceId in physical.sourceEdgeIds)
            {
                if (!bySourceId.TryGetValue(sourceId, out var hourly)) { statuses.Clear(); break; }
                statuses.Add(hourly);
            }
            if (statuses.Count == 0) { source.costs[physicalIndex] = Cost.Missing(); continue; }
            var validSampleCount = 0; var noGroundSampleCount = 0; var weightedShade = 0.0;
            foreach (var hourly in statuses)
            {
                if (hourly.validSampleCount < 0 || hourly.noGroundSampleCount < 0 || hourly.sampleCount < 0 ||
                    hourly.validSampleCount + hourly.noGroundSampleCount != hourly.sampleCount)
                    throw new InvalidOperationException($"Runtime shade sample counts are invalid: {physical.id}.");
                if (hourly.validSampleCount == 0)
                {
                    if (!string.Equals(hourly.status, "missing", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Runtime shade status is inconsistent: {physical.id}.");
                    noGroundSampleCount += hourly.noGroundSampleCount;
                    continue;
                }
                if (!double.IsFinite(hourly.shadeRatio) || hourly.shadeRatio < 0.0 || hourly.shadeRatio > 1.0)
                    throw new InvalidOperationException($"Runtime shade ratio is invalid: {physical.id}.");
                validSampleCount += hourly.validSampleCount;
                noGroundSampleCount += hourly.noGroundSampleCount;
                weightedShade += hourly.shadeRatio * hourly.validSampleCount;
            }
            if (validSampleCount == 0) { source.costs[physicalIndex] = Cost.Missing(); continue; }
            var walking = PhysicalWalkingSeconds(physicalIndex);
            var shade = Math.Max(0.0, Math.Min(1.0, weightedShade / validSampleCount));
            source.costs[physicalIndex] = new Cost { status = noGroundSampleCount > 0 ? "partial" : "available", shadeRatio = shade, solarExposureSeconds = walking * (1.0 - shade) };
        }
        return source;
    }

    private EnvironmentCostRuntimeRouteSnap Snap(EnvironmentCostRuntimeRouteCoordinate input)
    {
        if (input == null) throw new ArgumentException("Start and end must specify a coordinate or node index.");
        if (input.nodeIndex >= 0)
        {
            if (input.nodeIndex >= package.nodes.Length) throw new ArgumentException("Node index is out of range.");
            return SnapAt(input.nodeIndex, null, 0.0);
        }
        if (!IsCoordinate(input.longitude, input.latitude)) throw new ArgumentException("A WGS84 coordinate or node index is required.");
        if (Haversine(input.longitude, input.latitude, package.center.longitude, package.center.latitude) > package.radiusMeters + 250.0)
            throw new InvalidOperationException("The coordinate is outside the precomputed area.");
        var nearest = -1; var nearestDistance = double.PositiveInfinity;
        for (var index = 0; index < package.nodes.Length; index++)
        {
            var node = package.nodes[index]; var distance = Haversine(input.longitude, input.latitude, node.longitude, node.latitude);
            if (distance < nearestDistance - Tolerance || (Math.Abs(distance - nearestDistance) <= Tolerance && (nearest < 0 || index < nearest))) { nearest = index; nearestDistance = distance; }
        }
        if (nearest < 0 || nearestDistance > 250.0) throw new InvalidOperationException("No walkable node is within the snapping distance.");
        return SnapAt(nearest, input, nearestDistance);
    }

    private EnvironmentCostRuntimeRouteSnap SnapAt(int index, EnvironmentCostRuntimeRouteCoordinate input, double distance) => new EnvironmentCostRuntimeRouteSnap
    {
        nodeIndex = index, nodeId = "osm-node-" + package.nodes[index].sourceNodeId.ToString(CultureInfo.InvariantCulture),
        input = input, snapped = new EnvironmentCostRuntimeRouteCoordinate { longitude = package.nodes[index].longitude, latitude = package.nodes[index].latitude, nodeIndex = index }, distanceMeters = distance
    };

    private EnvironmentCostRuntimeRoute RouteInternal(int start, int end, EnvironmentCostRuntimeRouteProfile profile, CostSource source)
    {
        var distances = new double[package.nodes.Length]; var previous = new int[package.nodes.Length]; var visited = new bool[package.nodes.Length];
        for (var i = 0; i < distances.Length; i++) { distances[i] = double.PositiveInfinity; previous[i] = -1; }
        var queue = new BinaryHeap(); distances[start] = 0; queue.Push(start, 0);
        while (queue.Count > 0)
        {
            var item = queue.Pop(); if (item.priority > distances[item.node] + Tolerance || visited[item.node]) continue;
            visited[item.node] = true; if (item.node == end) break;
            foreach (var edgeIndex in outgoing[item.node])
            {
                var edge = package.directedEdges[edgeIndex]; if (visited[edge.toNodeIndex]) continue;
                var cost = Effective(source.costs[edge.physicalEdgeIndex], edge.walkingSeconds, profile.solarAvoidanceFactor);
                var candidate = distances[item.node] + cost.weight;
                if (candidate < distances[edge.toNodeIndex] - Tolerance) { distances[edge.toNodeIndex] = candidate; previous[edge.toNodeIndex] = edgeIndex; queue.Push(edge.toNodeIndex, candidate); }
            }
        }
        if (!Finite(distances[end])) throw new InvalidOperationException("No directed route connects the snapped nodes.");
        var indexes = new List<int>(); for (var node = end; node != start;) { var edge = previous[node]; if (edge < 0) throw new InvalidOperationException("Route reconstruction failed."); indexes.Add(edge); node = package.directedEdges[edge].fromNodeIndex; } indexes.Reverse();
        var route = new EnvironmentCostRuntimeRoute { profile = profile, routeCostSeconds = distances[end] };
        if (indexes.Count == 0) route.coordinates.Add(new EnvironmentCostRuntimeRouteCoordinate { longitude = package.nodes[start].longitude, latitude = package.nodes[start].latitude, nodeIndex = start });
        foreach (var edgeIndex in indexes)
        {
            var edge = package.directedEdges[edgeIndex]; var physical = package.physicalEdges[edge.physicalEdgeIndex]; var cost = Effective(source.costs[edge.physicalEdgeIndex], edge.walkingSeconds, profile.solarAvoidanceFactor);
            route.edgeIds.Add(physical.id + (edge.directionCode == 0 ? ":forward" : ":backward")); route.distanceMeters += edge.lengthMeters; route.walkingSeconds += edge.walkingSeconds; route.solarExposureSeconds += cost.solarExposureSeconds; route.observedSolarExposureSeconds += cost.observedSolarExposureSeconds; route.unknownWalkingSeconds += cost.unknownWalkingSeconds;
            if (cost.missing) route.missingEdgeCount++; if (cost.partial) route.partialEdgeCount++;
            if (route.coordinates.Count == 0) route.coordinates.Add(new EnvironmentCostRuntimeRouteCoordinate { longitude = package.nodes[edge.fromNodeIndex].longitude, latitude = package.nodes[edge.fromNodeIndex].latitude, nodeIndex = edge.fromNodeIndex });
            route.coordinates.Add(new EnvironmentCostRuntimeRouteCoordinate { longitude = package.nodes[edge.toNodeIndex].longitude, latitude = package.nodes[edge.toNodeIndex].latitude, nodeIndex = edge.toNodeIndex });
        }
        route.shadeRatio = route.walkingSeconds == 0 ? 0 : 1.0 - route.solarExposureSeconds / route.walkingSeconds;
        var known = route.walkingSeconds - route.unknownWalkingSeconds; route.observedShadeRatio = known <= 0 ? -1.0 : 1.0 - route.observedSolarExposureSeconds / known;
        route.coverageStatus = route.missingEdgeCount > 0 ? "missing" : route.partialEdgeCount > 0 ? "partial" : "available";
        return route;
    }

    private static EffectiveCost Effective(Cost cost, double walking, double factor)
    {
        var missing = cost == null || cost.status == "missing"; var solar = missing ? walking : cost.solarExposureSeconds;
        return new EffectiveCost { weight = walking + solar * factor, solarExposureSeconds = solar, observedSolarExposureSeconds = missing ? 0 : solar, unknownWalkingSeconds = missing ? walking : 0, missing = missing, partial = !missing && cost.status == "partial" };
    }

    private void ValidateRequest(EnvironmentCostRuntimeRouteComparisonRequest request)
    {
        if (!string.Equals(request.areaId, package.areaId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(request.timestamp)) throw new ArgumentException("Route request area or timestamp is invalid.");
        if (!package.baselineCostFiles.ContainsKey(request.timestamp)) throw new ArgumentException("The requested timestamp is unavailable.");
        foreach (var profile in request.Profiles())
            if (string.IsNullOrWhiteSpace(profile.id) || !Finite(profile.solarAvoidanceFactor) || profile.solarAvoidanceFactor < 0 || profile.solarAvoidanceFactor > 100)
                throw new ArgumentException("Route profile is invalid.");
    }

    private void ValidateRuntimeResult(EnvironmentCostRuntimeShadeAnalysisResult result, string timestamp)
    {
        var hour = HourFromTimestamp(timestamp);
        if (!string.Equals(result.status, "completed", StringComparison.Ordinal) || !string.Equals(result.areaId, package.areaId, StringComparison.Ordinal) || result.provenance == null ||
            !string.Equals(result.provenance.areaId, package.areaId, StringComparison.Ordinal) || !string.Equals(result.provenance.analysisDate, DateFromTimestamp(timestamp), StringComparison.Ordinal) ||
            !string.Equals(result.provenance.timezone, package.timezone, StringComparison.Ordinal)) throw new InvalidOperationException("Runtime shade result does not match the city package or requested conditions.");
        if (!string.Equals(result.provenance.cityPackageVersion, package.cityPackageVersion, StringComparison.Ordinal) ||
            !string.Equals(result.provenance.cityPackageManifestSha256, package.cityPackageManifestSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime shade result city package version or fingerprint does not match.");
        if (result.provenance.hours == null || !result.provenance.hours.Contains(hour))
            throw new InvalidOperationException("Runtime shade result does not contain the requested hour.");
        if (string.IsNullOrWhiteSpace(result.provenance.scenarioId) || !IsSha256(result.provenance.policyFingerprintSha256) ||
            !IsSha256(result.provenance.resultFingerprintSha256) || string.IsNullOrWhiteSpace(result.generatedAtUtc))
            throw new InvalidOperationException("Runtime shade result provenance is incomplete.");
    }

    private double PhysicalWalkingSeconds(int physicalIndex) => package.physicalWalkingSeconds[physicalIndex];
    private Cost[] GetBaselineCosts(string timestamp)
    {
        lock (baselineCostCacheLock)
        {
            if (package.baselineCosts.TryGetValue(timestamp, out var cached)) return cached;
            if (!package.baselineCostFiles.TryGetValue(timestamp, out var path)) throw new InvalidOperationException("The requested timestamp is not in the city package.");
            var parsed = new ParsedTopology { areaId = package.areaId, contentFingerprintSha256 = package.topologyFingerprintSha256,
                nodes = package.nodes, physicalEdges = package.physicalEdges, directedEdges = package.directedEdges,
                physicalWalkingSeconds = package.physicalWalkingSeconds };
            var loaded = ParseCostSlice(ReadObject(path), parsed, timestamp);
            package.baselineCosts.Add(timestamp, loaded);
            return loaded;
        }
    }
    private static Cost[] Clone(Cost[] original) { var copy = new Cost[original.Length]; for (var i = 0; i < copy.Length; i++) copy[i] = original[i].Copy(); return copy; }
    private static int HourFromTimestamp(string timestamp) => DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture).Hour;
    private static string DateFromTimestamp(string timestamp) => DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool IsSha256(string value) => value != null && value.Length == 64 && value.All(character =>
        (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
    private static bool IsCoordinate(double longitude, double latitude) => Finite(longitude) && Finite(latitude) && longitude >= -180 && longitude <= 180 && latitude >= -90 && latitude <= 90;
    private static double Haversine(double lon1, double lat1, double lon2, double lat2) { var p = Math.PI / 180.0; var dlat = (lat2 - lat1) * p; var dlon = (lon2 - lon1) * p; var h = Math.Sin(dlat / 2) * Math.Sin(dlat / 2) + Math.Cos(lat1 * p) * Math.Cos(lat2 * p) * Math.Sin(dlon / 2) * Math.Sin(dlon / 2); return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h))); }
    private static JObject ReadObject(string path)
    {
        using var stream = File.OpenText(path);
        using var reader = new JsonTextReader(stream) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }
    private static string SafePath(string root, string relative) { var full = Path.GetFullPath(Path.Combine(root, relative)); var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Road-network path escapes its package."); return full; }
    private static string RequiredString(JObject value, string name) => (string)value[name] ?? throw new InvalidOperationException("Missing required field: " + name);
    private static double RequiredNumber(JObject value, string name) => (double?)value[name] ?? throw new InvalidOperationException("Missing required number: " + name);
    private static EnvironmentCostRuntimeRouteCoordinate ReadCoordinate(JArray value, string name) { if (value == null || value.Count != 2) throw new InvalidOperationException("Invalid coordinate: " + name); var c = new EnvironmentCostRuntimeRouteCoordinate { longitude = (double?)value[0] ?? double.NaN, latitude = (double?)value[1] ?? double.NaN, nodeIndex = -1 }; if (!IsCoordinate(c.longitude, c.latitude)) throw new InvalidOperationException("Invalid coordinate: " + name); return c; }

    private static ParsedTopology ParseTopology(JObject topology)
    {
        if (!string.Equals((string)topology["schemaVersion"], TopologySchema, StringComparison.Ordinal)) throw new InvalidOperationException("Unsupported topology schema.");
        var nodesArray = topology["nodes"] as JArray; var physicalArray = topology["physicalEdges"] as JArray; var directedArray = topology["directedEdges"] as JArray; var counts = topology["counts"] as JObject;
        if (nodesArray == null || physicalArray == null || directedArray == null || counts == null || nodesArray.Count != (int?)counts["nodeCount"] || physicalArray.Count != (int?)counts["physicalEdgeCount"] || directedArray.Count != (int?)counts["directedEdgeCount"]) throw new InvalidOperationException("Topology counts do not match packed arrays.");
        var nodes = new Node[nodesArray.Count]; var nodeIds = new HashSet<long>(); for (var i = 0; i < nodes.Length; i++) { var item = nodesArray[i] as JArray; if (item == null || item.Count != 3) throw new InvalidOperationException("Invalid packed node."); var id = (long?)item[0] ?? long.MinValue; var coordinate = ReadCoordinate(new JArray(item[1], item[2]), "node"); if (!nodeIds.Add(id)) throw new InvalidOperationException("Duplicate source node."); nodes[i] = new Node { sourceNodeId = id, longitude = coordinate.longitude, latitude = coordinate.latitude }; }
        var physical = new PhysicalEdge[physicalArray.Count]; var physicalIds = new HashSet<string>(); for (var i = 0; i < physical.Length; i++) { var item = physicalArray[i] as JArray; var sources = item?[1] as JArray; var id = item?.Count == 2 ? (string)item[0] : null; if (string.IsNullOrWhiteSpace(id) || sources == null || sources.Count == 0 || !physicalIds.Add(id)) throw new InvalidOperationException("Invalid packed physical edge."); physical[i] = new PhysicalEdge { id = id, sourceEdgeIds = sources.Values<string>().ToArray() }; }
        var directed = new DirectedEdge[directedArray.Count]; for (var i = 0; i < directed.Length; i++) { var item = directedArray[i] as JArray; if (item == null || item.Count != 6) throw new InvalidOperationException("Invalid packed directed edge."); var edge = new DirectedEdge { physicalEdgeIndex = (int?)item[0] ?? -1, fromNodeIndex = (int?)item[1] ?? -1, toNodeIndex = (int?)item[2] ?? -1, directionCode = (int?)item[3] ?? -1, lengthMeters = (double?)item[4] ?? -1, walkingSeconds = (double?)item[5] ?? -1 }; if (edge.physicalEdgeIndex < 0 || edge.physicalEdgeIndex >= physical.Length || edge.fromNodeIndex < 0 || edge.fromNodeIndex >= nodes.Length || edge.toNodeIndex < 0 || edge.toNodeIndex >= nodes.Length || edge.fromNodeIndex == edge.toNodeIndex || (edge.directionCode != 0 && edge.directionCode != 1) || edge.lengthMeters <= 0 || edge.walkingSeconds <= 0) throw new InvalidOperationException("Invalid packed directed edge values."); directed[i] = edge; }
        var walkingByPhysical = new double[physical.Length];
        for (var index = 0; index < walkingByPhysical.Length; index++) walkingByPhysical[index] = -1;
        foreach (var edge in directed)
        {
            var walking = walkingByPhysical[edge.physicalEdgeIndex];
            if (walking < 0) walkingByPhysical[edge.physicalEdgeIndex] = edge.walkingSeconds;
            else if (Math.Abs(walking - edge.walkingSeconds) > Tolerance) throw new InvalidOperationException("Walking time differs by direction for a physical edge.");
        }
        foreach (var walking in walkingByPhysical) if (walking <= 0) throw new InvalidOperationException("A physical edge has no directed edge.");
        return new ParsedTopology { areaId = RequiredString(topology, "areaId"), contentFingerprintSha256 = RequiredString(topology, "contentFingerprintSha256"), nodes = nodes, physicalEdges = physical, directedEdges = directed, physicalWalkingSeconds = walkingByPhysical };
    }

    private static Cost[] ParseCostSlice(JObject slice, ParsedTopology topology, string expectedTimestamp)
    {
        var costs = slice["costs"] as JArray;
        if (!string.Equals((string)slice["schemaVersion"], "environment-cost-server-cost-slice-1.0", StringComparison.Ordinal) || !string.Equals((string)slice["areaId"], topology.areaId, StringComparison.Ordinal) || !string.Equals((string)slice["timestamp"], expectedTimestamp, StringComparison.Ordinal) || !string.Equals((string)slice["topologyContentFingerprintSha256"], topology.contentFingerprintSha256, StringComparison.Ordinal) || (int?)slice["physicalEdgeCount"] != topology.physicalEdges.Length || costs == null || costs.Count != topology.physicalEdges.Length) throw new InvalidOperationException("Invalid cost slice.");
        var values = new Cost[costs.Count]; for (var i = 0; i < values.Length; i++) { var item = costs[i] as JArray; if (item == null || item.Count != 6) throw new InvalidOperationException("Invalid packed cost."); var code = (int?)item[0] ?? -1; var sample = (int?)item[1] ?? -1; var valid = (int?)item[2] ?? -1; var noGround = (int?)item[3] ?? -1; if (code < 0 || code > 2 || sample < 0 || valid < 0 || noGround < 0 || valid + noGround != sample) throw new InvalidOperationException("Invalid packed cost coverage."); if (code == 0) { if (item[4]?.Type != JTokenType.Null || item[5]?.Type != JTokenType.Null || valid != 0) throw new InvalidOperationException("Invalid missing packed cost."); values[i] = Cost.Missing(); } else { var shade = (double?)item[4] ?? double.NaN; var exposure = (double?)item[5] ?? double.NaN; if (!Finite(shade) || shade < 0 || shade > 1 || !Finite(exposure) || exposure < 0 || Math.Abs(exposure - topology.physicalWalkingSeconds[i] * (1.0 - shade)) > FormulaToleranceSeconds) throw new InvalidOperationException("Invalid calculated packed cost."); values[i] = new Cost { status = code == 1 ? "partial" : "available", shadeRatio = shade, solarExposureSeconds = exposure }; } }
        return values;
    }

    private sealed class Package { public string areaId, cityPackageVersion, cityPackageManifestSha256, topologyFingerprintSha256, graphFingerprintSha256, bundleFingerprintSha256, referenceDate, timezone; public EnvironmentCostRuntimeRouteCoordinate center; public double radiusMeters; public Node[] nodes; public PhysicalEdge[] physicalEdges; public DirectedEdge[] directedEdges; public double[] physicalWalkingSeconds; public Dictionary<string, string> baselineCostFiles; public Dictionary<string, Cost[]> baselineCosts; }
    private sealed class ParsedTopology { public string areaId, contentFingerprintSha256; public Node[] nodes; public PhysicalEdge[] physicalEdges; public DirectedEdge[] directedEdges; public double[] physicalWalkingSeconds; }
    private sealed class Node { public long sourceNodeId; public double longitude, latitude; }
    private sealed class PhysicalEdge { public string id; public string[] sourceEdgeIds; }
    private sealed class DirectedEdge { public int physicalEdgeIndex, fromNodeIndex, toNodeIndex, directionCode; public double lengthMeters, walkingSeconds; }
    private sealed class Cost { public string status; public double shadeRatio, solarExposureSeconds; public static Cost Missing() => new Cost { status = "missing", shadeRatio = -1, solarExposureSeconds = -1 }; public Cost Copy() => new Cost { status = status, shadeRatio = shadeRatio, solarExposureSeconds = solarExposureSeconds }; }
    private sealed class CostSource { public Cost[] costs; public EnvironmentCostRuntimeRouteScenarioProvenance provenance; }
    private sealed class EffectiveCost { public double weight, solarExposureSeconds, observedSolarExposureSeconds, unknownWalkingSeconds; public bool missing, partial; }
    private sealed class BinaryHeap { private readonly List<HeapItem> values = new List<HeapItem>(); public int Count => values.Count; public void Push(int node, double priority) { values.Add(new HeapItem { node = node, priority = priority }); for (var i = values.Count - 1; i > 0;) { var p = (i - 1) / 2; if (!Less(values[i], values[p])) break; Swap(i, p); i = p; } } public HeapItem Pop() { var result = values[0]; var last = values[values.Count - 1]; values.RemoveAt(values.Count - 1); if (values.Count > 0) { values[0] = last; for (var i = 0;;) { var l = i * 2 + 1; var r = l + 1; var s = i; if (l < values.Count && Less(values[l], values[s])) s = l; if (r < values.Count && Less(values[r], values[s])) s = r; if (s == i) break; Swap(i, s); i = s; } } return result; } private static bool Less(HeapItem a, HeapItem b) { var d = a.priority - b.priority; return d < 0 || (d == 0 && a.node < b.node); } private void Swap(int a, int b) { var value = values[a]; values[a] = values[b]; values[b] = value; } } private struct HeapItem { public int node; public double priority; }
}

[Serializable] public sealed class EnvironmentCostRuntimeRouteComparisonRequest { public string areaId; public string timestamp; public EnvironmentCostRuntimeRouteCoordinate start; public EnvironmentCostRuntimeRouteCoordinate end; public EnvironmentCostRuntimeRouteProfile[] profiles; public EnvironmentCostRuntimeRouteProfile[] Profiles() => profiles == null || profiles.Length == 0 ? new[] { new EnvironmentCostRuntimeRouteProfile { id = "shortest", solarAvoidanceFactor = 0 }, new EnvironmentCostRuntimeRouteProfile { id = "balanced", solarAvoidanceFactor = .5 }, new EnvironmentCostRuntimeRouteProfile { id = "shade", solarAvoidanceFactor = 2 } } : profiles; }
[Serializable] public sealed class EnvironmentCostRuntimeRouteCoordinate { public double longitude; public double latitude; public int nodeIndex = -1; }
[Serializable] public sealed class EnvironmentCostRuntimeRouteProfile { public string id; public double solarAvoidanceFactor; }
[Serializable] public sealed class EnvironmentCostRuntimeRouteComparisonResult { public string schemaVersion, areaId, timestamp, topologyFingerprintSha256, graphFingerprintSha256, bundleFingerprintSha256, cityPackageVersion, cityPackageManifestSha256, comparisonFingerprintSha256; public EnvironmentCostRuntimeRouteComparisonRequest conditions; public EnvironmentCostRuntimeRouteScenarioResult baseline; public List<EnvironmentCostRuntimeRouteScenarioResult> policies = new List<EnvironmentCostRuntimeRouteScenarioResult>(); }
[Serializable] public sealed class EnvironmentCostRuntimeRouteScenarioResult { public EnvironmentCostRuntimeRouteScenarioProvenance scenario; public EnvironmentCostRuntimeRouteSnap start, end; public List<EnvironmentCostRuntimeRoute> routes = new List<EnvironmentCostRuntimeRoute>(); }
[Serializable] public sealed class EnvironmentCostRuntimeRouteScenarioProvenance { public string id, label, kind, policyFingerprintSha256, resultFingerprintSha256, generatedAtUtc; }
[Serializable] public sealed class EnvironmentCostRuntimeRouteSnap { public int nodeIndex; public string nodeId; public EnvironmentCostRuntimeRouteCoordinate input, snapped; public double distanceMeters; }
[Serializable] public sealed class EnvironmentCostRuntimeRoute { public EnvironmentCostRuntimeRouteProfile profile; public List<string> edgeIds = new List<string>(); public List<EnvironmentCostRuntimeRouteCoordinate> coordinates = new List<EnvironmentCostRuntimeRouteCoordinate>(); public double distanceMeters, walkingSeconds, solarExposureSeconds, observedSolarExposureSeconds, unknownWalkingSeconds, shadeRatio, observedShadeRatio, routeCostSeconds; public int missingEdgeCount, partialEdgeCount; public string coverageStatus; }
