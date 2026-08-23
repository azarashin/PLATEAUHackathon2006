const TIMESTAMP = '2025-08-01T12:00:00+09:00'

function graphEdge(physicalEdgeId, fromNodeId, toNodeId, direction, coordinates, walkingSeconds) {
  return {
    id: `${physicalEdgeId}:${direction}`,
    physicalEdgeId,
    sourceEdgeIds: [physicalEdgeId],
    osmWayIds: [Number(physicalEdgeId.split('-')[2])],
    highways: ['footway'],
    fromNodeId,
    toNodeId,
    direction,
    coordinates,
    lengthMeters: walkingSeconds * 1.4,
    walkingSeconds,
  }
}

function bidirectional(physicalEdgeId, leftNodeId, rightNodeId, leftCoordinate, rightCoordinate, walkingSeconds) {
  return [
    graphEdge(physicalEdgeId, leftNodeId, rightNodeId, 'forward', [leftCoordinate, rightCoordinate], walkingSeconds),
    graphEdge(physicalEdgeId, rightNodeId, leftNodeId, 'backward', [rightCoordinate, leftCoordinate], walkingSeconds),
  ]
}

function environmentEdge(id, walkingSeconds, shadeRatio) {
  return {
    id,
    walkingSeconds,
    sampleCount: 2,
    validSampleCount: 2,
    noGroundSampleCount: 0,
    hourly: [{
      hour: 12,
      timestamp: TIMESTAMP,
      status: 'available',
      exclusionReason: null,
      sunElevationDegrees: 70,
      shadeRatio,
      solarExposureSeconds: walkingSeconds * (1 - shadeRatio),
    }],
  }
}

export function createRouteFixtureInputs() {
  const center = [139.7355, 35.6900]
  const coordinates = {
    start: [139.7350, 35.6900],
    short: [139.7355, 35.6902],
    balanced: [139.7355, 35.6900],
    shade: [139.7355, 35.6898],
    end: [139.7360, 35.6900],
    isolated: [139.7355, 35.6907],
  }
  const nodeIds = {
    start: 'osm-node-2001', short: 'osm-node-2002', balanced: 'osm-node-2003',
    shade: 'osm-node-2004', end: 'osm-node-2005', isolated: 'osm-node-2006',
  }
  const specifications = [
    ['osm-way-201-0', 'start', 'short', 100, 0.1],
    ['osm-way-202-0', 'short', 'end', 100, 0.1],
    ['osm-way-203-0', 'start', 'balanced', 115, 0.5],
    ['osm-way-204-0', 'balanced', 'end', 115, 0.5],
    ['osm-way-205-0', 'start', 'shade', 150, 0.95],
    ['osm-way-206-0', 'shade', 'end', 150, 0.95],
  ]
  const graph = {
    schemaVersion: 'pedestrian-road-network-1.0',
    areaId: 'route-server-fixture',
    generatedAt: '2026-08-23T07:00:00Z',
    graphFingerprintSha256: '4'.repeat(64),
    extent: { center, radiusMeters: 500 },
    coordinateSystems: {
      geographic: { epsg: 4326, axisOrder: ['longitude', 'latitude'] },
      unity: {
        japanPlaneRectangularZoneId: 9,
        epsg: 6677,
        coordinateSystem: 'EUN',
        referencePointGeographic: center,
      },
    },
    walking: { defaultSpeedMetersPerSecond: 1.4 },
    nodes: Object.entries(coordinates).map(([name, coordinate], index) => ({
      id: nodeIds[name], osmNodeId: 2001 + index, coordinate,
    })),
    edges: specifications.flatMap(([id, left, right, walkingSeconds]) => bidirectional(
      id, nodeIds[left], nodeIds[right], coordinates[left], coordinates[right], walkingSeconds,
    )),
  }
  const environment = {
    schemaVersion: 'environment-cost-analysis-0.2',
    status: 'completed',
    analysisKey: '5'.repeat(64),
    resultFingerprintSha256: '6'.repeat(64),
    areaId: graph.areaId,
    generatedAt: '2026-08-23T07:05:00Z',
    center,
    radiusMeters: 500,
    coordinateZoneId: 9,
    settings: {
      date: '2025-08-01', timezone: 'Asia/Tokyo', hours: [12],
      sampleSpacingMeters: 25, pedestrianHeightMeters: 1.5, walkingSpeedMetersPerSecond: 1.4,
    },
    edges: specifications.map(([id, , , walkingSeconds, shadeRatio]) => environmentEdge(id, walkingSeconds, shadeRatio)),
  }
  return { graph, environment, coordinates, timestamp: TIMESTAMP }
}
